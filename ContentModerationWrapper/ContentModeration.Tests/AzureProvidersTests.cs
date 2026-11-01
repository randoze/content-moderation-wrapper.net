using ContentModeration.Models;
using ContentModeration.Providers.Azure;
using Microsoft.Extensions.Logging.Abstractions;

namespace ContentModeration.Tests;

public sealed class AzureProvidersTests
{
    [Fact]
    public async Task AzureContentSafetyProvider_WhenCredentialsMissing_ReportsUnavailableAndFailsAnalysis()
    {
        var options = new ImageModerationOptions();
        options.Azure.Endpoint = "";
        options.Azure.ApiKey = "";

        var provider = new AzureContentSafetyProvider(
            new StaticOptionsMonitor<ImageModerationOptions>(options),
            NullLogger<AzureContentSafetyProvider>.Instance);

        Assert.Equal(ModerationProvider.Azure, provider.ProviderType);
        Assert.Equal(ImageDetection.AzureProviderName, provider.ProviderName);
        Assert.False(provider.IsAvailable);
        Assert.NotNull(provider.AvailabilityError);

        var result = await provider.AnalyzeAsync(TestImageFactory.CreateValidPngBytes());
        Assert.False(result.Succeeded);
        Assert.Contains("credentials", result.ErrorMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task AzureContentSafetyProvider_WhenEndpointInvalidUri_ReportsUnavailableAndFailsAnalysis()
    {
        var options = new ImageModerationOptions();
        options.Azure.Endpoint = "not-a-valid-uri";
        options.Azure.ApiKey = "secret-key";

        var provider = new AzureContentSafetyProvider(
            new StaticOptionsMonitor<ImageModerationOptions>(options),
            NullLogger<AzureContentSafetyProvider>.Instance);

        Assert.False(provider.IsAvailable);

        var result = await provider.AnalyzeAsync(TestImageFactory.CreateValidPngBytes());
        Assert.False(result.Succeeded);
        Assert.Contains("not a valid absolute URI", result.ErrorMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task AzureContentSafetyProvider_WhenImageBytesEmpty_ReturnsFailure()
    {
        var options = new ImageModerationOptions();
        options.Azure.Endpoint = "https://example.cognitiveservices.azure.com/";
        options.Azure.ApiKey = "secret-key";

        var provider = new AzureContentSafetyProvider(
            new StaticOptionsMonitor<ImageModerationOptions>(options),
            NullLogger<AzureContentSafetyProvider>.Instance);

        Assert.True(provider.IsAvailable);
        Assert.Null(provider.AvailabilityError);

        var result = await provider.AnalyzeAsync([]);
        Assert.False(result.Succeeded);
        Assert.Contains("null or empty", result.ErrorMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void AzureContentSafetyProvider_MapCategories_FiltersByMinimumSeverityAndMapsLabels()
    {
        var raw = new (string CategoryName, int? Severity)[]
        {
            ("Sexual", 4),
            ("Violence", 2),
            ("SelfHarm", 0), // Below min severity 2
            ("Hate", 6),
            ("CustomUnknown", 4)
        };

        var detections = AzureContentSafetyProvider.MapCategories(raw, minimumReportedSeverity: 2);

        Assert.Equal(4, detections.Count);
        Assert.Contains(detections, d => d.Category == ModerationCategory.SexualContent && d.RawCategory == "SEXUAL_CONTENT" && d.Severity == 4);
        Assert.Contains(detections, d => d.Category == ModerationCategory.Violence && d.RawCategory == "VIOLENCE" && d.Severity == 2);
        Assert.Contains(detections, d => d.Category == ModerationCategory.Hate && d.RawCategory == "HATE" && d.Severity == 6);
        Assert.Contains(detections, d => d.Category == ModerationCategory.MatureContent && d.RawCategory == "CUSTOMUNKNOWN" && d.Severity == 4);
    }

    [Fact]
    public async Task AzureTextModerationProvider_ValidationAndAvailability_WorkAsExpected()
    {
        var options = new TextModerationOptions();
        options.Azure.Endpoint = "https://example.cognitiveservices.azure.com/";
        options.Azure.ApiKey = "secret-key";

        var provider = new AzureTextModerationProvider(
            new StaticOptionsMonitor<TextModerationOptions>(options),
            NullLogger<AzureTextModerationProvider>.Instance);

        Assert.True(provider.IsAvailable);
        Assert.Null(provider.AvailabilityError);

        var emptyResult = await provider.AnalyzeTextAsync("   ");
        Assert.False(emptyResult.Succeeded);
        Assert.Contains("null or empty", emptyResult.ErrorMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void AzureTextModerationProvider_MapCategories_IncludesCategoriesAndBlocklistHits()
    {
        var categories = new (string CategoryName, int? Severity)[]
        {
            ("Sexual", 6),
            ("Hate", 2),
            ("Violence", 0),
            ("SelfHarm", 4)
        };

        var blocklistMatches = new (string BlocklistName, string ItemText)[]
        {
            ("fil-ceb-profanity", "putangina"),
            ("fil-ceb-profanity", "gago"),
            ("unknown-custom-list", "badword")
        };

        var knownDef = new ResolvedTextBlocklist(
            "fil-ceb-profanity",
            "Tagalog/Cebuano profanity.",
            TextModerationCategory.ProfanityAndAbuse,
            4,
            ["putangina", "gago"]);

        var detections = AzureTextModerationProvider.MapCategories(
            categories,
            minimumReportedSeverity: 2,
            blocklistMatches: blocklistMatches,
            blocklistDefinitionLookup: name => name == "fil-ceb-profanity" ? knownDef : null);

        Assert.Equal(5, detections.Count);
        Assert.Contains(detections, d => d.Category == TextModerationCategory.SexualAndExplicit && d.Severity == 6);
        Assert.Contains(detections, d => d.Category == TextModerationCategory.HateAndHarassment && d.Severity == 2);
        Assert.Contains(detections, d => d.Category == TextModerationCategory.SelfHarm && d.Severity == 4);

        var knownBlocklistHit = Assert.Single(detections, d => d.RawCategory == "BLOCKLIST:fil-ceb-profanity");
        Assert.Equal(TextModerationCategory.ProfanityAndAbuse, knownBlocklistHit.Category);
        Assert.Equal(4, knownBlocklistHit.Severity);
        Assert.Contains("\"putangina\"", knownBlocklistHit.Description);
        Assert.Contains("\"gago\"", knownBlocklistHit.Description);

        var unknownBlocklistHit = Assert.Single(detections, d => d.RawCategory == "BLOCKLIST:unknown-custom-list");
        Assert.Equal(TextModerationCategory.OtherHarmful, unknownBlocklistHit.Category);
        Assert.Equal(6, unknownBlocklistHit.Severity);
    }
}
