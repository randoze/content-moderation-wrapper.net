using ContentModeration.Abstractions;
using ContentModeration.Models;
using Microsoft.Extensions.Options;
using SkiaSharp;

namespace ContentModeration.Tests;

internal sealed class StaticOptionsMonitor<T>(T currentValue) : IOptionsMonitor<T>
{
    public T CurrentValue => currentValue;

    public T Get(string? name) => currentValue;

    public IDisposable? OnChange(Action<T, string?> listener) => null;
}

internal sealed class FakeImageModerationProvider(
    ModerationProvider providerType,
    string providerName,
    bool isAvailable,
    Func<byte[], CancellationToken, Task<ProviderAnalysisResult>> handler) : IImageModerationProvider
{
    public int CallCount { get; private set; }

    public ModerationProvider ProviderType => providerType;

    public string ProviderName => providerName;

    public bool IsAvailable => isAvailable;

    public string? AvailabilityError => isAvailable ? null : $"{providerName} is unavailable.";

    public Task<ProviderAnalysisResult> AnalyzeAsync(byte[] imageBytes, CancellationToken cancellationToken = default)
    {
        CallCount++;
        return handler(imageBytes, cancellationToken);
    }
}

internal sealed class FakeTextModerationProvider(
    ModerationProvider providerType,
    string providerName,
    bool isAvailable,
    Func<string, CancellationToken, Task<TextProviderAnalysisResult>> handler) : ITextModerationProvider
{
    public int CallCount { get; private set; }

    public ModerationProvider ProviderType => providerType;

    public string ProviderName => providerName;

    public bool IsAvailable => isAvailable;

    public string? AvailabilityError => isAvailable ? null : $"{providerName} is unavailable.";

    public Task<TextProviderAnalysisResult> AnalyzeTextAsync(string text, CancellationToken cancellationToken = default)
    {
        CallCount++;
        return handler(text, cancellationToken);
    }
}

internal static class TestImageFactory
{
    public static byte[] CreateValidPngBytes(int width = 32, int height = 32, SKColor? color = null)
    {
        using var bitmap = new SKBitmap(width, height, SKColorType.Rgba8888, SKAlphaType.Premul);
        using (var canvas = new SKCanvas(bitmap))
        {
            canvas.Clear(color ?? SKColors.CornflowerBlue);
        }

        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }

    public static byte[] CreateValidJpegBytes(int width = 32, int height = 32)
    {
        using var bitmap = new SKBitmap(width, height, SKColorType.Rgba8888, SKAlphaType.Premul);
        using (var canvas = new SKCanvas(bitmap))
        {
            canvas.Clear(SKColors.Coral);
        }

        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SKEncodedImageFormat.Jpeg, 90);
        return data.ToArray();
    }
}
