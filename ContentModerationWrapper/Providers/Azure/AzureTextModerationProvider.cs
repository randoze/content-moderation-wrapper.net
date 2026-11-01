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

namespace ContentModeration.Providers.Azure;

/// <summary>
/// <see cref="ITextModerationProvider"/> implementation backed by the official Azure AI Content Safety SDK
/// (<c>Azure.AI.ContentSafety</c>). Analyzes text for Sexual, Hate, Violence, and SelfHarm categories
/// and returns integer severity scores (0, 2, 4, 6).
/// </summary>
public sealed class AzureTextModerationProvider : ITextModerationProvider
{
    private const int AzureMaxTextCharacters = 10_000;

    private readonly IOptionsMonitor<TextModerationOptions> _optionsMonitor;
    private readonly IConfiguration? _configuration;
    private readonly ILogger<AzureTextModerationProvider> _logger;
    private readonly AzureTextBlocklistManager _blocklistManager;
    private readonly object _clientLock = new();

    private ContentSafetyClient? _cachedClient;
    private string _cachedEndpoint = string.Empty;
    private string _cachedKeyHash = string.Empty;

    public AzureTextModerationProvider(
        IOptionsMonitor<TextModerationOptions> optionsMonitor,
        ILogger<AzureTextModerationProvider> logger,
        IConfiguration? configuration = null)
    {
        _optionsMonitor = optionsMonitor ?? throw new ArgumentNullException(nameof(optionsMonitor));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _configuration = configuration;
        _blocklistManager = new AzureTextBlocklistManager(_logger);
    }

    public ModerationProvider ProviderType => ModerationProvider.Azure;

    public string ProviderName => TextDetection.AzureProviderName;

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

