using System.Diagnostics;
using ContentModeration.Abstractions;
using ContentModeration.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using SkiaSharp;

namespace ContentModeration.Providers.OpenSource;

/// <summary>
/// <see cref="IImageModerationProvider"/> implementation that runs open-source ONNX models locally on ONNX Runtime:
/// <list type="bullet">
/// <item><description>NudeNet v3 (<c>320n.onnx</c>) for anatomical nudity detection with pixel bounding boxes.</description></item>
/// <item><description>SwiftFormer (<c>image-safety-classifier-xs.onnx</c>) for Gore (<c>GORE</c>), Mature Content (<c>MATURE_CONTENT</c>), and Suggestive Content (<c>SUGGESTIVE_CONTENT</c>).</description></item>
/// <item><description>ViT (<c>vit-violence-detection.onnx</c>) for Graphic Violence (<c>VIOLENCE</c>).</description></item>
/// </list>
/// </summary>
public sealed class OpenSourceModerationProvider : IImageModerationProvider, IDisposable
{
    public static readonly string[] NudeNetClassLabels =
    [
        "FEMALE_GENITALIA_COVERED",
        "FACE_FEMALE",
        "BUTTOCKS_EXPOSED",
        "FEMALE_BREAST_EXPOSED",
        "FEMALE_GENITALIA_EXPOSED",
        "MALE_BREAST_EXPOSED",
        "ANUS_EXPOSED",
        "FEET_EXPOSED",
        "BELLY_COVERED",
        "FEET_COVERED",
        "ARMPITS_COVERED",
        "ARMPITS_EXPOSED",
        "FACE_MALE",
        "BELLY_EXPOSED",
        "MALE_GENITALIA_EXPOSED",
        "ANUS_COVERED",
        "FEMALE_BREAST_COVERED",
        "BUTTOCKS_COVERED"
    ];

    private static readonly HashSet<string> BenignAnatomicalLabels = new(StringComparer.OrdinalIgnoreCase)
    {
        "FACE_FEMALE",
        "FACE_MALE",
        "FEET_EXPOSED",
        "FEET_COVERED",
        "ARMPITS_EXPOSED",
        "ARMPITS_COVERED",
        "BELLY_COVERED"
    };

    private readonly IOptionsMonitor<ImageModerationOptions> _optionsMonitor;
    private readonly ILogger<OpenSourceModerationProvider> _logger;
    private readonly SemaphoreSlim _concurrencyGate;

    private readonly InferenceSession? _nudeNetSession;
    private readonly string _nudeNetInputName = "images";
    private readonly int _nudeNetResolution;

    private readonly InferenceSession? _goreMatureSession;
    private readonly string _goreMatureInputName = "pixel_values";

    private readonly InferenceSession? _violenceSession;
    private readonly string _violenceInputName = "pixel_values";

    private readonly List<string> _loadErrors = [];

    public OpenSourceModerationProvider(
        IOptionsMonitor<ImageModerationOptions> optionsMonitor,
        ILogger<OpenSourceModerationProvider> logger)
    {
        _optionsMonitor = optionsMonitor ?? throw new ArgumentNullException(nameof(optionsMonitor));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

        var opts = _optionsMonitor.CurrentValue.OpenSource;
        _nudeNetResolution = opts.InputResolution > 0 ? opts.InputResolution : 320;
        _concurrencyGate = new SemaphoreSlim(
            Math.Max(1, opts.MaxConcurrentInferences),
            Math.Max(1, opts.MaxConcurrentInferences));

        // 1. Load NudeNet v3 ONNX session
        (_nudeNetSession, _nudeNetInputName) = TryLoadSession(
            opts.NudityModelPath,
            opts.BaseDirectory,
            "images",
            "NudeNet v3");

        // 2. Load SwiftFormer Gore & Mature ONNX session
        if (opts.EnableGoreAndMatureDetection)
        {
            (_goreMatureSession, _goreMatureInputName) = TryLoadSession(
                opts.GoreAndMatureModelPath,
                opts.BaseDirectory,
                "pixel_values",
                "SwiftFormer Gore/Mature");
        }

        // 3. Load ViT Violence ONNX session
        if (opts.EnableViolenceDetection)
        {
            (_violenceSession, _violenceInputName) = TryLoadSession(
                opts.ViolenceModelPath,
                opts.BaseDirectory,
                "pixel_values",
                "ViT Violence");
        }
    }

