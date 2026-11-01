using ContentModeration.Models;
using ContentModeration.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace ContentModeration.Tests;

public sealed class DefaultImageModerationServiceTests
{
    [Fact]
    public async Task AnalyzeAsync_NullOrEmptyOrUnsupportedFormat_ThrowsExpectedExceptions()
    {
        var service = CreateService(new ImageModerationOptions());

        await Assert.ThrowsAsync<ArgumentNullException>(() => service.AnalyzeAsync((Stream)null!));
        await Assert.ThrowsAsync<ArgumentException>(() => service.AnalyzeAsync(Array.Empty<byte>()));

        var invalidMagic = new byte[32];
        await Assert.ThrowsAsync<InvalidDataException>(() => service.AnalyzeAsync(invalidMagic));
    }

    [Fact]
    public async Task AnalyzeAsync_ExceedsMaxFileSizeOrDimensions_ThrowsInvalidDataException()
    {
        var smallMaxBytesOptions = new ImageModerationOptions { MaxFileSizeBytes = 10 };
        var serviceSmallSize = CreateService(smallMaxBytesOptions);
        var pngBytes = TestImageFactory.CreateValidPngBytes(32, 32);

        await Assert.ThrowsAsync<InvalidDataException>(() => serviceSmallSize.AnalyzeAsync(pngBytes));

        using var ms = new MemoryStream(pngBytes);
        await Assert.ThrowsAsync<InvalidDataException>(() => serviceSmallSize.AnalyzeAsync(ms));

        var smallDimOptions = new ImageModerationOptions { MaxImageDimension = 16 };
        var serviceSmallDim = CreateService(smallDimOptions);
        await Assert.ThrowsAsync<InvalidDataException>(() => serviceSmallDim.AnalyzeAsync(pngBytes));
    }

    [Fact]
    public void ClassifyDetections_EvaluatesAzureAndOpenSourceDetectionsCorrectly()
    {
        var options = new ImageModerationOptions
        {
            FlagPotentiallyMature = true
        };

        // 1. Benign anatomical label (FACE_FEMALE) -> Safe
        var faceDetection = ImageDetection.FromOpenSource(ModerationCategory.Anatomical, "FACE_FEMALE", 0.98);
        var faceOutcome = DefaultImageModerationService.ClassifyDetections([faceDetection], options);
        Assert.Equal((false, ImageModerationClassification.Safe), faceOutcome);

        // 2. Covered anatomical / suggestive label -> PotentiallyMature
        var coveredDetection = ImageDetection.FromOpenSource(ModerationCategory.Nudity, "FEMALE_BREAST_COVERED", 0.70);
        var coveredOutcome = DefaultImageModerationService.ClassifyDetections([coveredDetection], options);
        Assert.Equal((true, ImageModerationClassification.PotentiallyMature), coveredOutcome);

        // 3. Explicit nudity label above threshold (0.60) -> Explicit
        var explicitDetection = ImageDetection.FromOpenSource(ModerationCategory.Nudity, "FEMALE_BREAST_EXPOSED", 0.82);
        var explicitOutcome = DefaultImageModerationService.ClassifyDetections([explicitDetection], options);
        Assert.Equal((true, ImageModerationClassification.Explicit), explicitOutcome);

        // 4. Azure SexualContent severity 4 -> Explicit
        var azureExplicit = ImageDetection.FromAzure(ModerationCategory.SexualContent, "SEXUAL_CONTENT", 4);
        var azureOutcome = DefaultImageModerationService.ClassifyDetections([azureExplicit], options);
        Assert.Equal((true, ImageModerationClassification.Explicit), azureOutcome);
    }

