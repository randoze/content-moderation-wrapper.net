namespace ContentModeration.Models;

/// <summary>
/// Configuration options for text moderation (<c>"ContentModeration:Text"</c>).
/// Independent of image moderation settings.
/// </summary>
public sealed class TextModerationOptions
{
    public const string SectionName = "ContentModeration:Text";

    /// <summary>
    /// Primary text moderation provider (<see cref="ModerationProvider.Azure"/> by default).
    /// </summary>
    public ModerationProvider Provider { get; set; } = ModerationProvider.Azure;

    /// <summary>
    /// Whether to fall back to <see cref="FallbackProvider"/> if the primary provider fails or is unreachable.
    /// </summary>
    public bool EnableFallback { get; set; } = true;

    /// <summary>
    /// Fallback provider to use when <see cref="EnableFallback"/> is true.
    /// </summary>
    public ModerationProvider? FallbackProvider { get; set; } = ModerationProvider.OpenSource;

    /// <summary>
    /// When true and all configured providers fail, returns <see cref="TextModerationClassification.ReviewRequired"/>
    /// with <c>IsFlagged = true</c> rather than throwing, ensuring text is never silently classified as Safe on error.
    /// </summary>
    public bool ReturnReviewRequiredOnFailure { get; set; } = true;

    /// <summary>
    /// Whether <see cref="TextModerationClassification.PotentiallyHarmful"/> results should set <c>IsFlagged = true</c>.
    /// </summary>
    public bool FlagPotentiallyHarmful { get; set; } = true;

    /// <summary>
    /// Maximum allowed text length in characters (default 10,000 characters, matching Azure AI Content Safety synchronous limit).
    /// </summary>
    public int MaxTextLength { get; set; } = 10_000;

    /// <summary>
    /// Overall timeout in seconds for a text moderation request.
    /// </summary>
    public int TimeoutSeconds { get; set; } = 10;

    /// <summary>
    /// Azure AI Content Safety settings for text analysis.
    /// </summary>
    public AzureTextModerationOptions Azure { get; set; } = new();

    /// <summary>
    /// Open-source text moderation settings.
    /// </summary>
    public OpenSourceTextModerationOptions OpenSource { get; set; } = new();
}

/// <summary>
/// Azure AI Content Safety configuration for <see cref="Providers.Azure.AzureTextModerationProvider"/>.
/// </summary>
public sealed class AzureTextModerationOptions
{
    /// <summary>
    /// Azure AI Content Safety endpoint URL (e.g., https://&lt;resource&gt;.cognitiveservices.azure.com/).
    /// Automatically falls back to User Secrets or <c>AZURE_CONTENT_SAFETY_ENDPOINT</c>.
    /// </summary>
    public string Endpoint { get; set; } = string.Empty;

    /// <summary>
    /// Azure AI Content Safety API key. Store in User Secrets or <c>AZURE_CONTENT_SAFETY_API_KEY</c>.
    /// </summary>
    public string ApiKey { get; set; } = string.Empty;

    /// <summary>
    /// Request timeout in seconds for Azure text analysis.
    /// </summary>
    public int TimeoutSeconds { get; set; } = 8;

    /// <summary>
    /// Minimum Azure severity score (0, 2, 4, 6) to include in <see cref="TextModerationResult.Detections"/>.
    /// </summary>
    public int MinimumReportedSeverity { get; set; } = 2;

    /// <summary>
    /// Azure severity threshold (0–6) that triggers <see cref="TextModerationClassification.PotentiallyHarmful"/>.
    /// </summary>
    public int PotentiallyHarmfulSeverityThreshold { get; set; } = 2;

    /// <summary>
    /// Azure severity threshold (0–6) that triggers <see cref="TextModerationClassification.Violation"/>.
    /// </summary>
    public int ViolationSeverityThreshold { get; set; } = 4;

    /// <summary>
    /// Optional Azure custom blocklist names to evaluate during text analysis.
    /// </summary>
    public List<string> BlocklistNames { get; set; } = [];

    /// <summary>
    /// Whether to halt analysis immediately upon hitting a blocklist item.
    /// </summary>
    public bool HaltOnBlocklistHit { get; set; } = false;

