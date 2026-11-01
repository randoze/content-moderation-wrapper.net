using ContentModeration.Models;

namespace ContentModeration.Abstractions;

/// <summary>
/// Common contract implemented by individual image content moderation providers
/// (such as <see cref="Providers.Azure.AzureContentSafetyProvider"/> and
/// <see cref="Providers.OpenSource.OpenSourceModerationProvider"/>).
/// </summary>
public interface IImageModerationProvider
{
    /// <summary>
    /// Gets the provider enum identifier (<see cref="ModerationProvider.Azure"/> or <see cref="ModerationProvider.OpenSource"/>).
    /// </summary>
    ModerationProvider ProviderType { get; }

    /// <summary>
    /// Gets the canonical provider name reported in <see cref="ImageDetection.ProviderName"/>.
    /// </summary>
    string ProviderName { get; }

    /// <summary>
    /// Gets whether the provider is properly configured and its models/credentials are ready.
    /// </summary>
    bool IsAvailable { get; }

    /// <summary>
    /// Gets an optional diagnostic message if <see cref="IsAvailable"/> is false.
    /// </summary>
    string? AvailabilityError { get; }

    /// <summary>
    /// Analyzes raw image bytes and returns provider-specific detections (with either confidence 0.0–1.0 or Azure severity 0–6).
    /// </summary>
    /// <param name="imageBytes">Raw encoded image bytes (JPEG, PNG, WebP, GIF, or BMP).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<ProviderAnalysisResult> AnalyzeAsync(
        byte[] imageBytes,
        CancellationToken cancellationToken = default);
}
