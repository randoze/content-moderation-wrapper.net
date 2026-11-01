using System.Diagnostics;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using Azure;
using Azure.AI.ContentSafety;
using ContentModeration.Abstractions;
using ContentModeration.Models;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SkiaSharp;

namespace ContentModeration.Providers.Azure;

/// <summary>
/// <see cref="IImageModerationProvider"/> implementation backed by the official Azure AI Content Safety SDK
/// (<c>Azure.AI.ContentSafety</c>). Returns integer severity scores (0, 2, 4, 6) for Sexual Content,
/// Violence, Self-Harm, and Hate categories.
/// </summary>
public sealed class AzureContentSafetyProvider : IImageModerationProvider
{
    private const int AzureMaxImageBytes = 4 * 1024 * 1024; // 4 MB limit for synchronous AnalyzeImage API
    private const int AzureMaxDimension = 2048;

    private readonly IOptionsMonitor<ImageModerationOptions> _optionsMonitor;
    private readonly IConfiguration? _configuration;
    private readonly ILogger<AzureContentSafetyProvider> _logger;
    private readonly object _clientLock = new();

    private ContentSafetyClient? _cachedClient;
    private string _cachedEndpoint = string.Empty;
    private string _cachedKeyHash = string.Empty;

    public AzureContentSafetyProvider(
        IOptionsMonitor<ImageModerationOptions> optionsMonitor,
        ILogger<AzureContentSafetyProvider> logger,
        IConfiguration? configuration = null)
    {
        _optionsMonitor = optionsMonitor ?? throw new ArgumentNullException(nameof(optionsMonitor));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _configuration = configuration;
    }

    public ModerationProvider ProviderType => ModerationProvider.Azure;

    public string ProviderName => ImageDetection.AzureProviderName;

    public bool IsAvailable
    {
        get
        {
            var (endpoint, apiKey) = ResolveCredentials();
            return !string.IsNullOrWhiteSpace(endpoint) &&
                   !string.IsNullOrWhiteSpace(apiKey) &&
                   Uri.TryCreate(endpoint, UriKind.Absolute, out _);
        }
    }

    public string? AvailabilityError =>
        IsAvailable
            ? null
            : "Azure AI Content Safety credentials (Endpoint and ApiKey) are not configured or Endpoint is not a valid URI.";

