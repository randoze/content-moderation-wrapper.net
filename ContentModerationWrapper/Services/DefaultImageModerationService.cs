using System.Diagnostics;
using ContentModeration.Abstractions;
using ContentModeration.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SkiaSharp;

namespace ContentModeration.Services;

/// <summary>
/// Default implementation of <see cref="IImageModerationService"/> that orchestrates configured
/// <see cref="IImageModerationProvider"/> engines (Azure AI Content Safety, Open-Source ONNX, or Hybrid),
/// automatic fallback, and threshold classification.
/// </summary>
public sealed class DefaultImageModerationService : IImageModerationService
{
    private static readonly HashSet<string> ExplicitNudityLabels = new(StringComparer.OrdinalIgnoreCase)
    {
        "FEMALE_GENITALIA_EXPOSED",
        "MALE_GENITALIA_EXPOSED",
        "FEMALE_BREAST_EXPOSED",
        "ANUS_EXPOSED",
        "BUTTOCKS_EXPOSED"
    };

    private static readonly HashSet<string> PotentiallyMatureLabels = new(StringComparer.OrdinalIgnoreCase)
    {
        "GORE",
        "VIOLENCE",
        "MATURE_CONTENT",
        "SUGGESTIVE_CONTENT",
        "FEMALE_GENITALIA_COVERED",
        "FEMALE_BREAST_COVERED",
        "BUTTOCKS_COVERED",
        "ANUS_COVERED",
        "BELLY_EXPOSED",
        "MALE_BREAST_EXPOSED"
    };

    private readonly IReadOnlyDictionary<ModerationProvider, IImageModerationProvider> _providers;
    private readonly IOptionsMonitor<ImageModerationOptions> _optionsMonitor;
    private readonly ILogger<DefaultImageModerationService> _logger;

    public DefaultImageModerationService(
        IEnumerable<IImageModerationProvider> providers,
        IOptionsMonitor<ImageModerationOptions> optionsMonitor,
        ILogger<DefaultImageModerationService> logger)
    {
        ArgumentNullException.ThrowIfNull(providers);
        _optionsMonitor = optionsMonitor ?? throw new ArgumentNullException(nameof(optionsMonitor));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

        var dict = new Dictionary<ModerationProvider, IImageModerationProvider>();
        foreach (var provider in providers)
        {
            dict[provider.ProviderType] = provider;
        }
        _providers = dict;
    }

    public IReadOnlyDictionary<string, bool> GetProviderAvailability()
    {
        var availability = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        foreach (var provider in _providers.Values)
        {
            availability[provider.ProviderName] = provider.IsAvailable;
        }
        return availability;
    }

    public async Task<ImageModerationResult> AnalyzeAsync(
        Stream imageStream,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(imageStream);
        var options = _optionsMonitor.CurrentValue;

        using var ms = new MemoryStream();
        var buffer = new byte[64 * 1024];
        long totalRead = 0;

        while (true)
        {
            var read = await imageStream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }

            totalRead += read;
            if (totalRead > options.MaxFileSizeBytes)
            {
                throw new InvalidDataException(
                    $"Image stream exceeds the maximum allowed size of {options.MaxFileSizeBytes / (1024 * 1024)} MB.");
            }

            ms.Write(buffer, 0, read);
        }