    /// <summary>
    /// When true, the library automatically creates/updates the Azure custom text blocklists defined in
    /// <see cref="Blocklists"/> (and the built-in Filipino/Cebuano lists when <see cref="UseBuiltInFilipinoBlocklists"/>
    /// is enabled) in your Azure AI Content Safety resource using the official <c>BlocklistClient</c>.
    /// Azure's built-in classifiers are only trained on a limited set of languages, so blocklists are the
    /// supported way to catch harmful terms in languages such as Tagalog, Cebuano, and Taglish.
    /// </summary>
    public bool AutoProvisionBlocklists { get; set; } = true;

    /// <summary>
    /// Whether to include the built-in Tagalog/Cebuano/Taglish blocklists (profanity, insults, sexual, hate, threats, self-harm).
    /// </summary>
    public bool UseBuiltInFilipinoBlocklists { get; set; } = true;

    /// <summary>
    /// When true, a normalized copy of the input (lower-cased, common leetspeak such as <c>t4ngina</c> / <c>g@go</c>
    /// decoded, in-word separators removed, and elongated letters collapsed) is appended to the text sent to Azure
    /// so obfuscated terms still match blocklist items. Only applied when the normalized form differs.
    /// </summary>
    public bool AppendNormalizedTextForBlocklistMatching { get; set; } = true;

    /// <summary>
    /// Additional custom blocklists to provision and evaluate (each mapped to a category and severity).
    /// </summary>
    public List<TextBlocklistDefinition> Blocklists { get; set; } = [];

    /// <summary>
    /// Optional per-category overrides for Azure <see cref="TextModerationClassification.Violation"/> severity thresholds
    /// (keyed by <see cref="TextModerationCategory"/> name or raw label such as "SEXUAL", "HATE", "VIOLENCE", "SELF_HARM").
    /// </summary>
    public Dictionary<string, int> CategoryViolationSeverityThresholds { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Optional per-category overrides for Azure <see cref="TextModerationClassification.PotentiallyHarmful"/> severity thresholds.
    /// </summary>
    public Dictionary<string, int> CategoryPotentiallyHarmfulSeverityThresholds { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

/// <summary>
/// Configuration options for <see cref="Providers.OpenSource.OpenSourceTextModerationProvider"/>.
/// </summary>
public sealed class OpenSourceTextModerationOptions
{
    /// <summary>
    /// Optional path to a local ONNX text classification model (e.g., <c>models/toxic-bert-quantized.onnx</c>).
    /// When not present on disk, uses the built-in contextual open-source heuristic/lexicon classifier.
    /// </summary>
    public string? ModelPath { get; set; }

    /// <summary>
    /// Optional path to a BERT WordPiece <c>vocab.txt</c> file corresponding to <see cref="ModelPath"/>.
    /// </summary>
    public string? VocabPath { get; set; }

    /// <summary>
    /// Minimum confidence score (0.0–1.0) to report a detection.
    /// </summary>
    public double MinimumConfidence { get; set; } = 0.40;

    /// <summary>
    /// Global confidence threshold (0.0–1.0) that triggers <see cref="TextModerationClassification.PotentiallyHarmful"/>.
    /// </summary>
    public double PotentiallyHarmfulThreshold { get; set; } = 0.45;

    /// <summary>
    /// Global confidence threshold (0.0–1.0) that triggers <see cref="TextModerationClassification.Violation"/>.
    /// </summary>
    public double ViolationThreshold { get; set; } = 0.75;

    /// <summary>
    /// Whether to dampen confidence scores when educational, clinical, or historical context is detected.
    /// </summary>
    public bool EnableContextualFalsePositiveReduction { get; set; } = true;

    /// <summary>
    /// Optional per-category overrides for open-source <see cref="TextModerationClassification.Violation"/> confidence thresholds.
    /// </summary>
    public Dictionary<string, double> CategoryViolationThresholds { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Optional per-category overrides for open-source <see cref="TextModerationClassification.PotentiallyHarmful"/> confidence thresholds.
    /// </summary>
    public Dictionary<string, double> CategoryPotentiallyHarmfulThresholds { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}
