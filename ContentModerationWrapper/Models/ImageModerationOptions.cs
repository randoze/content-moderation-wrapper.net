namespace ContentModeration.Models;

/// <summary>
/// Configuration options for the reusable <c>ImageModeration</c> library.
/// Can be bound from the <c>"ImageModeration"</c> configuration section or configured in code via <c>AddImageModeration</c>.
/// </summary>
public sealed class ImageModerationOptions
{
    public const string SectionName = "ImageModeration";

    /// <summary>
    /// Primary moderation provider to use (<see cref="ModerationProvider.Azure"/>,
    /// <see cref="ModerationProvider.OpenSource"/>, or <see cref="ModerationProvider.Hybrid"/>).
    /// </summary>
    public ModerationProvider Provider { get; set; } = ModerationProvider.Hybrid;

    /// <summary>
    /// Fallback provider to invoke when <see cref="EnableFallback"/> is true and the primary provider fails or is unavailable.
    /// </summary>
    public ModerationProvider? FallbackProvider { get; set; } = ModerationProvider.OpenSource;

    /// <summary>
    /// Whether to automatically fall back to <see cref="FallbackProvider"/> when the primary provider fails.
    /// </summary>
    public bool EnableFallback { get; set; } = true;

    /// <summary>
    /// When true and all configured providers fail (or fallback is disabled on failure), returns
    /// <see cref="ImageModerationClassification.ReviewRequired"/> with <c>IsFlagged = true</c> instead of throwing.
    /// </summary>
    public bool ReturnReviewRequiredOnFailure { get; set; } = false;

    /// <summary>
    /// In <see cref="ModerationProvider.Hybrid"/> mode, skips calling Azure AI Content Safety when local open-source
    /// models already classify the image as <see cref="ImageModerationClassification.Explicit"/>.
    /// </summary>
    public bool SkipAzureWhenLocalBlocks { get; set; } = true;

    /// <summary>
    /// Overall timeout in seconds for a moderation request.
    /// </summary>
    public int TimeoutSeconds { get; set; } = 15;

    /// <summary>
    /// Maximum allowed image size in bytes (default 10 MB).
    /// </summary>
    public long MaxFileSizeBytes { get; set; } = 10 * 1024 * 1024;

    /// <summary>
    /// Maximum allowed width or height in pixels (default 8192).
    /// </summary>
    public int MaxImageDimension { get; set; } = 8192;

    /// <summary>
    /// Maximum total pixels (width * height) to prevent decompression bombs (default 33,554,432).
    /// </summary>
    public long MaxTotalPixels { get; set; } = 33_554_432;

    /// <summary>
    /// Whether <see cref="ImageModerationClassification.PotentiallyMature"/> classifications should set <c>IsFlagged = true</c>.
    /// </summary>
    public bool FlagPotentiallyMature { get; set; } = true;

    /// <summary>
    /// Azure AI Content Safety provider settings.
    /// </summary>
    public AzureModerationProviderOptions Azure { get; set; } = new();

    /// <summary>
    /// Open-source ONNX models provider settings.
    /// </summary>
    public OpenSourceModerationProviderOptions OpenSource { get; set; } = new();
}

/// <summary>
/// Configuration options for <see cref="Providers.Azure.AzureContentSafetyProvider"/>.
/// </summary>
public sealed class AzureModerationProviderOptions
{
    /// <summary>
    /// Azure AI Content Safety endpoint URL (e.g., https://&lt;resource&gt;.cognitiveservices.azure.com/).
    /// Can also be supplied via <c>AZURE_CONTENT_SAFETY_ENDPOINT</c>.
    /// </summary>
    public string Endpoint { get; set; } = string.Empty;

    /// <summary>
    /// Azure AI Content Safety API key. Store in User Secrets or environment variables (<c>AZURE_CONTENT_SAFETY_API_KEY</c>).
    /// </summary>
    public string ApiKey { get; set; } = string.Empty;

    /// <summary>
    /// Timeout in seconds for each Azure API call.
    /// </summary>
    public int TimeoutSeconds { get; set; } = 8;

    /// <summary>
    /// Minimum Azure severity score (0, 2, 4, 6) to include in <see cref="ImageModerationResult.Detections"/>.
    /// </summary>
    public int MinimumReportedSeverity { get; set; } = 2;

