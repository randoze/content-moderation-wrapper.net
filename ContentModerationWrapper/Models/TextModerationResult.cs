using System.Text.Json.Serialization;

namespace ContentModeration.Models;

/// <summary>
/// Result returned by an individual <see cref="Abstractions.ITextModerationProvider"/>.
/// </summary>
public sealed record TextProviderAnalysisResult(
    string ProviderName,
    ModerationProvider ProviderType,
    bool Succeeded,
    IReadOnlyList<TextDetection> Detections,
    TimeSpan Duration,
    string? ErrorMessage = null)
{
    public static TextProviderAnalysisResult Success(
        string providerName,
        ModerationProvider providerType,
        IReadOnlyList<TextDetection> detections,
        TimeSpan duration) =>
        new(providerName, providerType, true, detections, duration, null);

    public static TextProviderAnalysisResult Failure(
        string providerName,
        ModerationProvider providerType,
        string errorMessage,
        TimeSpan duration) =>
        new(providerName, providerType, false, Array.Empty<TextDetection>(), duration, errorMessage);
}

/// <summary>
/// Structured text moderation result returned by <see cref="Abstractions.ITextModerationService"/>.
/// </summary>
public sealed record TextModerationResult(
    [property: JsonPropertyName("isFlagged")] bool IsFlagged,
    [property: JsonPropertyName("isViolation")] bool IsViolation,
    [property: JsonPropertyName("classification")] TextModerationClassification Classification,
    [property: JsonPropertyName("detections")] IReadOnlyList<TextDetection> Detections,
    [property: JsonPropertyName("providerName")] string ProviderName,
    [property: JsonPropertyName("providersUsed")] IReadOnlyList<string> ProvidersUsed,
    [property: JsonPropertyName("processingDuration")] TimeSpan ProcessingDuration,
    [property: JsonPropertyName("fallbackUsed")] bool FallbackUsed = false,
    [property: JsonPropertyName("requiresManualReview"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] bool? RequiresManualReview = null,
    [property: JsonPropertyName("errors"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<string>? Errors = null)
{
    /// <summary>
    /// Processing duration in milliseconds.
    /// </summary>
    [JsonPropertyName("processingTimeMs")]
    public long ProcessingTimeMs => (long)Math.Round(ProcessingDuration.TotalMilliseconds);
}
