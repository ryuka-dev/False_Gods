using System;
using System.IO;
using System.Linq;
using FalseGods.ArchitectureTests.Inspection;
using Xunit;

namespace FalseGods.ArchitectureTests.Checks;

/// <summary>
/// The throwaway PoC probe (tools/FalseGods.Probe) reads AstarPath, Addressables and game singletons
/// directly — it answers to none of the FG-ARCH boundaries, by design. The one thing that must stay true is
/// that its disregard for the rules cannot leak into the production graph: nothing under src/ may reference
/// it, and deleting it must never break a production build.
///
/// These checks are not tied to a FG-ARCH rule id (there is no architecture rule about a diagnostic tool that
/// is scheduled for deletion). They exist so "delete the probe" stays a one-line change, and so a future
/// edit cannot quietly promote the probe into a production dependency.
/// </summary>
public sealed class ProbeIsIsolatedChecks
{
    private const string ProbeAssembly = "FalseGods.Probe";

    private static readonly string[] ProductionProjects =
    {
        "FalseGods.Core",
        "FalseGods.Protocol",
        "FalseGods.RuntimeContracts",
        "FalseGods.Application",
        "FalseGods.UnityRuntime",
        "FalseGods.Integration.Sulfur",
        "FalseGods.Integration.SulfurTogether",
        "FalseGods.Plugin",
    };

    [Fact]
    public void No_production_project_references_the_probe()
    {
        var offenders = ProductionProjects
            .SelectMany(project => ProjectGraphInspector.EvaluateAllConfigurations(RepoLayout.ProjectFile(project)))
            .Where(evaluated => evaluated.ReferencesAssemblyNamed(ProbeAssembly))
            .Select(evaluated => $"{Path.GetFileName(evaluated.ProjectPath)} [{evaluated.Configuration}]")
            .Distinct()
            .ToList();

        Assert.True(offenders.Count == 0,
            $"The PoC probe is a throwaway diagnostic and must not be a production dependency — deleting it has " +
            $"to stay a one-line change. Referenced by: {string.Join(", ", offenders)}. " +
            $"See tools/FalseGods.Probe/FalseGods.Probe.csproj.");
    }

    [Fact]
    public void The_probe_is_not_in_the_solution()
    {
        // Keeping it out of False Gods.slnx is what makes deletion a one-line change and keeps verify.ps1
        // (which builds the solution) from depending on the game being installed for the probe's sake.
        var solutionText = File.ReadAllText(Path.Combine(RepoLayout.Root, "False Gods.slnx"));

        Assert.DoesNotContain(ProbeAssembly, solutionText, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void The_probe_lives_under_tools_not_src()
    {
        // A probe that drifted into src/ would be swept up by the production checks and, worse, would read
        // as production code. Its location is part of its contract.
        Assert.True(File.Exists(Path.Combine(RepoLayout.Root, "tools", ProbeAssembly, ProbeAssembly + ".csproj")),
            $"Expected the probe at tools/{ProbeAssembly}/{ProbeAssembly}.csproj.");

        Assert.False(Directory.Exists(Path.Combine(RepoLayout.Root, "src", ProbeAssembly)),
            $"The probe must not live under src/ — it answers to none of the FG-ARCH boundaries and is disposable.");
    }
}
