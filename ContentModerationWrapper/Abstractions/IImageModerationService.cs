using ContentModeration.Models;

namespace ContentModeration.Abstractions;

/// <summary>
/// Primary application-facing abstraction for analyzing images for nudity, sexual content,
/// suggestive content, gore, violence, and mature content using configured <see cref="IImageModerationProvider"/> engines.
/// Independent of ASP.NET Core HTTP/controller types.
/// </summary>
public interface IImageModerationService
{
    /// <summary>
    /// Analyzes an image stream using the configured primary and fallback <see cref="IImageModerationProvider"/> instances.
    /// </summary>
    /// <param name="imageStream">Readable stream containing the encoded image.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<ImageModerationResult> AnalyzeAsync(
        Stream imageStream,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Analyzes raw encoded image bytes using the configured primary and fallback <see cref="IImageModerationProvider"/> instances.
    /// </summary>
    /// <param name="imageBytes">Encoded image bytes (JPEG, PNG, WebP, GIF, or BMP).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<ImageModerationResult> AnalyzeAsync(
        byte[] imageBytes,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns the availability status of all registered <see cref="IImageModerationProvider"/> instances.
    /// </summary>
    IReadOnlyDictionary<string, bool> GetProviderAvailability();
}