    public ModerationProvider ProviderType => ModerationProvider.OpenSource;

    public string ProviderName => ImageDetection.OpenSourceProviderName;

    public bool IsAvailable
    {
        get
        {
            var opts = _optionsMonitor.CurrentValue.OpenSource;
            return _nudeNetSession is not null &&
                   (!opts.EnableGoreAndMatureDetection || _goreMatureSession is not null) &&
                   (!opts.EnableViolenceDetection || _violenceSession is not null) &&
                   _loadErrors.Count == 0;
        }
    }

    public string? AvailabilityError =>
        _loadErrors.Count > 0 ? string.Join(" | ", _loadErrors) : null;

    public async Task<ProviderAnalysisResult> AnalyzeAsync(
        byte[] imageBytes,
        CancellationToken cancellationToken = default)
    {
        var stopwatch = Stopwatch.StartNew();

        if (!IsAvailable)
        {
            stopwatch.Stop();
            return ProviderAnalysisResult.Failure(
                ProviderName,
                ProviderType,
                AvailabilityError ?? "One or more open-source ONNX models are not loaded.",
                stopwatch.Elapsed);
        }

        if (imageBytes is null || imageBytes.Length == 0)
        {
            stopwatch.Stop();
            return ProviderAnalysisResult.Failure(
                ProviderName,
                ProviderType,
                "Image payload cannot be null or empty.",
                stopwatch.Elapsed);
        }

        await _concurrencyGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await Task.Run(() =>
            {
                cancellationToken.ThrowIfCancellationRequested();

                using var bitmap = DecodeBitmap(imageBytes);
                var opts = _optionsMonitor.CurrentValue.OpenSource;
                var detections = new List<ImageDetection>();

                // 1. NudeNet v3 Inference
                if (_nudeNetSession is not null)
                {
                    var nudityDetections = RunNudeNetInference(bitmap, opts);
                    detections.AddRange(nudityDetections);
                }

                // 2. SwiftFormer Gore & Mature Inference
                if (opts.EnableGoreAndMatureDetection && _goreMatureSession is not null)
                {
                    var goreDetections = RunGoreAndMatureInference(bitmap, opts);
                    detections.AddRange(goreDetections);
                }

                // 3. ViT Violence Inference
                if (opts.EnableViolenceDetection && _violenceSession is not null)
                {
                    var violenceDetections = RunViolenceInference(bitmap, opts);
                    detections.AddRange(violenceDetections);
                }

                stopwatch.Stop();
                return ProviderAnalysisResult.Success(
                    ProviderName,
                    ProviderType,
                    detections,
                    stopwatch.Elapsed);
            }, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            _logger.LogWarning(ex, "Open-source ONNX inference failed.");
            return ProviderAnalysisResult.Failure(
                ProviderName,
                ProviderType,
                $"Open-source ONNX inference failed: {ex.Message}",
                stopwatch.Elapsed);
        }
        finally
        {
            _concurrencyGate.Release();
        }
    }