    public async Task<ProviderAnalysisResult> AnalyzeAsync(
        byte[] imageBytes,
        CancellationToken cancellationToken = default)
    {
        var stopwatch = Stopwatch.StartNew();

        if (imageBytes is null || imageBytes.Length == 0)
        {
            stopwatch.Stop();
            return ProviderAnalysisResult.Failure(
                ProviderName,
                ProviderType,
                "Image payload cannot be null or empty.",
                stopwatch.Elapsed);
        }

        var (endpoint, apiKey) = ResolveCredentials();
        if (string.IsNullOrWhiteSpace(endpoint) || string.IsNullOrWhiteSpace(apiKey))
        {
            stopwatch.Stop();
            return ProviderAnalysisResult.Failure(
                ProviderName,
                ProviderType,
                "Azure AI Content Safety credentials (Endpoint and ApiKey) are not configured.",
                stopwatch.Elapsed);
        }

        if (!Uri.TryCreate(endpoint, UriKind.Absolute, out var endpointUri))
        {
            stopwatch.Stop();
            return ProviderAnalysisResult.Failure(
                ProviderName,
                ProviderType,
                $"Configured Azure AI Content Safety endpoint '{endpoint}' is not a valid absolute URI.",
                stopwatch.Elapsed);
        }

        var azureOptions = _optionsMonitor.CurrentValue.Azure;
        var timeoutSeconds = Math.Max(1, azureOptions.TimeoutSeconds);
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));

        try
        {
            var client = GetOrCreateClient(endpointUri, apiKey, timeoutSeconds);
            var payloadBytes = PrepareImageBytesForAzure(imageBytes);
            var imageData = new ContentSafetyImageData(BinaryData.FromBytes(payloadBytes));
            var requestOptions = new AnalyzeImageOptions(imageData);

            Response<AnalyzeImageResult> response = await client
                .AnalyzeImageAsync(requestOptions, timeoutCts.Token)
                .ConfigureAwait(false);

            var rawCategories = response.Value.CategoriesAnalysis
                .Select(c => (c.Category.ToString(), c.Severity));

            var detections = MapCategories(rawCategories, azureOptions.MinimumReportedSeverity);
            stopwatch.Stop();

            return ProviderAnalysisResult.Success(
                ProviderName,
                ProviderType,
                detections,
                stopwatch.Elapsed);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            stopwatch.Stop();
            var msg = $"Azure AI Content Safety request timed out after {timeoutSeconds} seconds.";
            _logger.LogWarning("{Message}", msg);
            return ProviderAnalysisResult.Failure(ProviderName, ProviderType, msg, stopwatch.Elapsed);
        }
        catch (RequestFailedException ex) when (ex.Status == (int)HttpStatusCode.TooManyRequests)
        {
            stopwatch.Stop();
            var msg = $"Azure AI Content Safety rate limit exceeded (HTTP 429, ErrorCode: {ex.ErrorCode}).";
            _logger.LogWarning("{Message}", msg);
            return ProviderAnalysisResult.Failure(ProviderName, ProviderType, msg, stopwatch.Elapsed);
        }
        catch (RequestFailedException ex)
        {
            stopwatch.Stop();
            var msg = $"Azure AI Content Safety request failed with HTTP {ex.Status} (ErrorCode: {ex.ErrorCode}).";
            _logger.LogWarning("{Message}", msg);
            return ProviderAnalysisResult.Failure(ProviderName, ProviderType, msg, stopwatch.Elapsed);
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            _logger.LogWarning(ex, "Unexpected error communicating with Azure AI Content Safety.");
            return ProviderAnalysisResult.Failure(
                ProviderName,
                ProviderType,
                $"Azure AI Content Safety error: {ex.Message}",
                stopwatch.Elapsed);
        }
    }

    internal static IReadOnlyList<ImageDetection> MapCategories(
        IEnumerable<(string CategoryName, int? Severity)> categories,
        int minimumReportedSeverity = 2)
    {
        var results = new List<ImageDetection>();
        var effectiveMinSeverity = Math.Max(1, minimumReportedSeverity);

        foreach (var (rawCategory, severity) in categories)
        {
            if (!severity.HasValue || severity.Value < effectiveMinSeverity)
            {
                continue;
            }

            var (normalizedEnum, rawLabel) = MapAzureCategory(rawCategory);
            results.Add(ImageDetection.FromAzure(normalizedEnum, rawLabel, severity.Value));
        }

        return results;
    }

    internal static (ModerationCategory Category, string RawLabel) MapAzureCategory(string azureCategory)
    {
        if (string.IsNullOrWhiteSpace(azureCategory))
        {
            return (ModerationCategory.MatureContent, "UNKNOWN");
        }

        if (azureCategory.Equals(ImageCategory.Sexual.ToString(), StringComparison.OrdinalIgnoreCase) ||
            azureCategory.Equals("SEXUAL_CONTENT", StringComparison.OrdinalIgnoreCase))
        {
            return (ModerationCategory.SexualContent, "SEXUAL_CONTENT");
        }

        if (azureCategory.Equals(ImageCategory.Violence.ToString(), StringComparison.OrdinalIgnoreCase))
        {
            return (ModerationCategory.Violence, "VIOLENCE");
        }

        if (azureCategory.Equals(ImageCategory.SelfHarm.ToString(), StringComparison.OrdinalIgnoreCase) ||
            azureCategory.Equals("SELF_HARM", StringComparison.OrdinalIgnoreCase))
        {
            return (ModerationCategory.SelfHarm, "SELF_HARM");
        }

        if (azureCategory.Equals(ImageCategory.Hate.ToString(), StringComparison.OrdinalIgnoreCase))
        {
            return (ModerationCategory.Hate, "HATE");
        }

        return (ModerationCategory.MatureContent, azureCategory.ToUpperInvariant());
    }

    private static byte[] PrepareImageBytesForAzure(byte[] imageBytes)
    {
        using var codec = SKCodec.Create(new SKMemoryStream(imageBytes));
        if (codec is null)
        {
            return imageBytes;
        }

        var width = codec.Info.Width;
        var height = codec.Info.Height;

        if (imageBytes.Length <= AzureMaxImageBytes &&
            width <= AzureMaxDimension &&
            height <= AzureMaxDimension)
        {
            return imageBytes;
        }

        using var bitmap = SKBitmap.Decode(codec);
        if (bitmap is null)
        {
            return imageBytes;
        }

        if (width > AzureMaxDimension || height > AzureMaxDimension)
        {
            var scale = Math.Min((float)AzureMaxDimension / width, (float)AzureMaxDimension / height);
            var targetWidth = Math.Max(1, (int)Math.Round(width * scale));
            var targetHeight = Math.Max(1, (int)Math.Round(height * scale));

            using var resized = bitmap.Resize(new SKImageInfo(targetWidth, targetHeight), SKFilterQuality.Medium);
            using var skImage = SKImage.FromBitmap(resized ?? bitmap);
            using var encoded = skImage.Encode(SKEncodedImageFormat.Jpeg, 85);
            return encoded.ToArray();
        }

        using var fallbackImage = SKImage.FromBitmap(bitmap);
        using var fallbackEncoded = fallbackImage.Encode(SKEncodedImageFormat.Jpeg, 85);
        return fallbackEncoded.ToArray();
    }

    private (string Endpoint, string ApiKey) ResolveCredentials()
    {
        var opts = _optionsMonitor.CurrentValue.Azure;

        var endpoint = !string.IsNullOrWhiteSpace(opts.Endpoint)
            ? opts.Endpoint
            : _configuration?["ImageModeration:Azure:Endpoint"]
              ?? _configuration?["Moderation:AzureContentSafety:Endpoint"]
              ?? _configuration?["AZURE_CONTENT_SAFETY_ENDPOINT"]
              ?? Environment.GetEnvironmentVariable("AZURE_CONTENT_SAFETY_ENDPOINT")
              ?? string.Empty;

        var apiKey = !string.IsNullOrWhiteSpace(opts.ApiKey)
            ? opts.ApiKey
            : _configuration?["ImageModeration:Azure:ApiKey"]
              ?? _configuration?["Moderation:AzureContentSafety:ApiKey"]
              ?? _configuration?["AZURE_CONTENT_SAFETY_API_KEY"]
              ?? Environment.GetEnvironmentVariable("AZURE_CONTENT_SAFETY_API_KEY")
              ?? string.Empty;

        return (endpoint.Trim(), apiKey.Trim());
    }

    private ContentSafetyClient GetOrCreateClient(Uri endpointUri, string apiKey, int timeoutSeconds)
    {
        var keyHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(apiKey)));

        lock (_clientLock)
        {
            if (_cachedClient is not null &&
                string.Equals(_cachedEndpoint, endpointUri.AbsoluteUri, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(_cachedKeyHash, keyHash, StringComparison.Ordinal))
            {
                return _cachedClient;
            }

            var clientOptions = new ContentSafetyClientOptions
            {
                Retry =
                {
                    MaxRetries = 1,
                    NetworkTimeout = TimeSpan.FromSeconds(timeoutSeconds)
                }
            };

            _cachedClient = new ContentSafetyClient(endpointUri, new AzureKeyCredential(apiKey), clientOptions);
            _cachedEndpoint = endpointUri.AbsoluteUri;
            _cachedKeyHash = keyHash;
            return _cachedClient;
        }
    }
}
