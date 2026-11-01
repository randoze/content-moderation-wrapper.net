using System.Diagnostics;
using ContentModeration.Abstractions;
using ContentModeration.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ContentModeration.Services;

/// <summary>
/// Orchestrates text moderation across configured <see cref="ITextModerationProvider"/> instances
/// (Azure AI Content Safety, Open-Source, or Hybrid), handles fallback when a provider is unreachable,
/// and evaluates configurable severity and confidence thresholds.
/// </summary>
public sealed class TextModerationService : ITextModerationService
{
    private readonly IReadOnlyDictionary<ModerationProvider, ITextModerationProvider> _providers;
    private readonly IOptionsMonitor<TextModerationOptions> _optionsMonitor;
    private readonly ILogger<TextModerationService> _logger;

    public TextModerationService(
        IEnumerable<ITextModerationProvider> providers,
        IOptionsMonitor<TextModerationOptions> optionsMonitor,
        ILogger<TextModerationService> logger)
    {
        ArgumentNullException.ThrowIfNull(providers);
        _optionsMonitor = optionsMonitor ?? throw new ArgumentNullException(nameof(optionsMonitor));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

        var dict = new Dictionary<ModerationProvider, ITextModerationProvider>();
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

    public async Task<TextModerationResult> AnalyzeTextAsync(
        string text,
        CancellationToken cancellationToken = default,
        ModerationProvider? providerOverride = null)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            throw new ArgumentException("Input text cannot be null or whitespace.", nameof(text));
        }

        var options = _optionsMonitor.CurrentValue;
        if (text.Length > options.MaxTextLength)
        {
            throw new ArgumentException(
                $"Input text length ({text.Length} characters) exceeds the maximum allowed length of {options.MaxTextLength} characters.",
                nameof(text));
        }

