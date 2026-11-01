using System.Collections.Concurrent;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using Azure;
using Azure.AI.ContentSafety;
using Azure.Core;
using ContentModeration.Models;
using Microsoft.Extensions.Logging;

namespace ContentModeration.Providers.Azure;

/// <summary>
/// Resolves custom text blocklist definitions (built-in Tagalog/Cebuano lists plus configured lists) and
/// provisions them into an Azure AI Content Safety resource using the official <see cref="BlocklistClient"/>.
/// Provisioning runs once in the background per configuration fingerprint; analysis requests never block on it.
/// </summary>
public sealed class AzureTextBlocklistManager
{
    internal const int MaxItemsPerRequest = 100;
    internal const int MaxItemLength = 128;
    private static readonly TimeSpan RetryCooldown = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan ProvisioningTimeout = TimeSpan.FromSeconds(90);

    /// <summary>Built-in list definitions: (name, category, severity, description).</summary>
    internal static readonly (string Name, TextModerationCategory Category, int Severity, string Description)[] BuiltInLists =
    [
        ("fil-ceb-profanity", TextModerationCategory.ProfanityAndAbuse, 4, "Tagalog/Cebuano/Taglish profanity and abusive expletives."),
        ("fil-ceb-insults", TextModerationCategory.ProfanityAndAbuse, 2, "Tagalog/Cebuano mild insults."),
        ("fil-ceb-sexual", TextModerationCategory.SexualAndExplicit, 6, "Tagalog/Cebuano explicit sexual vulgarities."),
        ("fil-ceb-hate", TextModerationCategory.HateAndHarassment, 4, "Tagalog/Cebuano hate speech and slurs."),
        ("fil-ceb-threats", TextModerationCategory.ThreatsAndViolence, 6, "Tagalog/Cebuano threats of violence."),
        ("fil-ceb-selfharm", TextModerationCategory.SelfHarm, 6, "Tagalog/Cebuano self-harm encouragement and ideation.")
    ];

    private readonly ILogger _logger;
    private readonly object _stateLock = new();
    private readonly ConcurrentDictionary<string, ResolvedTextBlocklist> _definitionsByName = new(StringComparer.OrdinalIgnoreCase);

    private string _fingerprint = string.Empty;
    private IReadOnlyList<string> _readyBlocklists = Array.Empty<string>();
    private Task? _provisioningTask;
    private DateTimeOffset _nextAttemptUtc = DateTimeOffset.MinValue;

    public AzureTextBlocklistManager(ILogger logger)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>Blocklists that have been successfully provisioned and can be referenced in analysis requests.</summary>
    public IReadOnlyList<string> ReadyBlocklists
    {
        get { lock (_stateLock) { return _readyBlocklists; } }
    }

    /// <summary>Looks up a resolved blocklist definition (category/severity) by name.</summary>
    public bool TryGetDefinition(string blocklistName, out ResolvedTextBlocklist definition) =>
        _definitionsByName.TryGetValue(blocklistName, out definition!);

    /// <summary>
    /// Returns the currently provisioned blocklists and, if needed, starts background provisioning.
    /// Never blocks the caller on network I/O.
    /// </summary>
    public IReadOnlyList<string> GetReadyBlocklistsAndEnsureProvisioning(
        Func<BlocklistClient> clientFactory,
        string credentialFingerprint,
        AzureTextModerationOptions options)
    {
        if (!options.AutoProvisionBlocklists)
        {
            return Array.Empty<string>();
        }

        var definitions = ResolveDefinitions(options, _logger);
        foreach (var definition in definitions)
        {
            _definitionsByName[definition.Name] = definition;
        }

        var fingerprint = ComputeFingerprint(credentialFingerprint, definitions);

        lock (_stateLock)
        {
            var configChanged = !string.Equals(fingerprint, _fingerprint, StringComparison.Ordinal);
            if (configChanged)
            {
                _fingerprint = fingerprint;
                _readyBlocklists = Array.Empty<string>();
                _nextAttemptUtc = DateTimeOffset.MinValue;
            }

            var running = _provisioningTask is { IsCompleted: false };
            var needsWork = _readyBlocklists.Count < definitions.Count;

            if (!running && needsWork && DateTimeOffset.UtcNow >= _nextAttemptUtc)
            {
                _nextAttemptUtc = DateTimeOffset.UtcNow + RetryCooldown;
                _provisioningTask = Task.Run(() => ProvisionAllAsync(clientFactory(), definitions, fingerprint));
            }

            return _readyBlocklists;
        }
    }

    /// <summary>Waits for any in-flight provisioning (used at startup warm-up and in tests).</summary>
    public Task WaitForProvisioningAsync()
    {
        lock (_stateLock)
        {
            return _provisioningTask ?? Task.CompletedTask;
        }
    }

    /// <summary>Marks blocklists as not ready (e.g., Azure returned 404 for a referenced list) so they are re-provisioned.</summary>
    public void Invalidate()
    {
        lock (_stateLock)
        {
            _readyBlocklists = Array.Empty<string>();
            _nextAttemptUtc = DateTimeOffset.MinValue;
        }
    }