    private IReadOnlyList<ImageDetection> RunNudeNetInference(
        SKBitmap bitmap,
        OpenSourceModerationProviderOptions opts)
    {
        var tensor = PreprocessNudeNet(bitmap, _nudeNetResolution, _nudeNetResolution, out var maxDimension);
        var inputs = new List<NamedOnnxValue>
        {
            NamedOnnxValue.CreateFromTensor(_nudeNetInputName, tensor)
        };

        using var results = _nudeNetSession!.Run(inputs);
        var firstOutput = results.FirstOrDefault()?.AsTensor<float>();
        if (firstOutput is null)
        {
            return Array.Empty<ImageDetection>();
        }

        return PostprocessNudeNet(
            firstOutput,
            maxDimension,
            bitmap.Width,
            bitmap.Height,
            _nudeNetResolution,
            _nudeNetResolution,
            (float)opts.MinimumDetectionConfidence,
            opts.NmsIouThreshold);
    }

    private IReadOnlyList<ImageDetection> RunGoreAndMatureInference(
        SKBitmap bitmap,
        OpenSourceModerationProviderOptions opts)
    {
        var tensor = PreprocessRgb255(bitmap, 224, 224);
        var inputs = new List<NamedOnnxValue>
        {
            NamedOnnxValue.CreateFromTensor(_goreMatureInputName, tensor)
        };

        using var results = _goreMatureSession!.Run(inputs);
        var output = results.FirstOrDefault()?.AsTensor<float>();
        if (output is null || output.Length < 3)
        {
            return Array.Empty<ImageDetection>();
        }

        float rawNsfl = output[0, 0];
        float rawNsfw = output[0, 1];
        float rawSfw = output[0, 2];

        float sum = rawNsfl + rawNsfw + rawSfw;
        double probNsfl, probNsfw;

        if (rawNsfl >= 0f && rawNsfw >= 0f && rawSfw >= 0f && Math.Abs(sum - 1.0f) < 0.05f)
        {
            probNsfl = rawNsfl;
            probNsfw = rawNsfw;
        }
        else
        {
            float maxLogit = Math.Max(rawNsfl, Math.Max(rawNsfw, rawSfw));
            double expNsfl = Math.Exp(rawNsfl - maxLogit);
            double expNsfw = Math.Exp(rawNsfw - maxLogit);
            double expSfw = Math.Exp(rawSfw - maxLogit);
            double denom = expNsfl + expNsfw + expSfw;
            probNsfl = expNsfl / denom;
            probNsfw = expNsfw / denom;
        }

        var detections = new List<ImageDetection>(2);

        if (probNsfl >= opts.GoreMinimumConfidence)
        {
            detections.Add(ImageDetection.FromOpenSource(
                ModerationCategory.Gore,
                "GORE",
                probNsfl));
        }

        if (probNsfw >= opts.MatureContentMinimumConfidence)
        {
            detections.Add(ImageDetection.FromOpenSource(
                ModerationCategory.MatureContent,
                "MATURE_CONTENT",
                probNsfw));
        }
        else if (probNsfw >= opts.SuggestiveContentMinimumConfidence)
        {
            detections.Add(ImageDetection.FromOpenSource(
                ModerationCategory.SuggestiveContent,
                "SUGGESTIVE_CONTENT",
                probNsfw));
        }

        return detections;
    }

    private IReadOnlyList<ImageDetection> RunViolenceInference(
        SKBitmap bitmap,
        OpenSourceModerationProviderOptions opts)
    {
        var tensor = PreprocessViT(bitmap, 224, 224);
        var inputs = new List<NamedOnnxValue>
        {
            NamedOnnxValue.CreateFromTensor(_violenceInputName, tensor)
        };

        using var results = _violenceSession!.Run(inputs);
        var output = results.FirstOrDefault()?.AsTensor<float>();
        if (output is null || output.Length < 2)
        {
            return Array.Empty<ImageDetection>();
        }

        float logitNonViolence = output[0, 0];
        float logitViolence = output[0, 1];
        float maxLogit = Math.Max(logitNonViolence, logitViolence);
        double expNonV = Math.Exp(logitNonViolence - maxLogit);
        double expV = Math.Exp(logitViolence - maxLogit);
        double probViolence = expV / (expNonV + expV);

        if (probViolence >= opts.ViolenceMinimumConfidence)
        {
            return
            [
                ImageDetection.FromOpenSource(
                    ModerationCategory.Violence,
                    "VIOLENCE",
                    probViolence)
            ];
        }

        return Array.Empty<ImageDetection>();
    }

