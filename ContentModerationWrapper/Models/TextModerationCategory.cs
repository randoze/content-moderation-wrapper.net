using System.Text.Json.Serialization;

namespace ContentModeration.Models;

/// <summary>
/// Normalized harmful text detection categories across Azure AI Content Safety and open-source text models.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum TextModerationCategory
{
    /// <summary>
    /// Sexual and explicit text content (e.g., Azure <c>TextCategory.Sexual</c>).
    /// </summary>
    SexualAndExplicit,

    /// <summary>
    /// Hate speech, identity attacks, insults, and harassment (e.g., Azure <c>TextCategory.Hate</c>).
    /// </summary>
    HateAndHarassment,

    /// <summary>
    /// Threats, physical violence, and violent acts (e.g., Azure <c>TextCategory.Violence</c>).
    /// </summary>
    ThreatsAndViolence,

    /// <summary>
    /// Self-harm encouragement or suicide instruction (e.g., Azure <c>TextCategory.SelfHarm</c>).
    /// </summary>
    SelfHarm,

    /// <summary>
    /// Profanity, obscenity, and abusive language.
    /// </summary>
    ProfanityAndAbuse,

    /// <summary>
    /// Other harmful text categories or custom blocklist matches reported by a provider.
    /// </summary>
    OtherHarmful
}

/// <summary>
/// Overall policy classification assigned to analyzed text by <see cref="Abstractions.ITextModerationService"/>.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum TextModerationClassification
{
    Safe,
    PotentiallyHarmful,
    Violation,
    ReviewRequired
}
