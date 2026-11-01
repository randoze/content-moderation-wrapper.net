using System.Text.Json.Serialization;

namespace ContentModeration.Models;

/// <summary>
/// Normalized content moderation categories across open-source ONNX models and Azure AI Content Safety.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ModerationCategory
{
    /// <summary>
    /// Exposed or covered intimate anatomical regions (e.g., NudeNet detections).
    /// </summary>
    Nudity,

    /// <summary>
    /// Explicit sexual or pornographic imagery (e.g., Azure Sexual category).
    /// </summary>
    SexualContent,

    /// <summary>
    /// Sexualized poses or suggestive imagery.
    /// </summary>
    SuggestiveContent,

    /// <summary>
    /// Blood, graphic injuries, dismemberment, and severe bodily harm (NSFL).
    /// </summary>
    Gore,

    /// <summary>
    /// Physical attacks, weapons in violent action, and graphic violence.
    /// </summary>
    Violence,

    /// <summary>
    /// Adult-oriented or disturbing mature imagery.
    /// </summary>
    MatureContent,

    /// <summary>
    /// Self-harm or intentional bodily injury (e.g., Azure SelfHarm category).
    /// </summary>
    SelfHarm,

    /// <summary>
    /// Hate symbols or hateful disturbing imagery (e.g., Azure Hate category).
    /// </summary>
    Hate,

    /// <summary>
    /// Benign anatomical features (e.g., faces, feet, covered belly).
    /// </summary>
    Anatomical
}

/// <summary>
/// Overall moderation classification returned by <see cref="Abstractions.IImageModerationService"/>.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ImageModerationClassification
{
    Safe,
    PotentiallyMature,
    Explicit,
    ReviewRequired
}

/// <summary>
/// Configurable image moderation provider selection.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ModerationProvider
{
    /// <summary>
    /// Local open-source ONNX models only (NudeNet, SwiftFormer Gore/Mature, ViT Violence).
    /// </summary>
    OpenSource,

    /// <summary>
    /// Azure AI Content Safety cloud API only.
    /// </summary>
    Azure,

    /// <summary>
    /// Runs both OpenSource and Azure AI Content Safety providers and combines their results.
    /// </summary>
    Hybrid
}
