using System;
using System.Collections.Generic;
using System.Linq;
using FalseGods.ArchitectureTests.Inspection;
using FalseGods.ArchitectureTests.Rules;
using Xunit;

namespace FalseGods.ArchitectureTests.Checks;

/// <summary>
/// FG-ARCH-006 — Harmony lives only in a base-game anti-corruption layer.
///
/// The permitted set is an explicit ALLOW-LIST of assembly names, not a naming convention: a project must
/// not be able to grant itself the permission by choosing a name. It holds two today —
/// FalseGods.Integration.Sulfur (the boss composition's adapter) and FalseGods.Farm (the farm expansion's
/// own). See Docs/ADRs/ADR-007 for why it grew from one, and for what deliberately did NOT change with it.
///
/// FalseGods.Integration.SulfurTogether is NOT on the list: it reflects into ST internals, and reflection is
/// not a patch. The split is by TARGET.
///
/// LAYER, and this check is deliberately narrower than the rule:
///
///   covered      the project-graph layer — no project outside the allow-list references 0Harmony, in any
///                declared configuration, however the reference arrived
///   NOT covered  the rule's other half: that no TYPE outside the allow-list carries [HarmonyPatch].
///                That needs either the compiled outer assemblies (CI cannot build them) or a source scan;
///                it stays Planned. Today no project outside the list can even resolve the attribute,
///                precisely because this reference check holds — but "cannot resolve it" and "does not
///                carry it" are different claims, and only the first one is checked here.
///
/// See Docs/ArchitectureEnforcement.md §5 FG-ARCH-006.
/// </summary>
public sealed class HarmonyStaysInIntegrationSulfurChecks
{
    private const string RuleId = "FG-ARCH-006";

    /// <summary>
    /// The assemblies permitted to patch the base game. Every entry is a deliberate, ADR-backed decision;
    /// adding one is a change to Docs/DependencyRules.md §5, not an edit to this array.
    /// </summary>
    private static readonly string[] Patchers =
    {
        "FalseGods.Integration.Sulfur",
        "FalseGods.Farm",
    };

    /// <summary>
    /// Harmony's assembly is <c>0Harmony</c>; <c>HarmonyLib</c> is its namespace. Both are listed so that a
    /// hand-written &lt;Reference Include="HarmonyLib"&gt; is caught by this rule rather than by MSB3245.
    /// A HintPath ending in <c>0Harmony.dll</c> is matched whatever the Include says.
    /// </summary>
    private static readonly string[] ForbiddenAssemblies = { "0Harmony", "HarmonyLib" };

    private static string PatcherList => string.Join(" / ", Patchers);

    private static string Failure(string detail) =>
        $"{RuleId}: {detail}{Environment.NewLine}" +
        $"Patches belong in {PatcherList} — see Docs/DependencyRules.md §5. Widening that allow-list is a rule " +
        $"change with its own ADR, not a suppression.{Environment.NewLine}" +
        $"See {ArchitectureRuleRegistry.DocLinkFor(RuleId)}";

    /// <summary>Every production project except the allow-listed patchers, discovered from disk.</summary>
    private static IReadOnlyList<string> ScannedProjects() =>
        RepoLayout.ProductionProjectNames()
            .Where(name => !Patchers.Contains(name, StringComparer.Ordinal))
            .ToList();

    [Fact]
    [ArchitectureRule(RuleId)]
    public void No_project_outside_the_base_game_adapters_references_harmony()
    {
        var scanned = ScannedProjects();

        Assert.True(scanned.Count > 0, Failure("no production projects were found under src/ to scan."));

        var evaluations = scanned
            .SelectMany(project => ProjectGraphInspector.EvaluateAllConfigurations(RepoLayout.ProjectFile(project)))
            .ToList();

        Assert.True(evaluations.Count > 0, Failure("no project/configuration pairs were evaluated."));

        var offences = ForbiddenReferenceScanner.Scan(evaluations, ForbiddenAssemblies);

        Assert.True(offences.Count == 0, Failure(
            "a project outside the base-game anti-corruption layers references Harmony." +
            $"{Environment.NewLine}  projects scanned: {string.Join(", ", scanned)}" +
            $"{Environment.NewLine}{ForbiddenReferenceScanner.Format(offences)}"));
    }

    [Fact]
    [ArchitectureRule(RuleId)]
    public void Every_exempted_project_really_does_reference_harmony_so_the_check_is_not_vacuous()
    {
        // The strongest guard available for this rule, and it now guards two things. If 0Harmony were
        // renamed, moved, or dropped, the scan above would keep passing — on a forbidden name that now
        // matches nothing anywhere. And if a project were added to the allow-list that has no business
        // patching anything, this is what says so: an exemption is only legitimate for an assembly that
        // actually uses the permission.
        foreach (var patcher in Patchers)
        {
            var evaluations = ProjectGraphInspector.EvaluateAllConfigurations(RepoLayout.ProjectFile(patcher));

            Assert.All(evaluations, evaluated => Assert.True(
                ForbiddenReferenceScanner.Scan(new[] { evaluated }, ForbiddenAssemblies).Count > 0,
                $"{RuleId}: {patcher} [{evaluated.Configuration}] does not reference any of " +
                $"{string.Join(" / ", ForbiddenAssemblies)}, so exempting it proves nothing — either Harmony " +
                $"has been renamed or removed, or this project does not belong on the allow-list. " +
                $"See {ArchitectureRuleRegistry.DocLinkFor(RuleId)}"));
        }
    }

    [Fact]
    [ArchitectureRule(RuleId)]
    public void The_patchers_are_excluded_and_every_other_project_is_covered()
    {
        var all = RepoLayout.ProductionProjectNames();
        var scanned = ScannedProjects();

        foreach (var patcher in Patchers)
        {
            Assert.True(all.Contains(patcher, StringComparer.Ordinal),
                $"{RuleId}: {patcher} was not found under src/, so excluding it from the scan is meaningless. " +
                $"Found: {string.Join(", ", all)}. See {ArchitectureRuleRegistry.DocLinkFor(RuleId)}");
        }

        Assert.Equal(all.Count - Patchers.Length, scanned.Count);
        Assert.Empty(scanned.Intersect(Patchers, StringComparer.Ordinal));
    }
}
