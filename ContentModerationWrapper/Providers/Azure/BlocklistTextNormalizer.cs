using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace ContentModeration.Providers.Azure;

/// <summary>
/// Normalizes obfuscated text (e.g. <c>T4NG1NA</c>, <c>g.a.g.o</c>, <c>gagooooo</c>, <c>p0ta</c>) so that
/// Azure AI Content Safety blocklist items can match common Filipino/Taglish obfuscation patterns.
/// </summary>
public static class BlocklistTextNormalizer
{
    private static readonly Dictionary<char, char> LeetMap = new()
    {
        ['4'] = 'a', ['@'] = 'a', ['1'] = 'i', ['!'] = 'i', ['3'] = 'e',
        ['0'] = 'o', ['5'] = 's', ['$'] = 's', ['7'] = 't'
    };

    // Separators placed between single letters inside a word: "g.a.g.o", "p-u-t-a", "t_a_n_g_i_n_a"
    private static readonly Regex InWordSeparator = new(@"(?<=\p{L})[.\-_*]+(?=\p{L})", RegexOptions.Compiled);

    // Letters elongated 3+ times: "gagoooo" -> "gago"
    private static readonly Regex ElongatedLetters = new(@"(\p{L})\1{2,}", RegexOptions.Compiled);

    /// <summary>Returns a normalized form of <paramref name="text"/> for blocklist matching.</summary>
    public static string Normalize(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return string.Empty;
        }

        var decomposed = text.ToLowerInvariant().Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder(decomposed.Length);

        for (var i = 0; i < decomposed.Length; i++)
        {
            var c = decomposed[i];
            if (CharUnicodeInfo.GetUnicodeCategory(c) == UnicodeCategory.NonSpacingMark)
            {
                continue; // strip accents: "putañgina" -> "putangina"
            }

            // Only decode leet characters surrounded by letters (e.g. "t4ngina", "p0ta", "g@go"), so normal numbers
            // and trailing punctuation ("100", "$5", "gago!") are left untouched
            if (LeetMap.TryGetValue(c, out var mapped) && IsBetweenLetters(decomposed, i))
            {
                sb.Append(mapped);
            }
            else
            {
                sb.Append(c);
            }
        }

        var result = InWordSeparator.Replace(sb.ToString(), string.Empty);
        result = ElongatedLetters.Replace(result, "$1");
        return result.Normalize(NormalizationForm.FormC);
    }

    /// <summary>
    /// Builds the text sent to Azure: the original text, plus the normalized form when it differs and fits within
    /// <paramref name="maxLength"/>.
    /// </summary>
    public static string BuildAnalysisText(string text, bool appendNormalized, int maxLength)
    {
        if (!appendNormalized)
        {
            return text;
        }

        var normalized = Normalize(text);
        if (string.Equals(normalized, text.ToLowerInvariant(), StringComparison.Ordinal))
        {
            return text;
        }

        var combined = text + "\n" + normalized;
        return combined.Length <= maxLength ? combined : text;
    }

    private static bool IsBetweenLetters(string s, int index) =>
        index > 0 && index < s.Length - 1 &&
        char.IsLetter(s[index - 1]) && char.IsLetter(s[index + 1]);
}
