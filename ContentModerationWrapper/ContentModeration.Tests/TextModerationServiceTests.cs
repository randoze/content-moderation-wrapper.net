using ContentModeration.Models;
using ContentModeration.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace ContentModeration.Tests;

public sealed class TextModerationServiceTests
{
    [Fact]
    public async Task AnalyzeTextAsync_NullOrWhitespace_ThrowsArgumentException()
    {
        var service = CreateService(new TextModerationOptions());
        await Assert.ThrowsAsync<ArgumentException>(() => service.AnalyzeTextAsync("   "));
    }

    [Fact]
    public async Task AnalyzeTextAsync_ExceedsMaxTextLength_ThrowsArgumentException()
    {
        var options = new TextModerationOptions { MaxTextLength = 10 };
        var service = CreateService(options);

        await Assert.ThrowsAsync<ArgumentException>(() => service.AnalyzeTextAsync("0123456789012345"));
    }

    [Fact]
    public void ClassifyDetections_EvaluatesAzureAndOpenSourceThresholdsAndOverrides()
    {
        var options = new TextModerationOptions
        {
            FlagPotentiallyHarmful = true
        };
        options.Azure.PotentiallyHarmfulSeverityThreshold = 2;
        options.Azure.ViolationSeverityThreshold = 4;
        options.Azure.CategoryViolationSeverityThresholds["HATE"] = 6;

        // 1. Empty -> Safe
        var emptyResult = TextModerationService.ClassifyDetections([], options);
        Assert.Equal((false, false, TextModerationClassification.Safe), emptyResult);

        // 2. Azure HATE with severity 4 -> PotentiallyHarmful because HATE Violation threshold is overridden to 6
        var hateDet = TextDetection.FromAzure(TextModerationCategory.HateAndHarassment, "HATE", severity: 4);
        var hateResult = TextModerationService.ClassifyDetections([hateDet], options);
        Assert.Equal((true, false, TextModerationClassification.PotentiallyHarmful), hateResult);

        // 3. Azure SEXUAL with severity 4 -> Violation (uses default ViolationSeverityThreshold = 4)
        var sexualDet = TextDetection.FromAzure(TextModerationCategory.SexualAndExplicit, "SEXUAL", severity: 4);
        var sexualResult = TextModerationService.ClassifyDetections([sexualDet], options);
        Assert.Equal((true, true, TextModerationClassification.Violation), sexualResult);

        // 4. OpenSource confidence above ViolationThreshold (0.75) -> Violation
        var osViol = TextDetection.FromOpenSource(TextModerationCategory.SelfHarm, "SELF_HARM_ENCOURAGEMENT", 0.90);
        var osViolResult = TextModerationService.ClassifyDetections([osViol], options);
        Assert.Equal((true, true, TextModerationClassification.Violation), osViolResult);
    }

    [Fact]
    public async Task AnalyzeTextAsync_WhenPrimaryFails_FallsBackToSecondaryProvider()
    {
        var options = new TextModerationOptions
        {
            Provider = ModerationProvider.Azure,
            EnableFallback = true,
            FallbackProvider = ModerationProvider.OpenSource
        };

        var azureProvider = new FakeTextModerationProvider(
            ModerationProvider.Azure,
            TextDetection.AzureProviderName,
            isAvailable: false,
            (_, _) => Task.FromResult(TextProviderAnalysisResult.Failure(
                TextDetection.AzureProviderName,
                ModerationProvider.Azure,
                "Azure unavailable",
                TimeSpan.FromMilliseconds(5))));

        var openSourceProvider = new FakeTextModerationProvider(
            ModerationProvider.OpenSource,
            TextDetection.OpenSourceProviderName,
            isAvailable: true,
            (_, _) => Task.FromResult(TextProviderAnalysisResult.Success(
                TextDetection.OpenSourceProviderName,
                ModerationProvider.OpenSource,
                [TextDetection.FromOpenSource(TextModerationCategory.ThreatsAndViolence, "THREAT", 0.92)],
                TimeSpan.FromMilliseconds(5))));

        var service = CreateService(options, azureProvider, openSourceProvider);
        var result = await service.AnalyzeTextAsync("threat text");

        Assert.True(result.FallbackUsed);
        Assert.True(result.IsViolation);
        Assert.Equal(TextModerationClassification.Violation, result.Classification);
        Assert.Equal([TextDetection.OpenSourceProviderName], result.ProvidersUsed);
        Assert.NotNull(result.Errors);
        Assert.Contains("Azure unavailable", result.Errors![0]);
    }

