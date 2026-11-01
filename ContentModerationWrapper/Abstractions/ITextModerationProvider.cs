using ContentModeration.Models;

namespace ContentModeration.Abstractions;

/// <summary>
/// Common contract implemented by individual text moderation providers
/// (such as <see cref="Providers.Azure.AzureTextModerationProvider"/> and
/// <see cref="Providers.OpenSource.OpenSourceTextModerationProvider"/>).
/// </summary>
public interface ITextModerationProvider
{
    /// <summary>
    /// Gets the provider enum identifier (<see cref="ModerationProvider.Azure"/> or <see cref="ModerationProvider.OpenSource"/>).
    /// </summary>
    ModerationProvider ProviderType { get; }

    /// <summary>
    /// Gets the canonical provider name reported in <see cref="TextDetection.ProviderName"/>.
    /// </summary>
    string ProviderName { get; }

    /// <summary>
    /// Gets whether the provider is properly configured and ready to analyze text.
    /// </summary>
    bool IsAvailable { get; }

    /// <summary>
    /// Gets an optional diagnostic message when <see cref="IsAvailable"/> is false.
    /// </summary>
    string? AvailabilityError { get; }

    /// <summary>
    /// Analyzes the input text for harmful categories and returns provider-specific detections
    /// (with either Azure severity 0–6 or open-source confidence 0.0–1.0).
    /// </summary>
    Task<TextProviderAnalysisResult> AnalyzeTextAsync(
        string text,
        CancellationToken cancellationToken = default);
}
