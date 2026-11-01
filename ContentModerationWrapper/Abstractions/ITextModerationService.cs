using ContentModeration.Models;

namespace ContentModeration.Abstractions;

/// <summary>
/// Primary application-facing abstraction for analyzing text for sexual content, hate speech and harassment,
/// threats and violence, self-harm encouragement, and profanity/abusive language.
/// Independent of ASP.NET Core HTTP/controller types and independent of image moderation.
/// </summary>
public interface ITextModerationService
{
    /// <summary>
    /// Analyzes the given text using the configured primary and fallback <see cref="ITextModerationProvider"/> instances,
    /// with an optional per-call provider override.
    /// </summary>
    /// <param name="text">Input text to moderate.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <param name="providerOverride">Optional provider override (<see cref="ModerationProvider.Azure"/>, <see cref="ModerationProvider.OpenSource"/>, or <see cref="ModerationProvider.Hybrid"/>).</param>
    Task<TextModerationResult> AnalyzeTextAsync(
        string text,
        CancellationToken cancellationToken = default,
        ModerationProvider? providerOverride = null);

    /// <summary>
    /// Returns the availability status of all registered <see cref="ITextModerationProvider"/> instances.
    /// </summary>
    IReadOnlyDictionary<string, bool> GetProviderAvailability();
}