    [Fact]
    public async Task AnalyzeTextAsync_InHybridMode_CombinesDetectionsFromBothProviders()
    {
        var options = new TextModerationOptions
        {
            Provider = ModerationProvider.Hybrid
        };

        var azureProvider = new FakeTextModerationProvider(
            ModerationProvider.Azure,
            TextDetection.AzureProviderName,
            isAvailable: true,
            (_, _) => Task.FromResult(TextProviderAnalysisResult.Success(
                TextDetection.AzureProviderName,
                ModerationProvider.Azure,
                [TextDetection.FromAzure(TextModerationCategory.HateAndHarassment, "HATE", 2)],
                TimeSpan.FromMilliseconds(5))));

        var openSourceProvider = new FakeTextModerationProvider(
            ModerationProvider.OpenSource,
            TextDetection.OpenSourceProviderName,
            isAvailable: true,
            (_, _) => Task.FromResult(TextProviderAnalysisResult.Success(
                TextDetection.OpenSourceProviderName,
                ModerationProvider.OpenSource,
                [TextDetection.FromOpenSource(TextModerationCategory.ProfanityAndAbuse, "PROFANITY", 0.65)],
                TimeSpan.FromMilliseconds(5))));

        var service = CreateService(options, azureProvider, openSourceProvider);
        var result = await service.AnalyzeTextAsync("sample text");

        Assert.Equal(2, result.Detections.Count);
        Assert.Equal(2, result.ProvidersUsed.Count);
        Assert.Equal(TextModerationClassification.PotentiallyHarmful, result.Classification);
        Assert.True(result.IsFlagged);
        Assert.False(result.IsViolation);
    }

    [Fact]
    public async Task AnalyzeTextAsync_WhenAllProvidersFail_ReturnsReviewRequiredOrThrowsBasedOnOption()
    {
        var failingAzure = new FakeTextModerationProvider(
            ModerationProvider.Azure,
            TextDetection.AzureProviderName,
            isAvailable: false,
            (_, _) => Task.FromResult(TextProviderAnalysisResult.Failure(
                TextDetection.AzureProviderName,
                ModerationProvider.Azure,
                "Azure down",
                TimeSpan.Zero)));

        var reviewOptions = new TextModerationOptions
        {
            Provider = ModerationProvider.Azure,
            EnableFallback = false,
            ReturnReviewRequiredOnFailure = true
        };

        var serviceReview = CreateService(reviewOptions, failingAzure);
        var reviewResult = await serviceReview.AnalyzeTextAsync("check me");

        Assert.True(reviewResult.IsFlagged);
        Assert.False(reviewResult.IsViolation);
        Assert.Equal(TextModerationClassification.ReviewRequired, reviewResult.Classification);
        Assert.True(reviewResult.RequiresManualReview);

        var throwOptions = new TextModerationOptions
        {
            Provider = ModerationProvider.Azure,
            EnableFallback = false,
            ReturnReviewRequiredOnFailure = false
        };

        var serviceThrow = CreateService(throwOptions, failingAzure);
        await Assert.ThrowsAsync<InvalidOperationException>(() => serviceThrow.AnalyzeTextAsync("check me"));
    }

    private static TextModerationService CreateService(
        TextModerationOptions options,
        params FakeTextModerationProvider[] providers) =>
        new(
            providers,
            new StaticOptionsMonitor<TextModerationOptions>(options),
            NullLogger<TextModerationService>.Instance);
}