    [Fact]
    public async Task AnalyzeAsync_HybridWithSkipAzureWhenLocalBlocks_SkipsAzureOnExplicitLocalResult()
    {
        var options = new ImageModerationOptions
        {
            Provider = ModerationProvider.Hybrid,
            SkipAzureWhenLocalBlocks = true
        };

        var localProvider = new FakeImageModerationProvider(
            ModerationProvider.OpenSource,
            ImageDetection.OpenSourceProviderName,
            isAvailable: true,
            (_, _) => Task.FromResult(ProviderAnalysisResult.Success(
                ImageDetection.OpenSourceProviderName,
                ModerationProvider.OpenSource,
                [ImageDetection.FromOpenSource(ModerationCategory.Nudity, "FEMALE_GENITALIA_EXPOSED", 0.95)],
                TimeSpan.FromMilliseconds(5))));

        var azureProvider = new FakeImageModerationProvider(
            ModerationProvider.Azure,
            ImageDetection.AzureProviderName,
            isAvailable: true,
            (_, _) => Task.FromResult(ProviderAnalysisResult.Success(
                ImageDetection.AzureProviderName,
                ModerationProvider.Azure,
                [],
                TimeSpan.FromMilliseconds(5))));

        var service = CreateService(options, localProvider, azureProvider);
        var result = await service.AnalyzeAsync(TestImageFactory.CreateValidPngBytes());

        Assert.True(result.IsFlagged);
        Assert.Equal(ImageModerationClassification.Explicit, result.Classification);
        Assert.Equal(1, localProvider.CallCount);
        Assert.Equal(0, azureProvider.CallCount);
        Assert.Equal([ImageDetection.OpenSourceProviderName], result.ProvidersUsed);
    }

    [Fact]
    public async Task AnalyzeAsync_WhenPrimaryFails_UsesFallbackProvider()
    {
        var options = new ImageModerationOptions
        {
            Provider = ModerationProvider.Azure,
            EnableFallback = true,
            FallbackProvider = ModerationProvider.OpenSource
        };

        var azureProvider = new FakeImageModerationProvider(
            ModerationProvider.Azure,
            ImageDetection.AzureProviderName,
            isAvailable: false,
            (_, _) => Task.FromResult(ProviderAnalysisResult.Failure(
                ImageDetection.AzureProviderName,
                ModerationProvider.Azure,
                "Azure error",
                TimeSpan.Zero)));

        var localProvider = new FakeImageModerationProvider(
            ModerationProvider.OpenSource,
            ImageDetection.OpenSourceProviderName,
            isAvailable: true,
            (_, _) => Task.FromResult(ProviderAnalysisResult.Success(
                ImageDetection.OpenSourceProviderName,
                ModerationProvider.OpenSource,
                [],
                TimeSpan.FromMilliseconds(5))));

        var service = CreateService(options, azureProvider, localProvider);
        var result = await service.AnalyzeAsync(TestImageFactory.CreateValidJpegBytes());

        Assert.False(result.IsFlagged);
        Assert.Equal(ImageModerationClassification.Safe, result.Classification);
        Assert.True(result.FallbackUsed);
        Assert.Equal([ImageDetection.OpenSourceProviderName], result.ProvidersUsed);
    }

    [Fact]
    public async Task AnalyzeAsync_WhenAllProvidersFail_ThrowsOrReturnsReviewRequired()
    {
        var failingAzure = new FakeImageModerationProvider(
            ModerationProvider.Azure,
            ImageDetection.AzureProviderName,
            isAvailable: false,
            (_, _) => Task.FromResult(ProviderAnalysisResult.Failure(
                ImageDetection.AzureProviderName,
                ModerationProvider.Azure,
                "Azure down",
                TimeSpan.Zero)));

        var throwOpts = new ImageModerationOptions
        {
            Provider = ModerationProvider.Azure,
            EnableFallback = false,
            ReturnReviewRequiredOnFailure = false
        };

        var throwService = CreateService(throwOpts, failingAzure);
        var png = TestImageFactory.CreateValidPngBytes();
        await Assert.ThrowsAsync<InvalidOperationException>(() => throwService.AnalyzeAsync(png));

        var reviewOpts = new ImageModerationOptions
        {
            Provider = ModerationProvider.Azure,
            EnableFallback = false,
            ReturnReviewRequiredOnFailure = true
        };

        var reviewService = CreateService(reviewOpts, failingAzure);
        var reviewResult = await reviewService.AnalyzeAsync(png);
        Assert.True(reviewResult.IsFlagged);
        Assert.Equal(ImageModerationClassification.ReviewRequired, reviewResult.Classification);
    }

    private static DefaultImageModerationService CreateService(
        ImageModerationOptions options,
        params FakeImageModerationProvider[] providers) =>
        new(
            providers,
            new StaticOptionsMonitor<ImageModerationOptions>(options),
            NullLogger<DefaultImageModerationService>.Instance);
}