    internal static DenseTensor<float> PreprocessNudeNet(
        SKBitmap sourceBitmap,
        int targetWidth,
        int targetHeight,
        out int maxDimension)
    {
        var origWidth = sourceBitmap.Width;
        var origHeight = sourceBitmap.Height;
        maxDimension = Math.Max(origWidth, origHeight);

        var scale = Math.Min((float)targetWidth / maxDimension, (float)targetHeight / maxDimension);
        var newWidth = Math.Max(1, (int)Math.Round(origWidth * scale));
        var newHeight = Math.Max(1, (int)Math.Round(origHeight * scale));

        using var canvasBitmap = new SKBitmap(targetWidth, targetHeight, SKColorType.Rgba8888, SKAlphaType.Unpremul);
        using (var canvas = new SKCanvas(canvasBitmap))
        {
            canvas.Clear(SKColors.Black);
            using var resized = sourceBitmap.Resize(
                new SKImageInfo(newWidth, newHeight, SKColorType.Rgba8888, SKAlphaType.Unpremul),
                SKFilterQuality.Medium);

            if (resized is not null)
            {
                canvas.DrawBitmap(resized, 0, 0);
            }
        }

        var tensor = new DenseTensor<float>([1, 3, targetHeight, targetWidth]);
        var pixels = canvasBitmap.GetPixelSpan();
        const float inv255 = 1.0f / 255.0f;

        for (var y = 0; y < targetHeight; y++)
        {
            var rowOffset = y * targetWidth * 4;
            for (var x = 0; x < targetWidth; x++)
            {
                var idx = rowOffset + (x * 4);
                tensor[0, 0, y, x] = pixels[idx] * inv255;
                tensor[0, 1, y, x] = pixels[idx + 1] * inv255;
                tensor[0, 2, y, x] = pixels[idx + 2] * inv255;
            }
        }

        return tensor;
    }