    private async Task ProvisionAllAsync(
        BlocklistClient client,
        IReadOnlyList<ResolvedTextBlocklist> definitions,
        string fingerprint)
    {
        using var cts = new CancellationTokenSource(ProvisioningTimeout);
        var ready = new List<string>();

        foreach (var definition in definitions)
        {
            try
            {
                await ProvisionBlocklistAsync(client, definition, cts.Token).ConfigureAwait(false);
                ready.Add(definition.Name);
            }
            catch (RequestFailedException ex)
            {
                _logger.LogWarning(
                    "Failed to provision Azure text blocklist '{Blocklist}' (HTTP {Status}, ErrorCode: {ErrorCode}).",
                    definition.Name, ex.Status, ex.ErrorCode);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to provision Azure text blocklist '{Blocklist}'.", definition.Name);
            }
        }

        lock (_stateLock)
        {
            if (string.Equals(fingerprint, _fingerprint, StringComparison.Ordinal))
            {
                _readyBlocklists = ready;
            }
        }

        _logger.LogInformation(
            "Azure text blocklists ready: {Ready}/{Total} ({Names}). Note: newly created items can take up to ~5 minutes to take effect in Azure.",
            ready.Count, definitions.Count, string.Join(", ", ready));
    }

    private static async Task ProvisionBlocklistAsync(
        BlocklistClient client,
        ResolvedTextBlocklist definition,
        CancellationToken cancellationToken)
    {
        await client.CreateOrUpdateTextBlocklistAsync(
                definition.Name,
                RequestContent.Create(new { description = definition.Description }),
                new RequestContext { CancellationToken = cancellationToken })
            .ConfigureAwait(false);

        var existing = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        await foreach (var item in client
                           .GetTextBlocklistItemsAsync(definition.Name, cancellationToken: cancellationToken)
                           .ConfigureAwait(false))
        {
            existing.Add(item.Text);
        }

        var missing = definition.Terms.Where(t => !existing.Contains(t)).ToList();
        for (var i = 0; i < missing.Count; i += MaxItemsPerRequest)
        {
            var batch = missing
                .Skip(i)
                .Take(MaxItemsPerRequest)
                .Select(t => new TextBlocklistItem(t))
                .ToList();

            await client.AddOrUpdateBlocklistItemsAsync(
                    definition.Name,
                    new AddOrUpdateTextBlocklistItemsOptions(batch),
                    cancellationToken)
                .ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Resolves built-in and configured blocklists into term lists (trimmed, de-duplicated, max 128 chars per item).
    /// </summary>
    public static IReadOnlyList<ResolvedTextBlocklist> ResolveDefinitions(AzureTextModerationOptions options, ILogger? logger = null)
    {
        var results = new Dictionary<string, ResolvedTextBlocklist>(StringComparer.OrdinalIgnoreCase);

        if (options.UseBuiltInFilipinoBlocklists)
        {
            foreach (var (name, category, severity, description) in BuiltInLists)
            {
                var terms = NormalizeTerms(ReadEmbeddedTerms(name));
                if (terms.Count > 0)
                {
                    results[name] = new ResolvedTextBlocklist(name, description, category, severity, terms);
                }
            }
        }

        foreach (var custom in options.Blocklists ?? [])
        {
            if (string.IsNullOrWhiteSpace(custom.Name))
            {
                continue;
            }

            var rawTerms = new List<string>(custom.Terms ?? []);
            if (!string.IsNullOrWhiteSpace(custom.TermsFile))
            {
                var path = Path.IsPathRooted(custom.TermsFile)
                    ? custom.TermsFile
                    : Path.Combine(AppContext.BaseDirectory, custom.TermsFile);
                if (File.Exists(path))
                {
                    rawTerms.AddRange(ParseTermLines(File.ReadAllLines(path, Encoding.UTF8)));
                }
                else
                {
                    logger?.LogWarning("Blocklist terms file '{Path}' for '{Blocklist}' was not found.", path, custom.Name);
                }
            }

            // Merge with a built-in list of the same name (allows extending built-in lists via configuration)
            if (results.TryGetValue(custom.Name, out var builtIn))
            {
                rawTerms.AddRange(builtIn.Terms);
            }

            var terms = NormalizeTerms(rawTerms);
            if (terms.Count == 0)
            {
                continue;
            }

            results[custom.Name.Trim()] = new ResolvedTextBlocklist(
                custom.Name.Trim(),
                custom.Description ?? builtIn?.Description ?? $"Custom blocklist '{custom.Name}'.",
                custom.Category,
                Math.Clamp(custom.Severity, 0, 7),
                terms);
        }

        return results.Values.ToList();
    }

    internal static IEnumerable<string> ReadEmbeddedTerms(string listName)
    {
        var assembly = typeof(AzureTextBlocklistManager).Assembly;
        using var stream = assembly.GetManifestResourceStream($"ContentModeration.Blocklists.{listName}.txt");
        if (stream is null)
        {
            return [];
        }

        using var reader = new StreamReader(stream, Encoding.UTF8);
        var lines = new List<string>();
        while (reader.ReadLine() is { } line)
        {
            lines.Add(line);
        }

        return ParseTermLines(lines);
    }

    private static IEnumerable<string> ParseTermLines(IEnumerable<string> lines) =>
        lines
            .Select(l => l.Trim())
            .Where(l => l.Length > 0 && !l.StartsWith('#'));

    private static IReadOnlyList<string> NormalizeTerms(IEnumerable<string> terms) =>
        terms
            .Select(t => t.Trim().ToLowerInvariant())
            .Where(t => t.Length > 0 && t.Length <= MaxItemLength)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

    private static string ComputeFingerprint(string credentialFingerprint, IReadOnlyList<ResolvedTextBlocklist> definitions)
    {
        var sb = new StringBuilder(credentialFingerprint);
        foreach (var d in definitions.OrderBy(d => d.Name, StringComparer.OrdinalIgnoreCase))
        {
            sb.Append('|').Append(d.Name).Append(':').AppendJoin(',', d.Terms);
        }

        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString())));
    }
}
