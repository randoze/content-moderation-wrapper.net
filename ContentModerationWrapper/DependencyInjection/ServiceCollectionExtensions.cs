using ContentModeration.Abstractions;
using ContentModeration.Models;
using ContentModeration.Providers.Azure;
using ContentModeration.Providers.OpenSource;
using ContentModeration.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace ContentModeration.DependencyInjection;

/// <summary>
/// Dependency injection registration extensions for the reusable <c>ImageModeration</c> library.
/// </summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers <see cref="IImageModerationService"/> and both <see cref="AzureContentSafetyProvider"/> and
    /// <see cref="OpenSourceModerationProvider"/> in the <see cref="IServiceCollection"/> using programmatic options.
    /// </summary>
    /// <example>
    /// <code>
    /// builder.Services.AddImageModeration(options =>
    /// {
    ///     options.Provider = ModerationProvider.Azure;
    /// });
    /// </code>
    /// </example>
    public static IServiceCollection AddImageModeration(
        this IServiceCollection services,
        Action<ImageModerationOptions>? configureOptions = null)
    {
        ArgumentNullException.ThrowIfNull(services);

        var optionsBuilder = services.AddOptions<ImageModerationOptions>();
        if (configureOptions is not null)
        {
            optionsBuilder.Configure(configureOptions);
        }

        RegisterCoreServices(services);
        return services;
    }

    /// <summary>
    /// Registers <see cref="IImageModerationService"/> and binds <see cref="ImageModerationOptions"/> from
    /// <paramref name="configuration"/> (default section <c>"ImageModeration"</c>) and environment variables,
    /// with optional programmatic overrides.
    /// </summary>
    public static IServiceCollection AddImageModeration(
        this IServiceCollection services,
        IConfiguration configuration,
        Action<ImageModerationOptions>? configureOptions = null,
        string sectionName = ImageModerationOptions.SectionName)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services.TryAddSingleton(configuration);

        var section = configuration.GetSection(sectionName);
        var optionsBuilder = services
            .AddOptions<ImageModerationOptions>()
            .Bind(section)
            .Configure(opts =>
            {
                // Support environment variable overrides (e.g., IMAGE_MODERATION_PROVIDER, AZURE_CONTENT_SAFETY_ENDPOINT)
                var envProvider = configuration["IMAGE_MODERATION_PROVIDER"]
                    ?? Environment.GetEnvironmentVariable("IMAGE_MODERATION_PROVIDER");
                if (!string.IsNullOrWhiteSpace(envProvider) &&
                    Enum.TryParse<ModerationProvider>(envProvider, ignoreCase: true, out var parsedProvider))
                {
                    opts.Provider = parsedProvider;
                }

                var envFallback = configuration["IMAGE_MODERATION_FALLBACK_PROVIDER"]
                    ?? Environment.GetEnvironmentVariable("IMAGE_MODERATION_FALLBACK_PROVIDER");
                if (!string.IsNullOrWhiteSpace(envFallback) &&
                    Enum.TryParse<ModerationProvider>(envFallback, ignoreCase: true, out var parsedFallback))
                {
                    opts.FallbackProvider = parsedFallback;
                }

                if (string.IsNullOrWhiteSpace(opts.Azure.Endpoint))
                {
                    opts.Azure.Endpoint = configuration["Moderation:AzureContentSafety:Endpoint"]
                        ?? configuration["ContentModeration:Text:Azure:Endpoint"]
                        ?? configuration["AZURE_CONTENT_SAFETY_ENDPOINT"]
                        ?? Environment.GetEnvironmentVariable("AZURE_CONTENT_SAFETY_ENDPOINT")
                        ?? string.Empty;
                }

                if (string.IsNullOrWhiteSpace(opts.Azure.ApiKey))
                {
                    opts.Azure.ApiKey = configuration["Moderation:AzureContentSafety:ApiKey"]
                        ?? configuration["ContentModeration:Text:Azure:ApiKey"]
                        ?? configuration["AZURE_CONTENT_SAFETY_API_KEY"]
                        ?? Environment.GetEnvironmentVariable("AZURE_CONTENT_SAFETY_API_KEY")
                        ?? string.Empty;
                }
            });

        if (configureOptions is not null)
        {
            optionsBuilder.Configure(configureOptions);
        }

        RegisterCoreServices(services);
        return services;
    }

    private static void RegisterCoreServices(IServiceCollection services)
    {
        services.TryAddSingleton<AzureContentSafetyProvider>();
        services.TryAddSingleton<OpenSourceModerationProvider>();

        // Register both providers under IImageModerationProvider so DefaultImageModerationService can switch dynamically
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IImageModerationProvider, AzureContentSafetyProvider>(
            sp => sp.GetRequiredService<AzureContentSafetyProvider>()));
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IImageModerationProvider, OpenSourceModerationProvider>(
            sp => sp.GetRequiredService<OpenSourceModerationProvider>()));

        services.TryAddSingleton<IImageModerationService, DefaultImageModerationService>();
    }

    /// <summary>
    /// Registers <see cref="ITextModerationService"/>, <see cref="AzureTextModerationProvider"/>, and
    /// <see cref="OpenSourceTextModerationProvider"/> using programmatic options.
    /// </summary>
    public static IServiceCollection AddTextModeration(
        this IServiceCollection services,
        Action<TextModerationOptions>? configureOptions = null)
    {
        ArgumentNullException.ThrowIfNull(services);

        var optionsBuilder = services.AddOptions<TextModerationOptions>();
        if (configureOptions is not null)
        {
            optionsBuilder.Configure(configureOptions);
        }

        RegisterTextModerationServices(services);
        return services;
    }

    /// <summary>
    /// Registers <see cref="ITextModerationService"/> and binds <see cref="TextModerationOptions"/> from
    /// the specified <see cref="IConfigurationSection"/> (e.g., <c>builder.Configuration.GetSection("ContentModeration:Text")</c>).
    /// </summary>
    public static IServiceCollection AddTextModeration(
        this IServiceCollection services,
        IConfigurationSection configurationSection,
        Action<TextModerationOptions>? configureOptions = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configurationSection);

        var optionsBuilder = services
            .AddOptions<TextModerationOptions>()
            .Bind(configurationSection)
            .Configure<IServiceProvider>((opts, sp) =>
            {
                ApplyTextEnvironmentFallbacks(opts, configurationSection);
                var rootConfig = sp.GetService<IConfiguration>();
                if (rootConfig is not null)
                {
                    ApplyTextEnvironmentFallbacks(opts, rootConfig);
                }
            });

        if (configureOptions is not null)
        {
            optionsBuilder.Configure(configureOptions);
        }

        RegisterTextModerationServices(services);
        return services;
    }

    /// <summary>
    /// Registers <see cref="ITextModerationService"/> and binds <see cref="TextModerationOptions"/> from
    /// <paramref name="configuration"/> (default section <c>"ContentModeration:Text"</c>).
    /// </summary>
    public static IServiceCollection AddTextModeration(
        this IServiceCollection services,
        IConfiguration configuration,
        Action<TextModerationOptions>? configureOptions = null,
        string sectionName = TextModerationOptions.SectionName)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services.TryAddSingleton(configuration);
        var section = configuration.GetSection(sectionName);

        var optionsBuilder = services
            .AddOptions<TextModerationOptions>()
            .Bind(section)
            .Configure(opts =>
            {
                ApplyTextEnvironmentFallbacks(opts, configuration);
            });

        if (configureOptions is not null)
        {
            optionsBuilder.Configure(configureOptions);
        }

        RegisterTextModerationServices(services);
        return services;
    }

    private static void ApplyTextEnvironmentFallbacks(TextModerationOptions opts, IConfiguration config)
    {
        var envProvider = config["TEXT_MODERATION_PROVIDER"]
            ?? Environment.GetEnvironmentVariable("TEXT_MODERATION_PROVIDER");
        if (!string.IsNullOrWhiteSpace(envProvider) &&
            Enum.TryParse<ModerationProvider>(envProvider, ignoreCase: true, out var parsedProvider))
        {
            opts.Provider = parsedProvider;
        }

        if (string.IsNullOrWhiteSpace(opts.Azure.Endpoint))
        {
            opts.Azure.Endpoint = config["Azure:Endpoint"]
                ?? config["Moderation:AzureContentSafety:Endpoint"]
                ?? config["ImageModeration:Azure:Endpoint"]
                ?? config["AZURE_CONTENT_SAFETY_ENDPOINT"]
                ?? Environment.GetEnvironmentVariable("AZURE_CONTENT_SAFETY_ENDPOINT")
                ?? string.Empty;
        }

        if (string.IsNullOrWhiteSpace(opts.Azure.ApiKey))
        {
            opts.Azure.ApiKey = config["Azure:ApiKey"]
                ?? config["Moderation:AzureContentSafety:ApiKey"]
                ?? config["ImageModeration:Azure:ApiKey"]
                ?? config["AZURE_CONTENT_SAFETY_API_KEY"]
                ?? Environment.GetEnvironmentVariable("AZURE_CONTENT_SAFETY_API_KEY")
                ?? string.Empty;
        }
    }

    private static void RegisterTextModerationServices(IServiceCollection services)
    {
        services.TryAddSingleton<AzureTextModerationProvider>();
        services.TryAddSingleton<OpenSourceTextModerationProvider>();

        services.TryAddEnumerable(ServiceDescriptor.Singleton<ITextModerationProvider, AzureTextModerationProvider>(
            sp => sp.GetRequiredService<AzureTextModerationProvider>()));
        services.TryAddEnumerable(ServiceDescriptor.Singleton<ITextModerationProvider, OpenSourceTextModerationProvider>(
            sp => sp.GetRequiredService<OpenSourceTextModerationProvider>()));

        services.TryAddSingleton<ITextModerationService, TextModerationService>();
    }
}