    internal static IReadOnlyList<ImageDetection> PostprocessNudeNet(
        Tensor<float> output,
        int maxDimension,
        int originalWidth,
        int originalHeight,
        int modelWidth,
        int modelHeight,
        float minConfidence,
        float nmsIouThreshold)
    {
        var dimensions = output.Dimensions;
        if (dimensions.Length != 3)
        {
            return Array.Empty<ImageDetection>();
        }

        var channelsFirst = dimensions[1] < dimensions[2];
        var numFeatures = channelsFirst ? dimensions[1] : dimensions[2];
        var numAnchors = channelsFirst ? dimensions[2] : dimensions[1];
        var numClasses = Math.Min(NudeNetClassLabels.Length, numFeatures - 4);
        if (numClasses <= 0)
        {
            return Array.Empty<ImageDetection>();
        }

        var candidates = new List<(int ClassId, float Score, float X, float Y, float W, float H)>();
        var xFactor = (float)maxDimension / modelWidth;
        var yFactor = (float)maxDimension / modelHeight;

        for (var a = 0; a < numAnchors; a++)
        {
            var maxScore = 0f;
            var maxClassId = -1;

            for (var c = 0; c < numClasses; c++)
            {
                var score = channelsFirst ? output[0, 4 + c, a] : output[0, a, 4 + c];
                if (score > maxScore)
                {
                    maxScore = score;
                    maxClassId = c;
                }
            }

            if (maxScore < minConfidence || maxClassId < 0)
            {
                continue;
            }

            var cx = channelsFirst ? output[0, 0, a] : output[0, a, 0];
            var cy = channelsFirst ? output[0, 1, a] : output[0, a, 1];
            var w = channelsFirst ? output[0, 2, a] : output[0, a, 2];
            var h = channelsFirst ? output[0, 3, a] : output[0, a, 3];

            var left = (cx - (w * 0.5f)) * xFactor;
            var top = (cy - (h * 0.5f)) * yFactor;
            var width = w * xFactor;
            var height = h * yFactor;

            left = Math.Clamp(left, 0, originalWidth);
            top = Math.Clamp(top, 0, originalHeight);
            width = Math.Clamp(width, 0, originalWidth - left);
            height = Math.Clamp(height, 0, originalHeight - top);

            if (width >= 1f && height >= 1f)
            {
                candidates.Add((maxClassId, maxScore, left, top, width, height));
            }
        }

        if (candidates.Count == 0)
        {
            return Array.Empty<ImageDetection>();
        }

        candidates.Sort((a, b) => b.Score.CompareTo(a.Score));
        var kept = new List<(int ClassId, float Score, float X, float Y, float W, float H)>();

        foreach (var cand in candidates)
        {
            var suppressed = false;
            foreach (var existing in kept)
            {
                if (ComputeIoU(cand, existing) > nmsIouThreshold)
                {
                    suppressed = true;
                    break;
                }
            }

            if (!suppressed)
            {
                kept.Add(cand);
            }
        }

        var results = new List<ImageDetection>(kept.Count);
        foreach (var item in kept)
        {
            var rawLabel = NudeNetClassLabels[item.ClassId];
            var category = BenignAnatomicalLabels.Contains(rawLabel)
                ? ModerationCategory.Anatomical
                : ModerationCategory.Nudity;

            results.Add(ImageDetection.FromOpenSource(
                category,
                rawLabel,
                item.Score,
                new ImageBoundingBox(
                    (int)Math.Round(item.X),
                    (int)Math.Round(item.Y),
                    (int)Math.Round(item.W),
                    (int)Math.Round(item.H))));
        }

        return results;
    }

    internal static DenseTensor<float> PreprocessRgb255(SKBitmap sourceBitmap, int targetWidth, int targetHeight)
    {
        using var resized = sourceBitmap.Resize(
            new SKImageInfo(targetWidth, targetHeight, SKColorType.Rgba8888, SKAlphaType.Unpremul),
            SKFilterQuality.Medium);

        var working = resized ?? sourceBitmap;
        var tensor = new DenseTensor<float>([1, 3, targetHeight, targetWidth]);
        var pixels = working.GetPixelSpan();

        for (var y = 0; y < targetHeight; y++)
        {
            var rowOffset = y * targetWidth * 4;
            for (var x = 0; x < targetWidth; x++)
            {
                var idx = rowOffset + (x * 4);
                tensor[0, 0, y, x] = pixels[idx];
                tensor[0, 1, y, x] = pixels[idx + 1];
                tensor[0, 2, y, x] = pixels[idx + 2];
            }
        }

        return tensor;
    }

    internal static DenseTensor<float> PreprocessViT(SKBitmap sourceBitmap, int targetWidth, int targetHeight)
    {
        using var resized = sourceBitmap.Resize(
            new SKImageInfo(targetWidth, targetHeight, SKColorType.Rgba8888, SKAlphaType.Unpremul),
            SKFilterQuality.Medium);

        var working = resized ?? sourceBitmap;
        var tensor = new DenseTensor<float>([1, 3, targetHeight, targetWidth]);
        var pixels = working.GetPixelSpan();
        const float scale = 2.0f / 255.0f;

        for (var y = 0; y < targetHeight; y++)
        {
            var rowOffset = y * targetWidth * 4;
            for (var x = 0; x < targetWidth; x++)
            {
                var idx = rowOffset + (x * 4);
                tensor[0, 0, y, x] = (pixels[idx] * scale) - 1.0f;
                tensor[0, 1, y, x] = (pixels[idx + 1] * scale) - 1.0f;
                tensor[0, 2, y, x] = (pixels[idx + 2] * scale) - 1.0f;
            }
        }

        return tensor;
    }

