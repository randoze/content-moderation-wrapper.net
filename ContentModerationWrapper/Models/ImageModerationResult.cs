using System.Text.Json.Serialization;

namespace ContentModeration.Models;

/// <summary>
/// Result returned by an individual <see cref="Abstractions.IImageModerationProvider"/>.
/// </summary>
public sealed record ProviderAnalysisResult(
    string ProviderName,
    ModerationProvider ProviderType,
    bool Succeeded,
    IReadOnlyList<ImageDetection> Detections,
    TimeSpan Duration,
    string? ErrorMessage = null)
{
    public static ProviderAnalysisResult Success(
        string providerName,
        ModerationProvider providerType,
        IReadOnlyList<ImageDetection> detections,
        TimeSpan duration) =>
        new(providerName, providerType, true, detections, duration, null);

    public static ProviderAnalysisResult Failure(
        string providerName,
        ModerationProvider providerType,
        string errorMessage,
        TimeSpan duration) =>
        new(providerName, providerType, false, Array.Empty<ImageDetection>(), duration, errorMessage);
}

/// <summary>
/// Comprehensive result returned by <see cref="Abstractions.IImageModerationService"/>.
/// </summary>
public sealed record ImageModerationResult(
    [property: JsonPropertyName("isFlagged")] bool IsFlagged,
    [property: JsonPropertyName("classification")] ImageModerationClassification Classification,
    [property: JsonPropertyName("detections")] IReadOnlyList<ImageDetection> Detections,
    [property: JsonPropertyName("providersUsed")] IReadOnlyList<string> ProvidersUsed,
    [property: JsonPropertyName("processingDuration")] TimeSpan ProcessingDuration,
    [property: JsonPropertyName("fallbackUsed")] bool FallbackUsed = false,
    [property: JsonPropertyName("errors")] IReadOnlyList<string>? Errors = null,
    [property: JsonPropertyName("providerAvailability")] IReadOnlyDictionary<string, bool>? ProviderAvailability = null)
{
    /// <summary>
    /// Processing duration in milliseconds.
    /// </summary>
    [JsonPropertyName("processingTimeMs")]
    public long ProcessingTimeMs => (long)Math.Round(ProcessingDuration.TotalMilliseconds);
}
