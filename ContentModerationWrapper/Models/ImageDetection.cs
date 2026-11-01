using System.Text.Json.Serialization;

namespace ContentModeration.Models;

/// <summary>
/// Pixel bounding box for localized object detections (e.g., NudeNet v3 anatomical regions).
/// </summary>
public sealed record ImageBoundingBox(
    [property: JsonPropertyName("x")] int X,
    [property: JsonPropertyName("y")] int Y,
    [property: JsonPropertyName("width")] int Width,
    [property: JsonPropertyName("height")] int Height);

/// <summary>
/// Represents a single content moderation finding from an <see cref="Abstractions.IImageModerationProvider"/>.
/// Strictly preserves the distinction between open-source model <see cref="Confidence"/> (0.0–1.0)
/// and Azure AI Content Safety <see cref="Severity"/> (0, 2, 4, 6).
/// </summary>
public sealed record ImageDetection
{
    public const string OpenSourceProviderName = "OpenSourceModel";
    public const string AzureProviderName = "AzureAIContentSafety";

    [JsonConstructor]
    public ImageDetection(
        ModerationCategory category,
        string rawCategory,
        string providerName,
        double? confidence = null,
        int? severity = null,
        ImageBoundingBox? boundingBox = null)
    {
        if (string.IsNullOrWhiteSpace(rawCategory))
        {
            throw new ArgumentException("Raw category label cannot be empty.", nameof(rawCategory));
        }

        if (string.IsNullOrWhiteSpace(providerName))
        {
            throw new ArgumentException("Provider name cannot be empty.", nameof(providerName));
        }

        Category = category;
        RawCategory = rawCategory;
        ProviderName = providerName;
        Confidence = confidence;
        Severity = severity;
        BoundingBox = boundingBox;
    }

    /// <summary>
    /// Creates an open-source model detection with a normalized probability confidence score (0.0–1.0).
    /// </summary>
    public static ImageDetection FromOpenSource(
        ModerationCategory category,
        string rawCategory,
        double confidence,
        ImageBoundingBox? boundingBox = null,
        string providerName = OpenSourceProviderName) =>
        new(
            category: category,
            rawCategory: rawCategory,
            providerName: providerName,
            confidence: Math.Round(confidence, 4),
            severity: null,
            boundingBox: boundingBox);

    /// <summary>
    /// Creates an Azure AI Content Safety detection with both an integer severity score (0, 2, 4, 6)
    /// and a corresponding confidence level percentage (0.0–1.0).
    /// </summary>
    public static ImageDetection FromAzure(
        ModerationCategory category,
        string rawCategory,
        int severity,
        string providerName = AzureProviderName) =>
        new(
            category: category,
            rawCategory: rawCategory,
            providerName: providerName,
            confidence: Math.Round(Math.Clamp((double)severity / 6.0, 0.0, 1.0), 4),
            severity: severity,
            boundingBox: null);

    /// <summary>
    /// High-level normalized moderation category (Nudity, SexualContent, SuggestiveContent, Gore, Violence, etc.).
    /// </summary>
    [JsonPropertyName("category")]
    public ModerationCategory Category { get; init; }

    /// <summary>
    /// Provider-specific category label (e.g., "FEMALE_BREAST_EXPOSED", "GORE", "VIOLENCE", "SEXUAL_CONTENT").
    /// </summary>
    [JsonPropertyName("rawCategory")]
    public string RawCategory { get; init; }

    /// <summary>
    /// Name of the provider that produced this detection ("OpenSourceModel" or "AzureAIContentSafety").
    /// </summary>
    [JsonPropertyName("providerName")]
    public string ProviderName { get; init; }

    /// <summary>
    /// Model confidence score in [0.0, 1.0]. Normalized from severity (severity / 6) for Azure AI Content Safety.
    /// </summary>
    [JsonPropertyName("confidence")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double? Confidence { get; init; }

    /// <summary>
    /// Confidence expressed as a percentage in [0.0, 100.0] (e.g., 66.7 for 66.7%).
    /// </summary>
    [JsonPropertyName("confidencePercent")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double? ConfidencePercent => Confidence.HasValue ? Math.Round(Confidence.Value * 100.0, 1) : null;

    /// <summary>
    /// Azure AI Content Safety integer severity score (0, 2, 4, 6). Null for open-source ONNX models.
    /// </summary>
    [JsonPropertyName("severity")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? Severity { get; init; }

    /// <summary>
    /// Optional localized bounding box in image pixel coordinates.
    /// </summary>
    [JsonPropertyName("boundingBox")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public ImageBoundingBox? BoundingBox { get; init; }
}
