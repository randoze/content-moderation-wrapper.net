using System.Diagnostics;
using System.Text.RegularExpressions;
using ContentModeration.Abstractions;
using ContentModeration.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace ContentModeration.Providers.OpenSource;

/// <summary>
/// <see cref="ITextModerationProvider"/> implementation for local open-source text moderation.
/// Supports running a local BERT/RoBERTa ONNX sequence classification model (such as <c>unitary/toxic-bert</c>)
/// when configured, and includes a built-in contextual multi-category analyzer for offline fallback across:
/// <list type="bullet">
/// <item><description><see cref="TextModerationCategory.SexualAndExplicit"/></description></item>
/// <item><description><see cref="TextModerationCategory.HateAndHarassment"/></description></item>
/// <item><description><see cref="TextModerationCategory.ThreatsAndViolence"/></description></item>
/// <item><description><see cref="TextModerationCategory.SelfHarm"/></description></item>
/// <item><description><see cref="TextModerationCategory.ProfanityAndAbuse"/></description></item>
/// </list>
/// </summary>
public sealed class OpenSourceTextModerationProvider : ITextModerationProvider, IDisposable
{
    private static readonly (TextModerationCategory Category, string RawLabel, string Description, Regex Pattern, double BaseConfidence)[] Rules =
    [
        (
            TextModerationCategory.SelfHarm,
            "SELF_HARM_ENCOURAGEMENT",
            "Encouragement or instruction of self-harm or suicide.",
            new Regex(@"\b(kill\s+yourself|commit\s+suicide|end\s+your\s+life|cut\s+your\s+wrists|how\s+to\s+overdose)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled),
            0.94
        ),
        (
            TextModerationCategory.ThreatsAndViolence,
            "THREAT_AND_VIOLENCE",
            "Direct threats of physical violence, murder, or severe harm.",
            new Regex(@"\b(i\s+will\s+kill\s+you|going\s+to\s+murder\s+you|shoot\s+up\s+the|bomb\s+the\s+building|stab\s+you\s+to\s+death|beat\s+you\s+to\s+death)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled),
            0.92
        ),
        (
            TextModerationCategory.SexualAndExplicit,
            "SEXUAL_EXPLICIT",
            "Explicit sexual solicitation or pornographic text description.",
            new Regex(@"\b(explicit\s+porn|hardcore\s+sex\s+video|send\s+nude\s+photos|sexual\s+intercourse\s+with\s+minors|nsfw\s+xxx\s+porn)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled),
            0.89
        ),
        (
            TextModerationCategory.HateAndHarassment,
            "HATE_AND_HARASSMENT",
            "Dehumanizing hate speech, slurs, or targeted harassment.",
            new Regex(@"\b(exterminate\s+all|subhuman\s+scum|ethnic\s+cleansing\s+now|you\s+are\s+worthless\s+trash\s+and\s+deserve\s+to\s+die|inferior\s+race)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled),
            0.88
        ),
        (
            TextModerationCategory.ProfanityAndAbuse,
            "PROFANITY_AND_ABUSE",
            "Profanity, obscenity, and abusive insults.",
            new Regex(@"\b(fuck\s+you|piece\s+of\s+shit|motherfucker|bitch\s+ass|dumb\s+asshole|go\s+to\s+hell\s+idiot)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled),
            0.68
        )
    ];

    private static readonly Regex EducationalOrContextualMarker = new(
        @"\b(history\s+of|educational|clinical\s+study|medical\s+research|suicide\s+prevention\s+hotline|documentary|policy\s+example|quoted\s+from|preventing\s+violence|academic\s+analysis)\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private readonly IOptionsMonitor<TextModerationOptions> _optionsMonitor;
    private readonly ILogger<OpenSourceTextModerationProvider> _logger;
    private readonly InferenceSession? _onnxSession;
    private readonly Dictionary<string, int>? _vocab;

    public OpenSourceTextModerationProvider(
        IOptionsMonitor<TextModerationOptions> optionsMonitor,
        ILogger<OpenSourceTextModerationProvider> logger)
    {
        _optionsMonitor = optionsMonitor ?? throw new ArgumentNullException(nameof(optionsMonitor));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

        var opts = _optionsMonitor.CurrentValue.OpenSource;
        if (!string.IsNullOrWhiteSpace(opts.ModelPath) && File.Exists(opts.ModelPath))
        {
            try
            {
                _onnxSession = new InferenceSession(opts.ModelPath);
                if (!string.IsNullOrWhiteSpace(opts.VocabPath) && File.Exists(opts.VocabPath))
                {
                    _vocab = LoadVocab(opts.VocabPath);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to load optional ONNX text model at {Path}; using built-in contextual open-source classifier.", opts.ModelPath);
            }
        }
    }

    public ModerationProvider ProviderType => ModerationProvider.OpenSource;

    public string ProviderName => TextDetection.OpenSourceProviderName;

    public bool IsAvailable => true;

    public string? AvailabilityError => null;

    public Task<TextProviderAnalysisResult> AnalyzeTextAsync(
        string text,
        CancellationToken cancellationToken = default)
    {
        var stopwatch = Stopwatch.StartNew();

        if (string.IsNullOrWhiteSpace(text))
        {
            stopwatch.Stop();
            return Task.FromResult(TextProviderAnalysisResult.Failure(
                ProviderName,
                ProviderType,
                "Text input cannot be null or empty.",
                stopwatch.Elapsed));
        }

        cancellationToken.ThrowIfCancellationRequested();

        var opts = _optionsMonitor.CurrentValue.OpenSource;
        var detections = new List<TextDetection>();

        if (_onnxSession is not null && _vocab is not null)
        {
            detections.AddRange(RunOnnxBertInference(text, opts));
        }

        var heuristicDetections = EvaluateContextualRules(text, opts);
        foreach (var item in heuristicDetections)
        {
            if (detections.All(d => !string.Equals(d.RawCategory, item.RawCategory, StringComparison.OrdinalIgnoreCase)))
            {
                detections.Add(item);
            }
        }

        stopwatch.Stop();
        return Task.FromResult(TextProviderAnalysisResult.Success(
            ProviderName,
            ProviderType,
            detections,
            stopwatch.Elapsed));
    }

    internal static IReadOnlyList<TextDetection> EvaluateContextualRules(
        string text,
        OpenSourceTextModerationOptions opts)
    {
        var detections = new List<TextDetection>();
        var isEducationalContext = opts.EnableContextualFalsePositiveReduction &&
                                   EducationalOrContextualMarker.IsMatch(text);

        foreach (var rule in Rules)
        {
            var matches = rule.Pattern.Matches(text);
            if (matches.Count == 0)
            {
                continue;
            }

            var confidence = Math.Min(0.99, rule.BaseConfidence + ((matches.Count - 1) * 0.03));

            // Dampen score when educational/clinical/prevention context is present to reduce false positives
            if (isEducationalContext)
            {
                confidence *= 0.45;
            }

            if (confidence >= opts.MinimumConfidence)
            {
                detections.Add(TextDetection.FromOpenSource(
                    rule.Category,
                    rule.RawLabel,
                    confidence,
                    rule.Description));
            }
        }

        return detections;
    }

    private IReadOnlyList<TextDetection> RunOnnxBertInference(
        string text,
        OpenSourceTextModerationOptions opts)
    {
        var tokenIds = TokenizeWordPiece(text, _vocab!, maxLength: 128);
        var seqLen = tokenIds.Count;

        var inputIds = new DenseTensor<long>([1, seqLen]);
        var attentionMask = new DenseTensor<long>([1, seqLen]);
        var tokenTypeIds = new DenseTensor<long>([1, seqLen]);

        for (var i = 0; i < seqLen; i++)
        {
            inputIds[0, i] = tokenIds[i];
            attentionMask[0, i] = 1L;
            tokenTypeIds[0, i] = 0L;
        }

        var inputs = new List<NamedOnnxValue>
        {
            NamedOnnxValue.CreateFromTensor("input_ids", inputIds),
            NamedOnnxValue.CreateFromTensor("attention_mask", attentionMask)
        };

        if (_onnxSession!.InputMetadata.ContainsKey("token_type_ids"))
        {
            inputs.Add(NamedOnnxValue.CreateFromTensor("token_type_ids", tokenTypeIds));
        }

        using var results = _onnxSession.Run(inputs);
        var logits = results.FirstOrDefault()?.AsTensor<float>();
        if (logits is null)
        {
            return Array.Empty<TextDetection>();
        }

        // toxic-bert 6 labels: 0=toxic, 1=severe_toxic, 2=obscene, 3=threat, 4=insult, 5=identity_hate
        (TextModerationCategory Cat, string Label)[] labelMap =
        [
            (TextModerationCategory.ProfanityAndAbuse, "TOXIC"),
            (TextModerationCategory.ProfanityAndAbuse, "SEVERE_TOXIC"),
            (TextModerationCategory.SexualAndExplicit, "OBSCENE"),
            (TextModerationCategory.ThreatsAndViolence, "THREAT"),
            (TextModerationCategory.HateAndHarassment, "INSULT"),
            (TextModerationCategory.HateAndHarassment, "IDENTITY_HATE")
        ];

        var list = new List<TextDetection>();
        var count = Math.Min(labelMap.Length, (int)logits.Length);
        for (var i = 0; i < count; i++)
        {
            var prob = 1.0 / (1.0 + Math.Exp(-logits[0, i]));
            if (prob >= opts.MinimumConfidence)
            {
                list.Add(TextDetection.FromOpenSource(labelMap[i].Cat, labelMap[i].Label, prob));
            }
        }

        return list;
    }

    private static Dictionary<string, int> LoadVocab(string vocabPath)
    {
        var lines = File.ReadAllLines(vocabPath);
        var dict = new Dictionary<string, int>(lines.Length, StringComparer.Ordinal);
        for (var i = 0; i < lines.Length; i++)
        {
            dict.TryAdd(lines[i].Trim(), i);
        }
        return dict;
    }

    private static List<long> TokenizeWordPiece(string text, Dictionary<string, int> vocab, int maxLength)
    {
        var clsId = vocab.GetValueOrDefault("[CLS]", 101);
        var sepId = vocab.GetValueOrDefault("[SEP]", 102);
        var unkId = vocab.GetValueOrDefault("[UNK]", 100);

        var tokens = new List<long>(maxLength) { clsId };
        var words = Regex.Split(text.ToLowerInvariant(), @"(\W+)");

        foreach (var raw in words)
        {
            var word = raw.Trim();
            if (string.IsNullOrEmpty(word))
            {
                continue;
            }

            if (tokens.Count >= maxLength - 1)
            {
                break;
            }

            if (vocab.TryGetValue(word, out var id))
            {
                tokens.Add(id);
            }
            else
            {
                tokens.Add(unkId);
            }
        }

        tokens.Add(sepId);
        return tokens;
    }

    public void Dispose()
    {
        _onnxSession?.Dispose();
    }
}
