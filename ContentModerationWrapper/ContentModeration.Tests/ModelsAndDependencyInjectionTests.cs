using ContentModeration.Abstractions;
using ContentModeration.DependencyInjection;
using ContentModeration.Models;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace ContentModeration.Tests;

public sealed class ModelsAndDependencyInjectionTests
{
    [Fact]
    public void ImageDetection_NormalizesAzureSeverityAndValidatesArguments()
    {
        var azureDet = ImageDetection.FromAzure(ModerationCategory.Violence, "VIOLENCE", severity: 4);
        Assert.Equal(4, azureDet.Severity);
        Assert.Equal(0.6667, azureDet.Confidence);
        Assert.Equal(66.7, azureDet.ConfidencePercent);

        var osDet = ImageDetection.FromOpenSource(
            ModerationCategory.Nudity,
            "FEMALE_BREAST_EXPOSED",
            0.87654,
            new ImageBoundingBox(10, 20, 30, 40));
        Assert.Null(osDet.Severity);
        Assert.Equal(0.8765, osDet.Confidence);
        Assert.Equal(87.6, osDet.ConfidencePercent);
        Assert.Equal(new ImageBoundingBox(10, 20, 30, 40), osDet.BoundingBox);

        Assert.Throws<ArgumentException>(() => new ImageDetection(ModerationCategory.Nudity, "", "Provider"));
        Assert.Throws<ArgumentException>(() => new ImageDetection(ModerationCategory.Nudity, "RAW", ""));
    }

    [Fact]
    public void TextDetection_NormalizesAzureSeverityAndValidatesArguments()
    {
        var azureDet = TextDetection.FromAzure(TextModerationCategory.SelfHarm, "SELF_HARM", severity: 6, "desc");
        Assert.Equal(6, azureDet.Severity);
        Assert.Equal(1.0, azureDet.Confidence);
        Assert.Equal(100.0, azureDet.ConfidencePercent);
        Assert.Equal("desc", azureDet.Description);

        Assert.Throws<ArgumentException>(() => new TextDetection(TextModerationCategory.SelfHarm, "", "Provider"));
        Assert.Throws<ArgumentException>(() => new TextDetection(TextModerationCategory.SelfHarm, "RAW", ""));
    }

    [Fact]
    public void AddImageModeration_And_AddTextModeration_RegisterServicesAndBindConfiguration()
    {
        var configValues = new Dictionary<string, string?>
        {
            ["ImageModeration:Provider"] = "Azure",
            ["ImageModeration:MaxFileSizeBytes"] = "5242880",
            ["ContentModeration:Text:Provider"] = "OpenSource",
            ["ContentModeration:Text:MaxTextLength"] = "4096",
            ["AZURE_CONTENT_SAFETY_ENDPOINT"] = "https://shared-endpoint.cognitiveservices.azure.com/",
            ["AZURE_CONTENT_SAFETY_API_KEY"] = "shared-key"
        };

        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(configValues)
            .Build();

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddImageModeration(configuration);
        services.AddTextModeration(configuration);

        using var provider = services.BuildServiceProvider();

        var imageService = provider.GetRequiredService<IImageModerationService>();
        var textService = provider.GetRequiredService<ITextModerationService>();
        var imageProviders = provider.GetServices<IImageModerationProvider>().ToList();
        var textProviders = provider.GetServices<ITextModerationProvider>().ToList();

        Assert.NotNull(imageService);
        Assert.NotNull(textService);
        Assert.Equal(2, imageProviders.Count);
        Assert.Equal(2, textProviders.Count);

        var imgOpts = provider.GetRequiredService<IOptions<ImageModerationOptions>>().Value;
        Assert.Equal(ModerationProvider.Azure, imgOpts.Provider);
        Assert.Equal(5242880, imgOpts.MaxFileSizeBytes);
        Assert.Equal("https://shared-endpoint.cognitiveservices.azure.com/", imgOpts.Azure.Endpoint);
        Assert.Equal("shared-key", imgOpts.Azure.ApiKey);

        var txtOpts = provider.GetRequiredService<IOptions<TextModerationOptions>>().Value;
        Assert.Equal(ModerationProvider.OpenSource, txtOpts.Provider);
        Assert.Equal(4096, txtOpts.MaxTextLength);
        Assert.Equal("https://shared-endpoint.cognitiveservices.azure.com/", txtOpts.Azure.Endpoint);
        Assert.Equal("shared-key", txtOpts.Azure.ApiKey);
    }
}
