using System;
using System.IO;
using System.Linq;
using FalseGods.ArchitectureTests.Inspection;
using FalseGods.ArchitectureTests.Rules;
using Xunit;

namespace FalseGods.ArchitectureTests.Checks;

/// <summary>
/// FG-ARCH-003 — FalseGods.UnityRuntime must not reference FalseGods.Protocol.
///
/// Presentation is driven by PresentationState / PresentationEvent, produced by the mapper in
/// FalseGods.Application. A wire DTO carries authoritative fields, and reading one "just for display" is
/// one line away from acting on it.
///
/// LAYER: this is the project-graph layer only. It sees the references FalseGods.UnityRuntime.csproj
/// itself declares, in every declared configuration, including any arriving through an import or behind a
/// condition — which is the mistake that compiles green. It does NOT read FalseGods.UnityRuntime.dll, so a
/// Protocol type reached transitively is out of its sight. That is the assembly-metadata layer, which
/// needs an outer assembly CI cannot build and is still Planned. See Docs/ArchitectureEnforcement.md
/// §5 FG-ARCH-003.
/// </summary>
public sealed class UnityRuntimeDoesNotReferenceProtocolChecks
{
    private const string RuleId = "FG-ARCH-003";

    private const string Presentation = "FalseGods.UnityRuntime";
    private const string ForbiddenAssembly = "FalseGods.Protocol";

    private static string Failure(string detail) =>
        $"{RuleId}: {detail}{Environment.NewLine}" +
        $"Presentation is driven by PresentationState/PresentationEvent, mapped in FalseGods.Application " +
        $"— see Docs/Architecture.md §7.{Environment.NewLine}" +
        $"See {ArchitectureRuleRegistry.DocLinkFor(RuleId)}";

    [Fact]
    [ArchitectureRule(RuleId)]
    public void UnityRuntime_project_graph_does_not_reference_protocol()
    {
        var evaluations = ProjectGraphInspector.EvaluateAllConfigurations(RepoLayout.ProjectFile(Presentation));

        // A graph check that silently covered zero configurations would pass forever.
        Assert.True(evaluations.Count > 0, Failure($"{Presentation} declares no configurations to evaluate."));

        var offences = ForbiddenReferenceScanner.Scan(evaluations, new[] { ForbiddenAssembly });

        Assert.True(offences.Count == 0, Failure(
            $"{Presentation}'s evaluated MSBuild project graph references {ForbiddenAssembly} " +
            $"(configurations checked: {string.Join(", ", evaluations.Select(e => e.Configuration))}):" +
            $"{Environment.NewLine}{ForbiddenReferenceScanner.Format(offences)}"));
    }

    [Fact]
    [ArchitectureRule(RuleId)]
    public void The_protocol_project_exists_so_this_check_is_not_vacuous()
    {
        // A check that passes because its subject vanished is worse than no check.
        Assert.True(File.Exists(RepoLayout.ProjectFile(ForbiddenAssembly)),
            $"{RuleId}: the project {ForbiddenAssembly}.csproj no longer exists, so " +
            $"'{Presentation} does not reference it' is trivially true and this check proves nothing. " +
            $"Update the rule, or the constant. See {ArchitectureRuleRegistry.DocLinkFor(RuleId)}");
    }

    [Fact]
    [ArchitectureRule(RuleId)]
    public void The_evaluation_really_returned_a_reference_graph()
    {
        // If MSBuild ever reported no items at all — a changed -getItem contract, a project that failed to
        // evaluate but exited zero — every scan above would pass while inspecting nothing. Anchor on the
        // references UnityRuntime is SUPPOSED to have, so an empty graph fails instead of passing.
        var debug = ProjectGraphInspector.Evaluate(RepoLayout.ProjectFile(Presentation), "Debug");

        Assert.Contains(debug.ProjectReferences, reference => reference.ProjectName == "FalseGods.Core");
        Assert.Contains(debug.ProjectReferences, reference => reference.ProjectName == "FalseGods.RuntimeContracts");
    }
}
