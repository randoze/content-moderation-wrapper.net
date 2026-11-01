using ContentModeration.Models;
using ContentModeration.Providers.Azure;
using Microsoft.Extensions.Logging.Abstractions;

namespace ContentModeration.Tests;

public sealed class AzureTextBlocklistManagerTests
{
    [Fact]
    public void ResolveDefinitions_WithBuiltInFilipinoBlocklistsEnabled_LoadsAllSixEmbeddedLists()
    {
        var options = new AzureTextModerationOptions
        {
            UseBuiltInFilipinoBlocklists = true
        };

        var definitions = AzureTextBlocklistManager.ResolveDefinitions(options, NullLogger.Instance);

        Assert.Equal(6, definitions.Count);
        Assert.Contains(definitions, d => d.Name == "fil-ceb-profanity" && d.Category == TextModerationCategory.ProfanityAndAbuse && d.Severity == 4 && d.Terms.Count > 0);
        Assert.Contains(definitions, d => d.Name == "fil-ceb-insults" && d.Category == TextModerationCategory.ProfanityAndAbuse && d.Severity == 2 && d.Terms.Count > 0);
        Assert.Contains(definitions, d => d.Name == "fil-ceb-sexual" && d.Category == TextModerationCategory.SexualAndExplicit && d.Severity == 6 && d.Terms.Count > 0);
        Assert.Contains(definitions, d => d.Name == "fil-ceb-hate" && d.Category == TextModerationCategory.HateAndHarassment && d.Severity == 4 && d.Terms.Count > 0);
        Assert.Contains(definitions, d => d.Name == "fil-ceb-threats" && d.Category == TextModerationCategory.ThreatsAndViolence && d.Severity == 6 && d.Terms.Count > 0);
        Assert.Contains(definitions, d => d.Name == "fil-ceb-selfharm" && d.Category == TextModerationCategory.SelfHarm && d.Severity == 6 && d.Terms.Count > 0);
    }

    [Fact]
    public void ResolveDefinitions_WithBuiltInDisabledAndCustomBlocklist_ResolvesAndNormalizesCustomTerms()
    {
        var tempFile = Path.GetTempFileName();
        try
        {
            File.WriteAllLines(tempFile, [
                "# Comment line should be ignored",
                "  CustomTermOne  ",
                "",
                "CUSTOMTERMONE", // duplicate (case-insensitive)
                new string('x', 130) // exceeds 128 char limit, should be excluded
            ]);

            var options = new AzureTextModerationOptions
            {
                UseBuiltInFilipinoBlocklists = false,
                Blocklists =
                [
                    new TextBlocklistDefinition
                    {
                        Name = "custom-list",
                        Description = "Custom test blocklist",
                        Category = TextModerationCategory.HateAndHarassment,
                        Severity = 10, // should clamp to 7
                        TermsFile = tempFile,
                        Terms = ["InlineTerm", "customtermone"]
                    }
                ]
            };

            var definitions = AzureTextBlocklistManager.ResolveDefinitions(options, NullLogger.Instance);

            var single = Assert.Single(definitions);
            Assert.Equal("custom-list", single.Name);
            Assert.Equal("Custom test blocklist", single.Description);
            Assert.Equal(TextModerationCategory.HateAndHarassment, single.Category);
            Assert.Equal(7, single.Severity);
            Assert.Equal(2, single.Terms.Count);
            Assert.Contains("inlineterm", single.Terms);
            Assert.Contains("customtermone", single.Terms);
        }
        finally
        {
            File.Delete(tempFile);
        }
    }

    [Fact]
    public void ResolveDefinitions_WhenCustomListMatchesBuiltInName_MergesTerms()
    {
        var options = new AzureTextModerationOptions
        {
            UseBuiltInFilipinoBlocklists = true,
            Blocklists =
            [
                new TextBlocklistDefinition
                {
                    Name = "fil-ceb-profanity",
                    Category = TextModerationCategory.ProfanityAndAbuse,
                    Severity = 4,
                    Terms = ["unique-custom-profanity-term"]
                }
            ]
        };

        var definitions = AzureTextBlocklistManager.ResolveDefinitions(options, NullLogger.Instance);
        var profanityList = Assert.Single(definitions, d => d.Name == "fil-ceb-profanity");

        Assert.Contains("unique-custom-profanity-term", profanityList.Terms);
        Assert.True(profanityList.Terms.Count > 1);
    }

    [Fact]
    public void GetReadyBlocklistsAndEnsureProvisioning_WhenAutoProvisionDisabled_ReturnsEmptyWithoutInvokingFactory()
    {
        var manager = new AzureTextBlocklistManager(NullLogger.Instance);
        var factoryCalled = false;
        var options = new AzureTextModerationOptions
        {
            AutoProvisionBlocklists = false
        };

        var ready = manager.GetReadyBlocklistsAndEnsureProvisioning(
            () =>
            {
                factoryCalled = true;
                throw new InvalidOperationException("Should not be called.");
            },
            "cred-fp",
            options);

        Assert.Empty(ready);
        Assert.False(factoryCalled);
    }
}
