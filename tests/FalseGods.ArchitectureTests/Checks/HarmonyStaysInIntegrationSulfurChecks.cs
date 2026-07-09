using System;
using System.Collections.Generic;
using System.Linq;
using FalseGods.ArchitectureTests.Inspection;
using FalseGods.ArchitectureTests.Rules;
using Xunit;

namespace FalseGods.ArchitectureTests.Checks;

/// <summary>
/// FG-ARCH-006 — Harmony lives only in FalseGods.Integration.Sulfur.
///
/// Patching the base game is one assembly's job. Note that FalseGods.Integration.SulfurTogether is NOT
/// exempt: it reflects into ST internals, and reflection is not a patch.
///
/// LAYER, and this check is deliberately narrower than the rule:
///
///   covered      the project-graph layer — no project except Integration.Sulfur references 0Harmony, in
///                any declared configuration, however the reference arrived
///   NOT covered  the rule's other half: that no TYPE outside Integration.Sulfur carries [HarmonyPatch].
///                That needs either the compiled outer assemblies (CI cannot build them) or a source scan;
///                it stays Planned. Today no project outside Integration.Sulfur can even resolve the
///                attribute, precisely because this reference check holds — but "cannot resolve it" and
///                "does not carry it" are different claims, and only the first one is checked here.
///
/// See Docs/ArchitectureEnforcement.md §5 FG-ARCH-006.
/// </summary>
public sealed class HarmonyStaysInIntegrationSulfurChecks
{
    private const string RuleId = "FG-ARCH-006";

    /// <summary>The one assembly permitted to patch the base game.</summary>
    private const string Patcher = "FalseGods.Integration.Sulfur";

    /// <summary>
    /// Harmony's assembly is <c>0Harmony</c>; <c>HarmonyLib</c> is its namespace. Both are listed so that a
    /// hand-written &lt;Reference Include="HarmonyLib"&gt; is caught by this rule rather than by MSB3245.
    /// A HintPath ending in <c>0Harmony.dll</c> is matched whatever the Include says.
    /// </summary>
    private static readonly string[] ForbiddenAssemblies = { "0Harmony", "HarmonyLib" };

    private static string Failure(string detail) =>
        $"{RuleId}: {detail}{Environment.NewLine}" +
        $"Patches belong in {Patcher} — see Docs/DependencyRules.md §5. An exception needs its own ADR, " +
        $"not a suppression.{Environment.NewLine}" +
        $"See {ArchitectureRuleRegistry.DocLinkFor(RuleId)}";

    /// <summary>Every production project except the patcher, discovered from disk.</summary>
    private static IReadOnlyList<string> ScannedProjects() =>
        RepoLayout.ProductionProjectNames()
            .Where(name => !string.Equals(name, Patcher, StringComparison.Ordinal))
            .ToList();

    [Fact]
    [ArchitectureRule(RuleId)]
    public void No_project_outside_integration_sulfur_references_harmony()
    {
        var scanned = ScannedProjects();

        Assert.True(scanned.Count > 0, Failure("no production projects were found under src/ to scan."));

        var evaluations = scanned
            .SelectMany(project => ProjectGraphInspector.EvaluateAllConfigurations(RepoLayout.ProjectFile(project)))
            .ToList();

        Assert.True(evaluations.Count > 0, Failure("no project/configuration pairs were evaluated."));

        var offences = ForbiddenReferenceScanner.Scan(evaluations, ForbiddenAssemblies);

        Assert.True(offences.Count == 0, Failure(
            $"a project outside {Patcher} references Harmony." +
            $"{Environment.NewLine}  projects scanned: {string.Join(", ", scanned)}" +
            $"{Environment.NewLine}{ForbiddenReferenceScanner.Format(offences)}"));
    }

    [Fact]
    [ArchitectureRule(RuleId)]
    public void Integration_sulfur_really_does_reference_harmony_so_the_check_is_not_vacuous()
    {
        // The strongest guard available for this rule. If 0Harmony were renamed, moved, or dropped from
        // Integration.Sulfur, the scan above would keep passing — on a forbidden name that now matches
        // nothing anywhere. This fails instead, and names the assembly the scan is looking for.
        var evaluations = ProjectGraphInspector.EvaluateAllConfigurations(RepoLayout.ProjectFile(Patcher));

        Assert.All(evaluations, evaluated => Assert.True(
            ForbiddenReferenceScanner.Scan(new[] { evaluated }, ForbiddenAssemblies).Count > 0,
            $"{RuleId}: {Patcher} [{evaluated.Configuration}] does not reference any of " +
            $"{string.Join(" / ", ForbiddenAssemblies)}, so the name this rule forbids everywhere else " +
            $"matches nothing and the check proves nothing. Has Harmony been renamed or removed? " +
            $"See {ArchitectureRuleRegistry.DocLinkFor(RuleId)}"));
    }

    [Fact]
    [ArchitectureRule(RuleId)]
    public void The_patcher_is_excluded_and_every_other_project_is_covered()
    {
        var all = RepoLayout.ProductionProjectNames();
        var scanned = ScannedProjects();

        Assert.True(all.Contains(Patcher, StringComparer.Ordinal),
            $"{RuleId}: {Patcher} was not found under src/, so excluding it from the scan is meaningless. " +
            $"Found: {string.Join(", ", all)}. See {ArchitectureRuleRegistry.DocLinkFor(RuleId)}");

        Assert.Equal(all.Count - 1, scanned.Count);
        Assert.DoesNotContain(Patcher, scanned);
    }
}