    public async Task<TextProviderAnalysisResult> AnalyzeTextAsync(
        string text,
        CancellationToken cancellationToken = default)
    {
        var stopwatch = Stopwatch.StartNew();

        if (string.IsNullOrWhiteSpace(text))
        {
            stopwatch.Stop();
            return TextProviderAnalysisResult.Failure(
                ProviderName,
                ProviderType,
                "Text input cannot be null or empty.",
                stopwatch.Elapsed);
        }

        var (endpoint, apiKey) = ResolveCredentials();
        if (string.IsNullOrWhiteSpace(endpoint) || string.IsNullOrWhiteSpace(apiKey))
        {
            stopwatch.Stop();
            return TextProviderAnalysisResult.Failure(
                ProviderName,
                ProviderType,
                "Azure AI Content Safety credentials (Endpoint and ApiKey) are not configured.",
                stopwatch.Elapsed);
        }

        if (!Uri.TryCreate(endpoint, UriKind.Absolute, out var endpointUri))
        {
            stopwatch.Stop();
            return TextProviderAnalysisResult.Failure(
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
            var boundedText = text.Length > AzureMaxTextCharacters
                ? text[..AzureMaxTextCharacters]
                : text;

            // Azure's classifiers are trained on a limited set of languages; Tagalog/Cebuano/Taglish coverage
            // comes from custom blocklists. Append a normalized copy so obfuscated terms (t4ngina, g.a.g.o) still match.
            var analysisText = BlocklistTextNormalizer.BuildAnalysisText(
                boundedText,
                azureOptions.AppendNormalizedTextForBlocklistMatching,
                AzureMaxTextCharacters);

            var blocklistNames = ResolveBlocklistNames(endpointUri, apiKey, timeoutSeconds, azureOptions);

            Response<AnalyzeTextResult> response;
            try
            {
                response = await client
                    .AnalyzeTextAsync(BuildRequest(analysisText, azureOptions, blocklistNames), timeoutCts.Token)
                    .ConfigureAwait(false);
            }
            catch (RequestFailedException ex) when (blocklistNames.Count > 0 &&
                                                    (ex.Status == (int)HttpStatusCode.NotFound ||
                                                     ex.Status == (int)HttpStatusCode.BadRequest))
            {
                // A referenced blocklist does not exist (yet) – retry without blocklists and re-provision in background
                _logger.LogWarning(
                    "Azure text analysis with blocklists failed (HTTP {Status}, ErrorCode: {ErrorCode}); retrying without blocklists.",
                    ex.Status, ex.ErrorCode);
                _blocklistManager.Invalidate();
                blocklistNames = Array.Empty<string>();
                response = await client
                    .AnalyzeTextAsync(BuildRequest(analysisText, azureOptions, blocklistNames), timeoutCts.Token)
                    .ConfigureAwait(false);
            }

            var rawCategories = response.Value.CategoriesAnalysis
                .Select(c => (c.Category.ToString(), c.Severity));

            var blocklistHits = response.Value.BlocklistsMatch?
                .Select(b => (b.BlocklistName, b.BlocklistItemText))
                ?? Enumerable.Empty<(string, string)>();

            var detections = MapCategories(
                rawCategories,
                azureOptions.MinimumReportedSeverity,
                blocklistHits,
                name => _blocklistManager.TryGetDefinition(name, out var def) ? def : null);

            stopwatch.Stop();
            return TextProviderAnalysisResult.Success(
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
            var msg = $"Azure AI Content Safety text analysis timed out after {timeoutSeconds} seconds.";
            _logger.LogWarning("{Message}", msg);
            return TextProviderAnalysisResult.Failure(ProviderName, ProviderType, msg, stopwatch.Elapsed);
        }
        catch (RequestFailedException ex) when (ex.Status == (int)HttpStatusCode.TooManyRequests)
        {
            stopwatch.Stop();
            var msg = $"Azure AI Content Safety rate limit exceeded (HTTP 429, ErrorCode: {ex.ErrorCode}).";
            _logger.LogWarning("{Message}", msg);
            return TextProviderAnalysisResult.Failure(ProviderName, ProviderType, msg, stopwatch.Elapsed);
        }
        catch (RequestFailedException ex)
        {
            stopwatch.Stop();
            var msg = $"Azure AI Content Safety text request failed with HTTP {ex.Status} (ErrorCode: {ex.ErrorCode}).";
            _logger.LogWarning("{Message}", msg);
            return TextProviderAnalysisResult.Failure(ProviderName, ProviderType, msg, stopwatch.Elapsed);
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            _logger.LogWarning(ex, "Unexpected error communicating with Azure AI Content Safety for text moderation.");
            return TextProviderAnalysisResult.Failure(
                ProviderName,
                ProviderType,
                $"Azure AI Content Safety text error: {ex.Message}",
                stopwatch.Elapsed);
        }
    }

    internal static IReadOnlyList<TextDetection> MapCategories(
        IEnumerable<(string CategoryName, int? Severity)> categories,
        int minimumReportedSeverity = 2,
        IEnumerable<(string BlocklistName, string ItemText)>? blocklistMatches = null,
        Func<string, ResolvedTextBlocklist?>? blocklistDefinitionLookup = null)
    {
        var results = new List<TextDetection>();
        var effectiveMinSeverity = Math.Max(1, minimumReportedSeverity);

        foreach (var (rawCategory, severity) in categories)
        {
            if (!severity.HasValue || severity.Value < effectiveMinSeverity)
            {
                continue;
            }

            var (normalizedEnum, rawLabel, description) = MapAzureTextCategory(rawCategory);
            results.Add(TextDetection.FromAzure(normalizedEnum, rawLabel, severity.Value, description));
        }

        if (blocklistMatches is not null)
        {
            // One detection per matched blocklist, listing the distinct matched terms
            foreach (var group in blocklistMatches.GroupBy(b => b.BlocklistName, StringComparer.OrdinalIgnoreCase))
            {
                var blocklistName = group.Key;
                var terms = group
                    .Select(g => g.ItemText)
                    .Where(t => !string.IsNullOrWhiteSpace(t))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();
                var termsText = terms.Count > 0 ? $" Matched: {string.Join(", ", terms.Select(t => $"\"{t}\""))}." : string.Empty;

                var definition = blocklistDefinitionLookup?.Invoke(blocklistName);
                if (definition is not null)
                {
                    results.Add(TextDetection.FromAzure(
                        definition.Category,
                        $"BLOCKLIST:{blocklistName}",
                        severity: definition.Severity,
                        description: $"{definition.Description} (Azure custom blocklist '{blocklistName}').{termsText}"));
                }
                else
                {
                    results.Add(TextDetection.FromAzure(
                        TextModerationCategory.OtherHarmful,
                        $"BLOCKLIST:{blocklistName}",
                        severity: 6,
                        description: $"Matched custom Azure blocklist '{blocklistName}'.{termsText}"));
                }
            }
        }

