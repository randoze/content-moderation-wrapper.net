using System.Text.Json.Serialization;

namespace ContentModeration.Models;

/// <summary>
/// Represents a single harmful text detection from an <see cref="Abstractions.ITextModerationProvider"/>.
/// Strictly preserves the distinction between Azure AI Content Safety <see cref="Severity"/> (0, 2, 4, 6)
/// and open-source model <see cref="Confidence"/> (0.0–1.0).
/// </summary>
public sealed record TextDetection
{
    public const string AzureProviderName = "AzureAIContentSafety";
    public const string OpenSourceProviderName = "OpenSourceModel";

    [JsonConstructor]
    public TextDetection(
        TextModerationCategory category,
        string rawCategory,
        string providerName,
        double? confidence = null,
        int? severity = null,
        string? description = null)
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
        Description = description;
    }

    /// <summary>
    /// Converts an Azure AI Content Safety integer severity (0, 2, 4, 6) into a normalized confidence score (0.0–1.0).
    /// </summary>
    public static double SeverityToConfidence(int severity) =>
        Math.Round(Math.Clamp((double)severity / 6.0, 0.0, 1.0), 4);

    /// <summary>
    /// Creates an Azure AI Content Safety text detection with both an integer severity score (0, 2, 4, 6)
    /// and a corresponding confidence level percentage (0.0–1.0).
    /// </summary>
    public static TextDetection FromAzure(
        TextModerationCategory category,
        string rawCategory,
        int severity,
        string? description = null,
        string providerName = AzureProviderName) =>
        new(
            category: category,
            rawCategory: rawCategory,
            providerName: providerName,
            confidence: SeverityToConfidence(severity),
            severity: severity,
            description: description);

    /// <summary>
    /// Creates an open-source model text detection with a normalized confidence score (0.0–1.0).
    /// </summary>
    public static TextDetection FromOpenSource(
        TextModerationCategory category,
        string rawCategory,
        double confidence,
        string? description = null,
        string providerName = OpenSourceProviderName) =>
        new(
            category: category,
            rawCategory: rawCategory,
            providerName: providerName,
            confidence: Math.Round(confidence, 4),
            severity: null,
            description: description);

    [JsonPropertyName("category")]
    public TextModerationCategory Category { get; init; }

    [JsonPropertyName("rawCategory")]
    public string RawCategory { get; init; }

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

    [JsonPropertyName("severity")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? Severity { get; init; }

    [JsonPropertyName("description")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Description { get; init; }
}