        var stopwatch = Stopwatch.StartNew();
        var effectiveProvider = providerOverride ?? options.Provider;

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(Math.Max(1, options.TimeoutSeconds)));
        var token = timeoutCts.Token;

        var detections = new List<TextDetection>();
        var providersUsed = new List<string>();
        var errors = new List<string>();
        var fallbackUsed = false;

        switch (effectiveProvider)
        {
            case ModerationProvider.Azure:
            case ModerationProvider.OpenSource:
            {
                var primaryResult = await ExecuteSingleProviderAsync(effectiveProvider, text, token)
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
                        options.FallbackProvider.Value != effectiveProvider &&
                        options.FallbackProvider.Value != ModerationProvider.Hybrid)
                    {
                        var fallbackType = options.FallbackProvider.Value;
                        _logger.LogWarning(
                            "Primary text moderation provider {Primary} failed ({Error}). Falling back to {Fallback}.",
                            effectiveProvider,
                            primaryResult.ErrorMessage,
                            fallbackType);

                        var fallbackResult = await ExecuteSingleProviderAsync(fallbackType, text, token)
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
                var azureTask = ExecuteSingleProviderAsync(ModerationProvider.Azure, text, token);
                var openSourceTask = ExecuteSingleProviderAsync(ModerationProvider.OpenSource, text, token);

                await Task.WhenAll(azureTask, openSourceTask).ConfigureAwait(false);

                var azureResult = await azureTask.ConfigureAwait(false);
                var openSourceResult = await openSourceTask.ConfigureAwait(false);

                if (azureResult.Succeeded)
                {
                    providersUsed.Add(azureResult.ProviderName);
                    detections.AddRange(azureResult.Detections);
                }
                else if (!string.IsNullOrWhiteSpace(azureResult.ErrorMessage))
                {
                    errors.Add(azureResult.ErrorMessage);
                    if (openSourceResult.Succeeded && options.EnableFallback)
                    {
                        fallbackUsed = true;
                    }
                }

                if (openSourceResult.Succeeded)
                {
                    providersUsed.Add(openSourceResult.ProviderName);
                    detections.AddRange(openSourceResult.Detections);
                }
                else if (!string.IsNullOrWhiteSpace(openSourceResult.ErrorMessage))
                {
                    errors.Add(openSourceResult.ErrorMessage);
                }

                break;
            }
        }

        stopwatch.Stop();

        // Never classify text as Safe when analysis fails across all providers
        if (providersUsed.Count == 0)
        {
            var errorSummary = errors.Count > 0
                ? string.Join(" | ", errors)
                : "All configured text moderation providers failed or were unavailable.";

            if (options.ReturnReviewRequiredOnFailure)
            {
                return new TextModerationResult(
                    IsFlagged: true,
                    IsViolation: false,
                    Classification: TextModerationClassification.ReviewRequired,
                    Detections: Array.Empty<TextDetection>(),
                    ProviderName: "None",
                    ProvidersUsed: Array.Empty<string>(),
                    ProcessingDuration: stopwatch.Elapsed,
                    FallbackUsed: fallbackUsed,
                    RequiresManualReview: true,
                    Errors: errors.Count > 0 ? errors : [errorSummary]);
            }

            throw new InvalidOperationException(errorSummary);
        }

        var (isFlagged, isViolation, classification) = ClassifyDetections(detections, options);
        var primaryProviderName = string.Join("+", providersUsed);

        return new TextModerationResult(
            IsFlagged: isFlagged,
            IsViolation: isViolation,
            Classification: classification,
            Detections: detections,
            ProviderName: primaryProviderName,
            ProvidersUsed: providersUsed,
            ProcessingDuration: stopwatch.Elapsed,
            FallbackUsed: fallbackUsed,
            RequiresManualReview: classification == TextModerationClassification.ReviewRequired ? true : null,
            Errors: errors.Count > 0 ? errors : null);
    }

    public static (bool IsFlagged, bool IsViolation, TextModerationClassification Classification) ClassifyDetections(
        IReadOnlyList<TextDetection> detections,
        TextModerationOptions options)
    {
        if (detections is null || detections.Count == 0)
        {
            return (false, false, TextModerationClassification.Safe);
        }

        var hasPotentiallyHarmful = false;

        foreach (var detection in detections)
        {
            // 1. Evaluate Azure AI Content Safety integer severity (0, 2, 4, 6)
            if (detection.Severity.HasValue)
            {
                var outcome = EvaluateAzureSeverity(detection, options.Azure);
                if (outcome == TextModerationClassification.Violation)
                {
                    return (true, true, TextModerationClassification.Violation);
                }

                if (outcome == TextModerationClassification.PotentiallyHarmful)
                {
                    hasPotentiallyHarmful = true;
                }

                continue;
            }

            // 2. Evaluate Open-Source model confidence (0.0 - 1.0)
            if (detection.Confidence.HasValue)
            {
                var outcome = EvaluateOpenSourceConfidence(detection, options.OpenSource);
                if (outcome == TextModerationClassification.Violation)
                {
                    return (true, true, TextModerationClassification.Violation);
                }

                if (outcome == TextModerationClassification.PotentiallyHarmful)
                {
                    hasPotentiallyHarmful = true;
                }
            }
        }

        if (hasPotentiallyHarmful)
        {
            return (options.FlagPotentiallyHarmful, false, TextModerationClassification.PotentiallyHarmful);
        }

        return (false, false, TextModerationClassification.Safe);
    }

    private static TextModerationClassification EvaluateAzureSeverity(
        TextDetection detection,
        AzureTextModerationOptions azureOpts)
    {
        var severity = detection.Severity!.Value;
        var catKey = detection.Category.ToString();
        var rawKey = detection.RawCategory;

        var violationThreshold =
            azureOpts.CategoryViolationSeverityThresholds.TryGetValue(rawKey, out var customRawViol) ? customRawViol :
            azureOpts.CategoryViolationSeverityThresholds.TryGetValue(catKey, out var customCatViol) ? customCatViol :
            azureOpts.ViolationSeverityThreshold;

        if (severity >= violationThreshold)
        {
            return TextModerationClassification.Violation;
        }

        var harmfulThreshold =
            azureOpts.CategoryPotentiallyHarmfulSeverityThresholds.TryGetValue(rawKey, out var customRawHarm) ? customRawHarm :
            azureOpts.CategoryPotentiallyHarmfulSeverityThresholds.TryGetValue(catKey, out var customCatHarm) ? customCatHarm :
            azureOpts.PotentiallyHarmfulSeverityThreshold;

        if (severity >= harmfulThreshold)
        {
            return TextModerationClassification.PotentiallyHarmful;
        }

        return TextModerationClassification.Safe;
    }

    private static TextModerationClassification EvaluateOpenSourceConfidence(
        TextDetection detection,
        OpenSourceTextModerationOptions openSourceOpts)
    {
        var confidence = detection.Confidence!.Value;
        var catKey = detection.Category.ToString();
        var rawKey = detection.RawCategory;

        var violationThreshold =
            openSourceOpts.CategoryViolationThresholds.TryGetValue(rawKey, out var customRawViol) ? customRawViol :
            openSourceOpts.CategoryViolationThresholds.TryGetValue(catKey, out var customCatViol) ? customCatViol :
            openSourceOpts.ViolationThreshold;

        if (confidence >= violationThreshold)
        {
            return TextModerationClassification.Violation;
        }

        var harmfulThreshold =
            openSourceOpts.CategoryPotentiallyHarmfulThresholds.TryGetValue(rawKey, out var customRawHarm) ? customRawHarm :
            openSourceOpts.CategoryPotentiallyHarmfulThresholds.TryGetValue(catKey, out var customCatHarm) ? customCatHarm :
            openSourceOpts.PotentiallyHarmfulThreshold;

        if (confidence >= harmfulThreshold)
        {
            return TextModerationClassification.PotentiallyHarmful;
        }

        return TextModerationClassification.Safe;
    }

    private async Task<TextProviderAnalysisResult> ExecuteSingleProviderAsync(
        ModerationProvider providerType,
        string text,
        CancellationToken cancellationToken)
    {
        if (!_providers.TryGetValue(providerType, out var provider))
        {
            return TextProviderAnalysisResult.Failure(
                providerType.ToString(),
                providerType,
                $"Text moderation provider '{providerType}' is not registered.",
                TimeSpan.Zero);
        }

        return await provider.AnalyzeTextAsync(text, cancellationToken).ConfigureAwait(false);
    }
}
