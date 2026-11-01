namespace ContentModeration.Models;

/// <summary>
/// Defines an Azure AI Content Safety custom text blocklist, the normalized category its matches map to,
/// and the Azure-style severity (0, 2, 4, 6) assigned to a match.
/// </summary>
public sealed class TextBlocklistDefinition
{
    /// <summary>
    /// Azure blocklist name (letters, digits, '-', '_', '.', '~'; max 64 characters).
    /// </summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// Optional human-readable description stored on the Azure blocklist.
    /// </summary>
    public string? Description { get; set; }

    /// <summary>
    /// Normalized category reported when an item of this blocklist matches.
    /// </summary>
    public TextModerationCategory Category { get; set; } = TextModerationCategory.OtherHarmful;

    /// <summary>
    /// Severity (0, 2, 4, 6) assigned to a match. Compared against the Azure severity thresholds,
    /// so with defaults 2 = PotentiallyHarmful and 4+ = Violation.
    /// </summary>
    public int Severity { get; set; } = 6;

    /// <summary>
    /// Optional path to a UTF-8 text file with one term or phrase per line (lines starting with '#' are comments).
    /// Relative paths are resolved against the application base directory.
    /// </summary>
    public string? TermsFile { get; set; }

    /// <summary>
    /// Optional inline terms or phrases.
    /// </summary>
    public List<string> Terms { get; set; } = [];
}

/// <summary>
/// A fully resolved blocklist (terms loaded and de-duplicated) ready for provisioning.
/// </summary>
public sealed record ResolvedTextBlocklist(
    string Name,
    string Description,
    TextModerationCategory Category,
    int Severity,
    IReadOnlyList<string> Terms);
