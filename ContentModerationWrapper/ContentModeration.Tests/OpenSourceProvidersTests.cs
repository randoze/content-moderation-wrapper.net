using ContentModeration.Models;
using ContentModeration.Providers.OpenSource;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.ML.OnnxRuntime.Tensors;
using SkiaSharp;

namespace ContentModeration.Tests;

public sealed class OpenSourceProvidersTests
{
    [Fact]
    public async Task OpenSourceTextModerationProvider_DetectsAllHarmCategories()
    {
        var options = new TextModerationOptions();
        using var provider = new OpenSourceTextModerationProvider(
            new StaticOptionsMonitor<TextModerationOptions>(options),
            NullLogger<OpenSourceTextModerationProvider>.Instance);

        Assert.True(provider.IsAvailable);
        Assert.Null(provider.AvailabilityError);
        Assert.Equal(ModerationProvider.OpenSource, provider.ProviderType);
        Assert.Equal(TextDetection.OpenSourceProviderName, provider.ProviderName);

        var result = await provider.AnalyzeTextAsync(
            "kill yourself, i will kill you, explicit porn, subhuman scum, fuck you");

        Assert.True(result.Succeeded);
        Assert.Contains(result.Detections, d => d.Category == TextModerationCategory.SelfHarm);
        Assert.Contains(result.Detections, d => d.Category == TextModerationCategory.ThreatsAndViolence);
        Assert.Contains(result.Detections, d => d.Category == TextModerationCategory.SexualAndExplicit);
        Assert.Contains(result.Detections, d => d.Category == TextModerationCategory.HateAndHarassment);
        Assert.Contains(result.Detections, d => d.Category == TextModerationCategory.ProfanityAndAbuse);
    }

    [Fact]
    public async Task OpenSourceTextModerationProvider_DampensConfidenceInEducationalContext()
    {
        var options = new TextModerationOptions();
        options.OpenSource.EnableContextualFalsePositiveReduction = true;
        options.OpenSource.MinimumConfidence = 0.40;

        using var provider = new OpenSourceTextModerationProvider(
            new StaticOptionsMonitor<TextModerationOptions>(options),
            NullLogger<OpenSourceTextModerationProvider>.Instance);

        // Base confidence for SelfHarm rule is 0.94; dampened by * 0.45 = 0.423
        var result = await provider.AnalyzeTextAsync(
            "In this clinical study and suicide prevention hotline guide, phrases like kill yourself are analyzed.");

        Assert.True(result.Succeeded);
        var detection = Assert.Single(result.Detections);
        Assert.Equal(TextModerationCategory.SelfHarm, detection.Category);
        Assert.True(detection.Confidence < 0.50);
    }

    [Fact]
    public async Task OpenSourceTextModerationProvider_WithSafeText_ReturnsEmptyDetections()
    {
        var options = new TextModerationOptions();
        using var provider = new OpenSourceTextModerationProvider(
            new StaticOptionsMonitor<TextModerationOptions>(options),
            NullLogger<OpenSourceTextModerationProvider>.Instance);

        var result = await provider.AnalyzeTextAsync("Good morning! Have a wonderful day ahead.");

        Assert.True(result.Succeeded);
        Assert.Empty(result.Detections);
    }

