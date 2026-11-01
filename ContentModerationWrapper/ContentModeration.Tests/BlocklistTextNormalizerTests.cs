using ContentModeration.Providers.Azure;

namespace ContentModeration.Tests;

public sealed class BlocklistTextNormalizerTests
{
    [Theory]
    [InlineData(null, "")]
    [InlineData("", "")]
    public void Normalize_NullOrEmpty_ReturnsEmptyString(string? input, string expected)
    {
        var actual = BlocklistTextNormalizer.Normalize(input!);
        Assert.Equal(expected, actual);
    }

    [Theory]
    [InlineData("putañgina", "putangina")]
    [InlineData("T4NG1NA", "tangina")]
    [InlineData("g@go", "gago")]
    [InlineData("p0ta", "pota")]
    [InlineData("b!tch", "bitch")]
    [InlineData("g.a.g.o", "gago")]
    [InlineData("p-u-t-a", "puta")]
    [InlineData("t_a_n_g_i_n_a", "tangina")]
    [InlineData("g*a*g*o", "gago")]
    [InlineData("gagooooo", "gago")]
    [InlineData("putaaaa", "puta")]
    [InlineData("book", "book")] // 2 consecutive identical letters should remain intact
    public void Normalize_DeobfuscatesAccentsLeetSeparatorsAndElongation(string input, string expected)
    {
        var actual = BlocklistTextNormalizer.Normalize(input);
        Assert.Equal(expected, actual);
    }

    [Fact]
    public void Normalize_PreservesStandaloneNumbersAndTrailingPunctuation()
    {
        var normalized = BlocklistTextNormalizer.Normalize("I paid $5 and 100 pesos, gago!");
        Assert.Equal("i paid $5 and 100 pesos, gago!", normalized);
    }

    [Fact]
    public void BuildAnalysisText_WhenAppendDisabled_ReturnsOriginalText()
    {
        var original = "t4ng1na mo";
        var result = BlocklistTextNormalizer.BuildAnalysisText(original, appendNormalized: false, maxLength: 1000);
        Assert.Equal(original, result);
    }

    [Fact]
    public void BuildAnalysisText_WhenNormalizedMatchesLowerOriginal_DoesNotDuplicate()
    {
        var original = "Hello World";
        var result = BlocklistTextNormalizer.BuildAnalysisText(original, appendNormalized: true, maxLength: 1000);
        Assert.Equal(original, result);
    }

    [Fact]
    public void BuildAnalysisText_WhenNormalizedDiffersAndFitsWithinLimit_AppendsNormalizedLine()
    {
        var original = "T4NG1NA g.a.g.o";
        var result = BlocklistTextNormalizer.BuildAnalysisText(original, appendNormalized: true, maxLength: 1000);
        Assert.Equal("T4NG1NA g.a.g.o\ntangina gago", result);
    }

    [Fact]
    public void BuildAnalysisText_WhenCombinedExceedsMaxLength_ReturnsOriginalTextOnly()
    {
        var original = "T4NG1NA";
        var result = BlocklistTextNormalizer.BuildAnalysisText(original, appendNormalized: true, maxLength: 10);
        Assert.Equal(original, result);
    }
}