    /// <summary>
    /// Azure severity threshold (0–6) that triggers <see cref="ImageModerationClassification.PotentiallyMature"/>.
    /// </summary>
    public int PotentiallyMatureSeverityThreshold { get; set; } = 2;

    /// <summary>
    /// Azure severity threshold (0–6) that triggers <see cref="ImageModerationClassification.Explicit"/>.
    /// </summary>
    public int ExplicitSeverityThreshold { get; set; } = 4;

    /// <summary>
    /// Optional per-category overrides for Azure explicit severity thresholds (e.g., "VIOLENCE": 4).
    /// </summary>
    public Dictionary<string, int> CategoryExplicitSeverityThresholds { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Optional per-category overrides for Azure potentially mature severity thresholds.
    /// </summary>
    public Dictionary<string, int> CategoryPotentiallyMatureSeverityThresholds { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

/// <summary>
/// Configuration options for <see cref="Providers.OpenSource.OpenSourceModerationProvider"/>.
/// </summary>
public sealed class OpenSourceModerationProviderOptions
{
    /// <summary>
    /// Optional base directory used to resolve relative ONNX model paths. Defaults to current working directory / AppContext.BaseDirectory.
    /// </summary>
    public string? BaseDirectory { get; set; }

    /// <summary>
    /// Path to NudeNet v3 ONNX model (<c>320n.onnx</c>).
    /// </summary>
    public string NudityModelPath { get; set; } = "models/320n.onnx";

    /// <summary>
    /// Input tensor width/height for NudeNet (default 320).
    /// </summary>
    public int InputResolution { get; set; } = 320;

    /// <summary>
    /// Whether to run the SwiftFormer gore/mature/suggestive ONNX model.
    /// </summary>
    public bool EnableGoreAndMatureDetection { get; set; } = true;

    /// <summary>
    /// Path to SwiftFormer ONNX model (<c>image-safety-classifier-xs.onnx</c>).
    /// </summary>
    public string GoreAndMatureModelPath { get; set; } = "models/image-safety-classifier-xs.onnx";

    /// <summary>
    /// Minimum probability (0.0–1.0) to report <c>GORE</c>.
    /// </summary>
    public double GoreMinimumConfidence { get; set; } = 0.50;

    /// <summary>
    /// Minimum probability (0.0–1.0) to report <c>MATURE_CONTENT</c>.
    /// </summary>
    public double MatureContentMinimumConfidence { get; set; } = 0.75;

    /// <summary>
    /// Minimum probability (0.0–1.0) to report <c>SUGGESTIVE_CONTENT</c>.
    /// </summary>
    public double SuggestiveContentMinimumConfidence { get; set; } = 0.60;

    /// <summary>
    /// Whether to run the ViT violence detection ONNX model.
    /// </summary>
    public bool EnableViolenceDetection { get; set; } = true;

    /// <summary>
    /// Path to ViT violence detection ONNX model (<c>vit-violence-detection.onnx</c>).
    /// </summary>
    public string ViolenceModelPath { get; set; } = "models/vit-violence-detection.onnx";

    /// <summary>
    /// Minimum probability (0.0–1.0) to report <c>VIOLENCE</c>.
    /// </summary>
    public double ViolenceMinimumConfidence { get; set; } = 0.70;

    /// <summary>
    /// Minimum confidence (0.0–1.0) for NudeNet bounding-box detections.
    /// </summary>
    public double MinimumDetectionConfidence { get; set; } = 0.25;

    /// <summary>
    /// Non-Maximum Suppression IoU threshold for NudeNet boxes.
    /// </summary>
    public float NmsIouThreshold { get; set; } = 0.45f;

    /// <summary>
    /// Maximum concurrent ONNX inference executions.
    /// </summary>
    public int MaxConcurrentInferences { get; set; } = 4;

    /// <summary>
    /// Global confidence threshold (0.0–1.0) for explicit open-source categories.
    /// </summary>
    public double ExplicitThreshold { get; set; } = 0.60;

    /// <summary>
    /// Global confidence threshold (0.0–1.0) for potentially mature open-source categories.
    /// </summary>
    public double PotentiallyMatureThreshold { get; set; } = 0.40;

    /// <summary>
    /// Per-category explicit confidence threshold overrides (e.g., "GORE": 0.85).
    /// </summary>
    public Dictionary<string, double> CategoryExplicitThresholds { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Per-category potentially mature confidence threshold overrides.
    /// </summary>
    public Dictionary<string, double> CategoryPotentiallyMatureThresholds { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}