        return results;
    }

    private static AnalyzeTextOptions BuildRequest(
        string text,
        AzureTextModerationOptions azureOptions,
        IReadOnlyCollection<string> blocklistNames)
    {
        var requestOptions = new AnalyzeTextOptions(text)
        {
            HaltOnBlocklistHit = azureOptions.HaltOnBlocklistHit,
            OutputType = AnalyzeTextOutputType.FourSeverityLevels
        };

        foreach (var blocklist in blocklistNames)
        {
            requestOptions.BlocklistNames.Add(blocklist);
        }

        return requestOptions;
    }

    /// <summary>
    /// Combines manually configured <see cref="AzureTextModerationOptions.BlocklistNames"/> with auto-provisioned
    /// blocklists that are ready. Starts background provisioning when needed.
    /// </summary>
    private IReadOnlyList<string> ResolveBlocklistNames(
        Uri endpointUri,
        string apiKey,
        int timeoutSeconds,
        AzureTextModerationOptions azureOptions)
    {
        var names = new List<string>();
        foreach (var name in azureOptions.BlocklistNames ?? [])
        {
            if (!string.IsNullOrWhiteSpace(name))
            {
                names.Add(name.Trim());
            }
        }

        try
        {
            var credentialFingerprint = endpointUri.AbsoluteUri + "|" +
                                        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(apiKey)));
            names.AddRange(_blocklistManager.GetReadyBlocklistsAndEnsureProvisioning(
                () => CreateBlocklistClient(endpointUri, apiKey, timeoutSeconds),
                credentialFingerprint,
                azureOptions));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to resolve Azure text blocklists; continuing without auto-provisioned blocklists.");
        }

        return names.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>
    /// Starts provisioning of the configured Azure text blocklists and waits for it to complete.
    /// Call once at application startup so blocklists are ready before the first requests.
    /// </summary>
    public async Task WarmUpBlocklistsAsync()
    {
        var (endpoint, apiKey) = ResolveCredentials();
        if (string.IsNullOrWhiteSpace(endpoint) || string.IsNullOrWhiteSpace(apiKey) ||
            !Uri.TryCreate(endpoint, UriKind.Absolute, out var endpointUri))
        {
            return;
        }

        var azureOptions = _optionsMonitor.CurrentValue.Azure;
        _ = ResolveBlocklistNames(endpointUri, apiKey, Math.Max(1, azureOptions.TimeoutSeconds), azureOptions);
        await _blocklistManager.WaitForProvisioningAsync().ConfigureAwait(false);
    }

    private static BlocklistClient CreateBlocklistClient(Uri endpointUri, string apiKey, int timeoutSeconds) =>
        new(endpointUri, new AzureKeyCredential(apiKey), new ContentSafetyClientOptions
        {
            Retry =
            {
                MaxRetries = 2,
                NetworkTimeout = TimeSpan.FromSeconds(Math.Max(timeoutSeconds, 15))
            }
        });

    internal static (TextModerationCategory Category, string RawLabel, string Description) MapAzureTextCategory(
        string azureCategory)
    {
        if (string.IsNullOrWhiteSpace(azureCategory))
        {
            return (TextModerationCategory.OtherHarmful, "UNKNOWN", "Unrecognized harmful text category.");
        }

        if (azureCategory.Equals(TextCategory.Sexual.ToString(), StringComparison.OrdinalIgnoreCase) ||
            azureCategory.Equals("SEXUAL", StringComparison.OrdinalIgnoreCase) ||
            azureCategory.Equals("SEXUAL_CONTENT", StringComparison.OrdinalIgnoreCase))
        {
            return (
                TextModerationCategory.SexualAndExplicit,
                "SEXUAL",
                "Sexual and explicit language or adult descriptions (Azure AI Content Safety).");
        }

        if (azureCategory.Equals(TextCategory.Hate.ToString(), StringComparison.OrdinalIgnoreCase))
        {
            return (
                TextModerationCategory.HateAndHarassment,
                "HATE",
                "Hate speech, identity attacks, or discriminatory harassment (Azure AI Content Safety).");
        }

        if (azureCategory.Equals(TextCategory.Violence.ToString(), StringComparison.OrdinalIgnoreCase))
        {
            return (
                TextModerationCategory.ThreatsAndViolence,
                "VIOLENCE",
                "Threats, physical violence, or graphic harm descriptions (Azure AI Content Safety).");
        }

        if (azureCategory.Equals(TextCategory.SelfHarm.ToString(), StringComparison.OrdinalIgnoreCase) ||
            azureCategory.Equals("SELF_HARM", StringComparison.OrdinalIgnoreCase))
        {
            return (
                TextModerationCategory.SelfHarm,
                "SELF_HARM",
                "Self-harm encouragement or suicide instruction (Azure AI Content Safety).");
        }

        return (
            TextModerationCategory.OtherHarmful,
            azureCategory.ToUpperInvariant(),
            $"Harmful text category '{azureCategory}' (Azure AI Content Safety).");
    }

    private (string Endpoint, string ApiKey) ResolveCredentials()
    {
        var opts = _optionsMonitor.CurrentValue.Azure;

        var endpoint = !string.IsNullOrWhiteSpace(opts.Endpoint)
            ? opts.Endpoint
            : _configuration?["ContentModeration:Text:Azure:Endpoint"]
              ?? _configuration?["ImageModeration:Azure:Endpoint"]
              ?? _configuration?["Moderation:AzureContentSafety:Endpoint"]
              ?? _configuration?["AZURE_CONTENT_SAFETY_ENDPOINT"]
              ?? Environment.GetEnvironmentVariable("AZURE_CONTENT_SAFETY_ENDPOINT")
              ?? string.Empty;

        var apiKey = !string.IsNullOrWhiteSpace(opts.ApiKey)
            ? opts.ApiKey
            : _configuration?["ContentModeration:Text:Azure:ApiKey"]
              ?? _configuration?["ImageModeration:Azure:ApiKey"]
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