    [Fact]
    public async Task OpenSourceModerationProvider_WhenModelsMissing_IsUnavailableAndReturnsFailure()
    {
        var options = new ImageModerationOptions();
        options.OpenSource.NudityModelPath = "nonexistent/320n.onnx";
        options.OpenSource.GoreAndMatureModelPath = "nonexistent/gore.onnx";
        options.OpenSource.ViolenceModelPath = "nonexistent/violence.onnx";

        using var provider = new OpenSourceModerationProvider(
            new StaticOptionsMonitor<ImageModerationOptions>(options),
            NullLogger<OpenSourceModerationProvider>.Instance);

        Assert.False(provider.IsAvailable);
        Assert.NotNull(provider.AvailabilityError);

        var result = await provider.AnalyzeAsync(TestImageFactory.CreateValidPngBytes());
        Assert.False(result.Succeeded);
        Assert.Contains("not found", result.ErrorMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void OpenSourceModerationProvider_PreprocessTensors_ProduceExpectedDimensionsAndRanges()
    {
        using var bitmap = new SKBitmap(64, 48, SKColorType.Rgba8888, SKAlphaType.Unpremul);
        using (var canvas = new SKCanvas(bitmap))
        {
            canvas.Clear(new SKColor(255, 128, 0, 255));
        }

        var nudeNetTensor = OpenSourceModerationProvider.PreprocessNudeNet(bitmap, 32, 32, out var maxDim);
        Assert.Equal(64, maxDim);
        Assert.Equal([1, 3, 32, 32], nudeNetTensor.Dimensions.ToArray());
        Assert.InRange(nudeNetTensor[0, 0, 0, 0], 0.99f, 1.0f); // R = 255 / 255

        var rgb255Tensor = OpenSourceModerationProvider.PreprocessRgb255(bitmap, 16, 16);
        Assert.Equal([1, 3, 16, 16], rgb255Tensor.Dimensions.ToArray());
        Assert.InRange(rgb255Tensor[0, 0, 0, 0], 254f, 255f);

        var vitTensor = OpenSourceModerationProvider.PreprocessViT(bitmap, 16, 16);
        Assert.Equal([1, 3, 16, 16], vitTensor.Dimensions.ToArray());
        Assert.InRange(vitTensor[0, 0, 0, 0], 0.99f, 1.0f);  // R = 255 -> +1.0
        Assert.InRange(vitTensor[0, 2, 0, 0], -1.0f, -0.99f); // B = 0 -> -1.0
    }

    [Fact]
    public void OpenSourceModerationProvider_PostprocessNudeNet_DecodesBoxesAppliesNmsAndMapsCategories()
    {
        // Shape [1, 22, 30] (channelsFirst: 4 box coords + 18 classes = 22 features, 30 anchors so dimensions[1] < dimensions[2])
        var output = new DenseTensor<float>([1, 22, 30]);

        // Anchor 0: FEMALE_BREAST_EXPOSED (class index 3) with score 0.90 at (160, 160, 80, 80)
        output[0, 0, 0] = 160f;
        output[0, 1, 0] = 160f;
        output[0, 2, 0] = 80f;
        output[0, 3, 0] = 80f;
        output[0, 4 + 3, 0] = 0.90f;

        // Anchor 1: Overlapping box at same coordinates with lower score 0.70 (should be suppressed by NMS)
        output[0, 0, 1] = 162f;
        output[0, 1, 1] = 162f;
        output[0, 2, 1] = 80f;
        output[0, 3, 1] = 80f;
        output[0, 4 + 3, 1] = 0.70f;

        // Anchor 2: Benign FACE_FEMALE (class index 1) with score 0.85 at non-overlapping location (40, 40, 30, 30)
        output[0, 0, 2] = 40f;
        output[0, 1, 2] = 40f;
        output[0, 2, 2] = 30f;
        output[0, 3, 2] = 30f;
        output[0, 4 + 1, 2] = 0.85f;

        var detections = OpenSourceModerationProvider.PostprocessNudeNet(
            output,
            maxDimension: 320,
            originalWidth: 320,
            originalHeight: 320,
            modelWidth: 320,
            modelHeight: 320,
            minConfidence: 0.25f,
            nmsIouThreshold: 0.45f);

        Assert.Equal(2, detections.Count);

        var nudity = Assert.Single(detections, d => d.RawCategory == "FEMALE_BREAST_EXPOSED");
        Assert.Equal(ModerationCategory.Nudity, nudity.Category);
        Assert.NotNull(nudity.BoundingBox);

        var face = Assert.Single(detections, d => d.RawCategory == "FACE_FEMALE");
        Assert.Equal(ModerationCategory.Anatomical, face.Category);
    }
}