        return await AnalyzeAsync(ms.ToArray(), cancellationToken).ConfigureAwait(false);
    }

    public async Task<ImageModerationResult> AnalyzeAsync(
        byte[] imageBytes,
        CancellationToken cancellationToken = default)
    {
        var stopwatch = Stopwatch.StartNew();
        var options = _optionsMonitor.CurrentValue;

        ValidateImageBytes(imageBytes, options);

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(Math.Max(1, options.TimeoutSeconds)));
        var token = timeoutCts.Token;

        var detections = new List<ImageDetection>();
        var providersUsed = new List<string>();
        var errors = new List<string>();
        var fallbackUsed = false;

        switch (options.Provider)
        {
            case ModerationProvider.Azure:
            case ModerationProvider.OpenSource:
            {
                var primaryResult = await ExecuteSingleProviderAsync(options.Provider, imageBytes, token)
                    .ConfigureAwait(false);

                if (primaryResult.Succeeded)
                {
                    providersUsed.Add(primaryResult.ProviderName);
                    detections.AddRange(primaryResult.Detections);
                }
                else
                {
                    if (!string.IsNullOrWhiteSpace(primaryResult.ErrorMessage))
                    {
                        errors.Add(primaryResult.ErrorMessage);
                    }

                    if (options.EnableFallback &&
                        options.FallbackProvider.HasValue &&
                        options.FallbackProvider.Value != options.Provider &&
                        options.FallbackProvider.Value != ModerationProvider.Hybrid)
                    {
                        var fallbackType = options.FallbackProvider.Value;
                        _logger.LogWarning(
                            "Primary provider {Primary} failed ({Error}). Falling back to {Fallback}.",
                            options.Provider,
                            primaryResult.ErrorMessage,
                            fallbackType);

                        var fallbackResult = await ExecuteSingleProviderAsync(fallbackType, imageBytes, token)
                            .ConfigureAwait(false);

                        if (fallbackResult.Succeeded)
                        {
                            fallbackUsed = true;
                            providersUsed.Add(fallbackResult.ProviderName);
                            detections.AddRange(fallbackResult.Detections);
                        }
                        else if (!string.IsNullOrWhiteSpace(fallbackResult.ErrorMessage))
                        {
                            errors.Add(fallbackResult.ErrorMessage);
                        }
                    }
                }
                break;
            }

            case ModerationProvider.Hybrid:
            default:
            {
                if (options.SkipAzureWhenLocalBlocks)
                {
                    var localResult = await ExecuteSingleProviderAsync(ModerationProvider.OpenSource, imageBytes, token)
                        .ConfigureAwait(false);

                    if (localResult.Succeeded)
                    {
                        providersUsed.Add(localResult.ProviderName);
                        detections.AddRange(localResult.Detections);
                    }
                    else if (!string.IsNullOrWhiteSpace(localResult.ErrorMessage))
                    {
                        errors.Add(localResult.ErrorMessage);
                    }

                    var (_, localClassification) = ClassifyDetections(detections, options);
                    if (localClassification != ImageModerationClassification.Explicit)
                    {
                        var azureResult = await ExecuteSingleProviderAsync(ModerationProvider.Azure, imageBytes, token)
                            .ConfigureAwait(false);

                        if (azureResult.Succeeded)
                        {
                            providersUsed.Add(azureResult.ProviderName);
                            detections.AddRange(azureResult.Detections);
                        }
                        else if (!string.IsNullOrWhiteSpace(azureResult.ErrorMessage))
                        {
                            errors.Add(azureResult.ErrorMessage);
                            if (localResult.Succeeded && options.EnableFallback)
                            {
                                fallbackUsed = true;
                            }
                        }
                    }
                }
                else
                {
                    var localTask = ExecuteSingleProviderAsync(ModerationProvider.OpenSource, imageBytes, token);
                    var azureTask = ExecuteSingleProviderAsync(ModerationProvider.Azure, imageBytes, token);

                    await Task.WhenAll(localTask, azureTask).ConfigureAwait(false);

                    var localResult = await localTask.ConfigureAwait(false);
                    var azureResult = await azureTask.ConfigureAwait(false);

                    if (localResult.Succeeded)
                    {
                        providersUsed.Add(localResult.ProviderName);
                        detections.AddRange(localResult.Detections);
                    }
                    else if (!string.IsNullOrWhiteSpace(localResult.ErrorMessage))
                    {
                        errors.Add(localResult.ErrorMessage);
                    }

                    if (azureResult.Succeeded)
                    {
                        providersUsed.Add(azureResult.ProviderName);
                        detections.AddRange(azureResult.Detections);
                    }
                    else if (!string.IsNullOrWhiteSpace(azureResult.ErrorMessage))
                    {
                        errors.Add(azureResult.ErrorMessage);
                        if (localResult.Succeeded && options.EnableFallback)
                        {
                            fallbackUsed = true;
                        }
                    }
                }
                break;
            }
        }

        stopwatch.Stop();

        // If no provider succeeded at all, never silently mark the image Safe!
        if (providersUsed.Count == 0)
        {
            var combinedError = errors.Count > 0
                ? string.Join(" | ", errors)
                : "No configured image moderation providers were available to analyze the image.";

            if (options.ReturnReviewRequiredOnFailure)
            {
                return new ImageModerationResult(
                    IsFlagged: true,
                    Classification: ImageModerationClassification.ReviewRequired,
                    Detections: Array.Empty<ImageDetection>(),
                    ProvidersUsed: Array.Empty<string>(),
                    ProcessingDuration: stopwatch.Elapsed,
                    FallbackUsed: fallbackUsed,
                    Errors: errors.Count > 0 ? errors : [combinedError],
                    ProviderAvailability: GetProviderAvailability());
            }

            throw new InvalidOperationException(combinedError);
        }

        var (isFlagged, classification) = ClassifyDetections(detections, options);

        return new ImageModerationResult(
            IsFlagged: isFlagged,
            Classification: classification,
            Detections: detections,
            ProvidersUsed: providersUsed,
            ProcessingDuration: stopwatch.Elapsed,
            FallbackUsed: fallbackUsed,
            Errors: errors.Count > 0 ? errors : null,
            ProviderAvailability: GetProviderAvailability());
    }

    public static (bool IsFlagged, ImageModerationClassification Classification) ClassifyDetections(
        IReadOnlyList<ImageDetection> detections,
        ImageModerationOptions options)
    {
        if (detections is null || detections.Count == 0)
        {
            return (false, ImageModerationClassification.Safe);
        }

        var hasPotentiallyMature = false;

        foreach (var detection in detections)
        {
            // 1. Evaluate Azure severity detections (0, 2, 4, 6)
            if (detection.Severity.HasValue)
            {
                var outcome = EvaluateAzureDetection(detection, options.Azure);
                if (outcome == ImageModerationClassification.Explicit)
                {
                    return (true, ImageModerationClassification.Explicit);
                }

                if (outcome == ImageModerationClassification.PotentiallyMature)
                {
                    hasPotentiallyMature = true;
                }

                continue;
            }

            // 2. Evaluate Open-Source confidence detections (0.0 - 1.0)
            if (detection.Confidence.HasValue)
            {
                var outcome = EvaluateOpenSourceDetection(detection, options.OpenSource);
                if (outcome == ImageModerationClassification.Explicit)
                {
                    return (true, ImageModerationClassification.Explicit);
                }

                if (outcome == ImageModerationClassification.PotentiallyMature)
                {
                    hasPotentiallyMature = true;
                }
            }
        }

        if (hasPotentiallyMature)
        {
            return (options.FlagPotentiallyMature, ImageModerationClassification.PotentiallyMature);
        }

        return (false, ImageModerationClassification.Safe);
    }

    private static ImageModerationClassification EvaluateAzureDetection(
        ImageDetection detection,
        AzureModerationProviderOptions azureOpts)
    {
        var severity = detection.Severity!.Value;
        var raw = detection.RawCategory;

        var isExplicitCapable =
            detection.Category is ModerationCategory.SexualContent
                or ModerationCategory.Violence
                or ModerationCategory.SelfHarm
                or ModerationCategory.Nudity
                or ModerationCategory.Gore;

        if (isExplicitCapable)
        {
            var explicitThreshold = azureOpts.CategoryExplicitSeverityThresholds.TryGetValue(raw, out var customExp)
                ? customExp
                : azureOpts.ExplicitSeverityThreshold;

            if (severity >= explicitThreshold)
            {
                return ImageModerationClassification.Explicit;
            }

            var matureThreshold = azureOpts.CategoryPotentiallyMatureSeverityThresholds.TryGetValue(raw, out var customMat)
                ? customMat
                : azureOpts.PotentiallyMatureSeverityThreshold;

            if (severity >= matureThreshold)
            {
                return ImageModerationClassification.PotentiallyMature;
            }
        }
        else
        {
            if (azureOpts.CategoryExplicitSeverityThresholds.TryGetValue(raw, out var customExp) &&
                severity >= customExp)
            {
                return ImageModerationClassification.Explicit;
            }

            var matureThreshold = azureOpts.CategoryPotentiallyMatureSeverityThresholds.TryGetValue(raw, out var customMat)
                ? customMat
                : azureOpts.PotentiallyMatureSeverityThreshold;

            if (severity >= matureThreshold)
            {
                return ImageModerationClassification.PotentiallyMature;
            }
        }

        return ImageModerationClassification.Safe;
    }

    private static ImageModerationClassification EvaluateOpenSourceDetection(
        ImageDetection detection,
        OpenSourceModerationProviderOptions openSourceOpts)
    {
        var confidence = detection.Confidence!.Value;
        var raw = detection.RawCategory;

        if (ExplicitNudityLabels.Contains(raw))
        {
            var explicitThreshold = openSourceOpts.CategoryExplicitThresholds.TryGetValue(raw, out var customExp)
                ? customExp
                : openSourceOpts.ExplicitThreshold;

            if (confidence >= explicitThreshold)
            {
                return ImageModerationClassification.Explicit;
            }

            var matureThreshold = openSourceOpts.CategoryPotentiallyMatureThresholds.TryGetValue(raw, out var customMat)
                ? customMat
                : openSourceOpts.PotentiallyMatureThreshold;

            if (confidence >= matureThreshold)
            {
                return ImageModerationClassification.PotentiallyMature;
            }
        }
        else if (PotentiallyMatureLabels.Contains(raw) ||
                 detection.Category is ModerationCategory.Gore
                     or ModerationCategory.Violence
                     or ModerationCategory.MatureContent
                     or ModerationCategory.SuggestiveContent)
        {
            if (openSourceOpts.CategoryExplicitThresholds.TryGetValue(raw, out var customExp) &&
                confidence >= customExp)
            {
                return ImageModerationClassification.Explicit;
            }

            var matureThreshold = openSourceOpts.CategoryPotentiallyMatureThresholds.TryGetValue(raw, out var customMat)
                ? customMat
                : openSourceOpts.PotentiallyMatureThreshold;

            if (confidence >= matureThreshold)
            {
                return ImageModerationClassification.PotentiallyMature;
            }
        }
        else
        {
            if (openSourceOpts.CategoryExplicitThresholds.TryGetValue(raw, out var customExp) &&
                confidence >= customExp)
            {
                return ImageModerationClassification.Explicit;
            }

            if (openSourceOpts.CategoryPotentiallyMatureThresholds.TryGetValue(raw, out var customMat) &&
                confidence >= customMat)
            {
                return ImageModerationClassification.PotentiallyMature;
            }
        }

        return ImageModerationClassification.Safe;
    }

    private async Task<ProviderAnalysisResult> ExecuteSingleProviderAsync(
        ModerationProvider providerType,
        byte[] imageBytes,
        CancellationToken cancellationToken)
    {
        if (!_providers.TryGetValue(providerType, out var provider))
        {
            return ProviderAnalysisResult.Failure(
                providerType.ToString(),
                providerType,
                $"Moderation provider '{providerType}' is not registered in the dependency injection container.",
                TimeSpan.Zero);
        }

        return await provider.AnalyzeAsync(imageBytes, cancellationToken).ConfigureAwait(false);
    }

    private static void ValidateImageBytes(byte[] imageBytes, ImageModerationOptions options)
    {
        if (imageBytes is null || imageBytes.Length == 0)
        {
            throw new ArgumentException("Image payload cannot be null or empty.", nameof(imageBytes));
        }

        if (imageBytes.Length > options.MaxFileSizeBytes)
        {
            throw new InvalidDataException(
                $"Image size ({imageBytes.Length} bytes) exceeds the maximum allowed limit of {options.MaxFileSizeBytes} bytes.");
        }

        if (!HasSupportedMagicBytes(imageBytes))
        {
            throw new InvalidDataException(
                "Unsupported image format. Only valid JPEG, PNG, WebP, GIF, and BMP images are supported.");
        }

        using var stream = new SKMemoryStream(imageBytes);
        using var codec = SKCodec.Create(stream)
            ?? throw new InvalidDataException("Corrupted or unreadable image header.");

        var width = codec.Info.Width;
        var height = codec.Info.Height;

        if (width <= 0 || height <= 0 ||
            width > options.MaxImageDimension ||
            height > options.MaxImageDimension ||
            (long)width * height > options.MaxTotalPixels)
        {
            throw new InvalidDataException(
                $"Image dimensions ({width}x{height}) exceed safety limits ({options.MaxImageDimension}px / {options.MaxTotalPixels} pixels).");
        }
    }

    private static bool HasSupportedMagicBytes(ReadOnlySpan<byte> header)
    {
        if (header.Length < 12)
        {
            return false;
        }

        // JPEG: FF D8 FF
        if (header[0] == 0xFF && header[1] == 0xD8 && header[2] == 0xFF)
            return true;

        // PNG: 89 50 4E 47 0D 0A 1A 0A
        if (header[0] == 0x89 && header[1] == 0x50 && header[2] == 0x4E && header[3] == 0x47 &&
            header[4] == 0x0D && header[5] == 0x0A && header[6] == 0x1A && header[7] == 0x0A)
            return true;

        // WEBP: RIFF....WEBP
        if (header[0] == (byte)'R' && header[1] == (byte)'I' && header[2] == (byte)'F' && header[3] == (byte)'F' &&
            header[8] == (byte)'W' && header[9] == (byte)'E' && header[10] == (byte)'B' && header[11] == (byte)'P')
            return true;

        // GIF: GIF87a or GIF89a
        if (header[0] == (byte)'G' && header[1] == (byte)'I' && header[2] == (byte)'F' && header[3] == (byte)'8' &&
            (header[4] == (byte)'7' || header[4] == (byte)'9') && header[5] == (byte)'a')
            return true;

        // BMP: BM
        if (header[0] == (byte)'B' && header[1] == (byte)'M')
            return true;

        return false;
    }
}
