using System.Linq;
using FalseGods.ArchitectureTests.Rules;
using Xunit;

namespace FalseGods.ArchitectureTests.SelfTests;

/// <summary>
/// Proves FG-ARCH-010's logic detects every kind of registry rot, using deliberately wrong registries and
/// documents rather than the real ones. The real registry is (we hope) always correct, so it can never
/// demonstrate that the validator would notice if it were not.
/// </summary>
public sealed class RuleRegistryValidatorSelfTests
{
    private static ArchitectureRule Rule(string id, CheckStatus status = CheckStatus.Planned) =>
        new(id, "synthetic", new[] { new RuleCheckLayer("project graph", status) }, CompilerProtection.None);

    private static ArchitectureRule Rule(string id, params RuleCheckLayer[] layers) =>
        new(id, "synthetic", layers, CompilerProtection.None);

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
    public void A_rule_is_enforced_if_any_single_layer_runs()
    {
        // The case the flat status model could not express: one layer gates every merge, another does not
        // exist yet. The rule still owes the registry a check, and one cited check satisfies it.
        var partiallyEnforced = Rule("FG-ARCH-006",
            new RuleCheckLayer("project graph", CheckStatus.RequiredInCi),
            new RuleCheckLayer("patch attribute scan", CheckStatus.Planned));

        var whollyPlanned = Rule("FG-ARCH-004",
            new RuleCheckLayer("public API signatures", CheckStatus.Planned));

        Assert.True(partiallyEnforced.IsEnforcedByAnyLayer);
        Assert.False(whollyPlanned.IsEnforcedByAnyLayer);

        Assert.Equal(new[] { "FG-ARCH-006" },
            RuleRegistryValidator.FindEnforcedRulesWithoutChecks(new[] { partiallyEnforced, whollyPlanned }, new string[0]));
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

    // ------------------------------------------------------ the layer-status table

    private const string TableDocument = """
        Prose before the table, mentioning FG-ARCH-001 in passing.

        <!-- BEGIN FG-ARCH-LAYER-STATUS -->

        | Rule | Check layer | Check status |
        |---|---|---|
        | FG-ARCH-002 | project graph | Required in CI |
        | **FG-ARCH-002** | assembly metadata | `Implemented` |
        | FG-ARCH-004 | public API signatures | Planned |

        <!-- END FG-ARCH-LAYER-STATUS -->

        | FG-ARCH-999 | outside the markers | Required in CI |
        """;

    [Fact]
    public void Parses_the_layer_status_table_and_ignores_everything_outside_the_markers()
    {
        var rows = RuleRegistryValidator.ParseLayerStatusTable(TableDocument);

        Assert.Equal(3, rows.Count);

        // Header and separator rows are skipped; bold and backticks are tolerated.
        Assert.Contains(rows, r => r.RuleId == "FG-ARCH-002" && r.Layer == "project graph" && r.Status == "Required in CI");
        Assert.Contains(rows, r => r.RuleId == "FG-ARCH-002" && r.Layer == "assembly metadata" && r.Status == "Implemented");

        // A table row after the end marker must not be picked up.
        Assert.DoesNotContain(rows, r => r.RuleId == "FG-ARCH-999");
    }

    [Fact]
    public void A_document_without_the_markers_parses_to_nothing_so_the_caller_must_fail()
    {
        // Renaming a marker must not quietly turn the comparison into "empty equals empty". The check that
        // consumes this asserts the result is non-empty for exactly this reason.
        Assert.Empty(RuleRegistryValidator.ParseLayerStatusTable("| FG-ARCH-002 | project graph | Planned |"));
        Assert.Empty(RuleRegistryValidator.ParseLayerStatusTable(""));
    }

    [Fact]
    public void Projects_the_registry_onto_one_row_per_layer()
    {
        var registry = new[]
        {
            Rule("FG-ARCH-006",
                new RuleCheckLayer("project graph", CheckStatus.RequiredInCi),
                new RuleCheckLayer("patch attribute scan", CheckStatus.Planned)),
        };

        var rows = RuleRegistryValidator.ProjectRegistryOntoLayerStatusRows(registry);

        Assert.Equal(new[] { "FG-ARCH-006 | project graph | Required in CI",
                             "FG-ARCH-006 | patch attribute scan | Planned" },
                     rows.Select(r => r.ToString()));
    }

    [Fact]
    public void Detects_a_layer_promoted_in_the_registry_but_not_in_the_document()
    {
        // The exact drift that went unnoticed before this check existed: the document and the registry
        // named the same rule and disagreed about whether it gates a merge.
        var document = RuleRegistryValidator.ParseLayerStatusTable("""
            <!-- BEGIN FG-ARCH-LAYER-STATUS -->
            | FG-ARCH-002 | project graph | Implemented |
            <!-- END FG-ARCH-LAYER-STATUS -->
            """);

        var registry = RuleRegistryValidator.ProjectRegistryOntoLayerStatusRows(
            new[] { Rule("FG-ARCH-002", CheckStatus.RequiredInCi) });

        var (onlyInDocument, onlyInRegistry) = RuleRegistryValidator.DiffLayerStatusRows(document, registry);

        Assert.Equal(new[] { "FG-ARCH-002 | project graph | Implemented" }, onlyInDocument);
        Assert.Equal(new[] { "FG-ARCH-002 | project graph | Required in CI" }, onlyInRegistry);
    }

    [Fact]
    public void Detects_a_layer_the_document_invents_and_a_layer_it_forgets()
    {
        var document = RuleRegistryValidator.ParseLayerStatusTable("""
            <!-- BEGIN FG-ARCH-LAYER-STATUS -->
            | FG-ARCH-005 | broker access | Required in CI |
            <!-- END FG-ARCH-LAYER-STATUS -->
            """);

        var registry = RuleRegistryValidator.ProjectRegistryOntoLayerStatusRows(new[]
        {
            Rule("FG-ARCH-005", new RuleCheckLayer("project graph", CheckStatus.RequiredInCi)),
        });

        var (onlyInDocument, onlyInRegistry) = RuleRegistryValidator.DiffLayerStatusRows(document, registry);

        Assert.Equal(new[] { "FG-ARCH-005 | broker access | Required in CI" }, onlyInDocument);
        Assert.Equal(new[] { "FG-ARCH-005 | project graph | Required in CI" }, onlyInRegistry);
    }

    [Fact]
    public void An_agreeing_document_and_registry_diff_to_nothing()
    {
        var document = RuleRegistryValidator.ParseLayerStatusTable(TableDocument);

        var registry = RuleRegistryValidator.ProjectRegistryOntoLayerStatusRows(new[]
        {
            Rule("FG-ARCH-002",
                new RuleCheckLayer("project graph", CheckStatus.RequiredInCi),
                new RuleCheckLayer("assembly metadata", CheckStatus.Implemented)),
            Rule("FG-ARCH-004", new RuleCheckLayer("public API signatures", CheckStatus.Planned)),
        });

        var (onlyInDocument, onlyInRegistry) = RuleRegistryValidator.DiffLayerStatusRows(document, registry);

        Assert.Empty(onlyInDocument);
        Assert.Empty(onlyInRegistry);
    }
}
