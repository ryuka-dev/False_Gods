using FalseGods.ArchitectureTests.Rules;
using Xunit;

namespace FalseGods.ArchitectureTests.SelfTests;

/// <summary>
/// Proves FG-ARCH-010's logic detects both kinds of registry rot, using deliberately wrong registries
/// rather than the real one. The real registry is (we hope) always correct, so it can never demonstrate
/// that the validator would notice if it were not.
/// </summary>
public sealed class RuleRegistryValidatorSelfTests
{
    private static ArchitectureRule Rule(string id, CheckStatus status = CheckStatus.Planned) =>
        new(id, "synthetic", status, CompilerProtection.None);

    [Fact]
    public void Detects_a_check_citing_an_unregistered_rule_id()
    {
        var registry = new[] { Rule("FG-ARCH-002") };

        var unknown = RuleRegistryValidator.FindUnknownRuleIds(
            new[] { "FG-ARCH-002", "FG-ARCH-999" }, registry);

        Assert.Equal(new[] { "FG-ARCH-999" }, unknown);
    }

    [Fact]
    public void Accepts_checks_that_cite_only_registered_rule_ids()
    {
        var registry = new[] { Rule("FG-ARCH-002"), Rule("FG-ARCH-010") };

        Assert.Empty(RuleRegistryValidator.FindUnknownRuleIds(new[] { "FG-ARCH-010" }, registry));
    }

    [Fact]
    public void Detects_a_rule_declared_enforced_that_no_check_cites()
    {
        var registry = new[]
        {
            Rule("FG-ARCH-002", CheckStatus.Implemented),
            Rule("FG-ARCH-005", CheckStatus.RequiredInCi),
            Rule("FG-ARCH-008", CheckStatus.Planned),   // Planned needs no check.
        };

        var unenforced = RuleRegistryValidator.FindEnforcedRulesWithoutChecks(registry, new[] { "FG-ARCH-002" });

        Assert.Equal(new[] { "FG-ARCH-005" }, unenforced);
    }

    [Fact]
    public void Detects_malformed_and_duplicate_rule_ids()
    {
        var registry = new[] { Rule("FG-ARCH-2"), Rule("FGARCH-003"), Rule("FG-ARCH-004"), Rule("FG-ARCH-004") };

        Assert.Equal(new[] { "FG-ARCH-2", "FGARCH-003" }, RuleRegistryValidator.FindMalformedRuleIds(registry));
        Assert.Equal(new[] { "FG-ARCH-004" }, RuleRegistryValidator.FindDuplicateRuleIds(registry));
    }

    [Fact]
    public void Parses_rule_ids_from_the_enforcement_document()
    {
        const string markdown = """
            ## 5. Rule registry

            ### FG-ARCH-001 — Core references no outer technology
            some prose mentioning FG-ARCH-999 which is not a heading

            ### FG-ARCH-002 — The base plugin does not reference the ST adapter
            """;

        Assert.Equal(new[] { "FG-ARCH-001", "FG-ARCH-002" },
            RuleRegistryValidator.ParseRuleIdsFromDocument(markdown));
    }

    [Fact]
    public void Detects_a_rule_whose_document_anchor_is_missing()
    {
        var registry = new[] { Rule("FG-ARCH-001"), Rule("FG-ARCH-002") };

        const string markdown = """
            <a id="fg-arch-001"></a>
            ### FG-ARCH-001 — anchored
            ### FG-ARCH-002 — not anchored
            """;

        Assert.Equal(new[] { "FG-ARCH-002" },
            RuleRegistryValidator.FindRuleIdsMissingDocAnchor(registry, markdown));
    }

    [Fact]
    public void Doc_link_points_at_the_rule_anchor()
    {
        Assert.Equal("Docs/ArchitectureEnforcement.md#fg-arch-002",
            ArchitectureRuleRegistry.DocLinkFor("FG-ARCH-002"));
    }
}