    private static float ComputeIoU(
        (int ClassId, float Score, float X, float Y, float W, float H) a,
        (int ClassId, float Score, float X, float Y, float W, float H) b)
    {
        var x1 = Math.Max(a.X, b.X);
        var y1 = Math.Max(a.Y, b.Y);
        var x2 = Math.Min(a.X + a.W, b.X + b.W);
        var y2 = Math.Min(a.Y + a.H, b.Y + b.H);

        var interW = Math.Max(0f, x2 - x1);
        var interH = Math.Max(0f, y2 - y1);
        var interArea = interW * interH;
        if (interArea <= 0f)
        {
            return 0f;
        }

        var union = (a.W * a.H) + (b.W * b.H) - interArea;
        return union <= 0f ? 0f : interArea / union;
    }

    private static SKBitmap DecodeBitmap(byte[] imageBytes)
    {
        using var stream = new SKMemoryStream(imageBytes);
        using var codec = SKCodec.Create(stream)
            ?? throw new InvalidOperationException("Failed to decode image header.");

        var info = new SKImageInfo(codec.Info.Width, codec.Info.Height, SKColorType.Rgba8888, SKAlphaType.Unpremul);
        var bitmap = new SKBitmap(info);
        var result = codec.GetPixels(info, bitmap.GetPixels());
        if (result != SKCodecResult.Success && result != SKCodecResult.IncompleteInput)
        {
            bitmap.Dispose();
            throw new InvalidOperationException($"Failed to decode image pixels ({result}).");
        }

        return bitmap;
    }

    private (InferenceSession? Session, string InputName) TryLoadSession(
        string configuredPath,
        string? baseDirectory,
        string defaultInputName,
        string label)
    {
        try
        {
            var resolved = ResolveModelPath(configuredPath, baseDirectory);
            if (!File.Exists(resolved))
            {
                var err = $"{label} ONNX model file not found at '{resolved}'.";
                _loadErrors.Add(err);
                _logger.LogWarning("{Error}", err);
                return (null, defaultInputName);
            }

            var sessionOptions = new SessionOptions
            {
                GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL,
                ExecutionMode = ExecutionMode.ORT_SEQUENTIAL,
                InterOpNumThreads = 1,
                IntraOpNumThreads = Math.Max(1, Environment.ProcessorCount / 2)
            };

            var session = new InferenceSession(resolved, sessionOptions);
            var inputName = session.InputMetadata.Keys.FirstOrDefault() ?? defaultInputName;
            return (session, inputName);
        }
        catch (Exception ex)
        {
            var err = $"Failed to initialize {label} ONNX model: {ex.Message}";
            _loadErrors.Add(err);
            _logger.LogError(ex, "{Error}", err);
            return (null, defaultInputName);
        }
    }

    private static string ResolveModelPath(string configuredPath, string? baseDirectory)
    {
        if (Path.IsPathRooted(configuredPath))
        {
            return configuredPath;
        }

        if (!string.IsNullOrWhiteSpace(baseDirectory))
        {
            var candidate = Path.GetFullPath(Path.Combine(baseDirectory, configuredPath));
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        var fromCurrentDir = Path.GetFullPath(Path.Combine(Directory.GetCurrentDirectory(), configuredPath));
        if (File.Exists(fromCurrentDir))
        {
            return fromCurrentDir;
        }

        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, configuredPath);
            if (File.Exists(candidate))
            {
                return candidate;
            }
            dir = dir.Parent;
        }

        return fromCurrentDir;
    }

    public void Dispose()
    {
        _nudeNetSession?.Dispose();
        _goreMatureSession?.Dispose();
        _violenceSession?.Dispose();
        _concurrencyGate.Dispose();
    }
}
