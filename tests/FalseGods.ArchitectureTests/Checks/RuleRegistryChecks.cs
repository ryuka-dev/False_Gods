using System;
using System.IO;
using System.Linq;
using FalseGods.ArchitectureTests.Rules;
using Xunit;

namespace FalseGods.ArchitectureTests.Checks;

/// <summary>
/// FG-ARCH-010 — every enforced architecture check cites a valid rule id.
///
/// This catches the two ways the registry rots, neither of which anyone notices at the time:
///
///   a check enforcing a rule nobody wrote down  → FindUnknownRuleIds
///   a rule declared enforced that nothing checks → FindEnforcedRulesWithoutChecks
///
/// It also pins the registry to the document, so a rule cannot be added, renamed, or silently promoted
/// in one place and not the other.
/// </summary>
public sealed class RuleRegistryChecks
{
    private const string RuleId = "FG-ARCH-010";

    private static string Failure(string detail) =>
        $"{RuleId}: {detail}{Environment.NewLine}See {ArchitectureRuleRegistry.DocLinkFor(RuleId)}";

    private static string EnforcementDocument() =>
        File.ReadAllText(RepoLayout.Doc(ArchitectureRuleRegistry.DocumentPath));

    [Fact]
    [ArchitectureRule(RuleId)]
    public void Every_cited_rule_id_is_registered()
    {
        var cited = RuleRegistryValidator.CollectCitedRuleIds(typeof(RuleRegistryChecks).Assembly);
        var unknown = RuleRegistryValidator.FindUnknownRuleIds(cited, ArchitectureRuleRegistry.All);

        Assert.True(unknown.Count == 0, Failure(
            $"checks cite rule ids that no registry entry defines: {string.Join(", ", unknown)}. " +
            $"Add them to {ArchitectureRuleRegistry.DocumentPath} §5 and to ArchitectureRuleRegistry, or correct the id."));
    }

    [Fact]
    [ArchitectureRule(RuleId)]
    public void Every_rule_claiming_enforcement_has_at_least_one_check()
    {
        var cited = RuleRegistryValidator.CollectCitedRuleIds(typeof(RuleRegistryChecks).Assembly);
        var unenforced = RuleRegistryValidator.FindEnforcedRulesWithoutChecks(ArchitectureRuleRegistry.All, cited);

        Assert.True(unenforced.Count == 0, Failure(
            $"these rules are registered as Implemented or Required in CI but no check cites them: " +
            $"{string.Join(", ", unenforced)}. Either write the check, or lower the status back to Planned."));
    }

    [Fact]
    [ArchitectureRule(RuleId)]
    public void Rule_ids_are_well_formed_and_unique()
    {
        var malformed = RuleRegistryValidator.FindMalformedRuleIds(ArchitectureRuleRegistry.All);
        var duplicates = RuleRegistryValidator.FindDuplicateRuleIds(ArchitectureRuleRegistry.All);

        Assert.True(malformed.Count == 0, Failure($"malformed rule ids: {string.Join(", ", malformed)} (expected FG-ARCH-NNN)."));
        Assert.True(duplicates.Count == 0, Failure($"duplicate rule ids: {string.Join(", ", duplicates)}. Ids are never reused."));
    }

    [Fact]
    [ArchitectureRule(RuleId)]
    public void The_registry_and_the_enforcement_document_agree()
    {
        var documented = RuleRegistryValidator.ParseRuleIdsFromDocument(EnforcementDocument());
        var registered = ArchitectureRuleRegistry.All.Select(r => r.Id).OrderBy(id => id, StringComparer.Ordinal).ToList();

        var onlyInDoc = documented.Except(registered, StringComparer.Ordinal).ToList();
        var onlyInCode = registered.Except(documented, StringComparer.Ordinal).ToList();

        Assert.True(onlyInDoc.Count == 0 && onlyInCode.Count == 0, Failure(
            $"the registry and {ArchitectureRuleRegistry.DocumentPath} §5 disagree.{Environment.NewLine}" +
            $"  only in the document: {(onlyInDoc.Count == 0 ? "(none)" : string.Join(", ", onlyInDoc))}{Environment.NewLine}" +
            $"  only in the registry: {(onlyInCode.Count == 0 ? "(none)" : string.Join(", ", onlyInCode))}"));
    }

    [Fact]
    [ArchitectureRule(RuleId)]
    public void Every_rule_has_a_document_anchor_so_failure_links_resolve()
    {
        var missing = RuleRegistryValidator.FindRuleIdsMissingDocAnchor(ArchitectureRuleRegistry.All, EnforcementDocument());

        Assert.True(missing.Count == 0, Failure(
            $"these rules have no <a id=\"fg-arch-nnn\"></a> anchor in {ArchitectureRuleRegistry.DocumentPath}, " +
            $"so the link printed by their failure message does not resolve: {string.Join(", ", missing)}."));
    }
}
