using System;
using System.Collections.Generic;
using System.Linq;
using FalseGods.ArchitectureTests.Inspection;
using FalseGods.ArchitectureTests.Rules;
using Xunit;

namespace FalseGods.ArchitectureTests.Checks;

/// <summary>
/// FG-ARCH-002 — FalseGods.Plugin must not reference FalseGods.Integration.SulfurTogether.
///
/// Checked at two layers, because each misses what the other catches:
///
///   Project graph   a reference that is present but not yet used. It compiles green, it ships, and
///                   BepInEx resolves the missing DLL at load on a machine without SULFUR Together.
///   AssemblyRef     a real CLR dependency introduced through a signature, a typeof(), a base class,
///                   or a static-initialization path — regardless of what the csproj says.
///
/// The rule protects the property that breaks most quietly: single-player, on someone else's computer.
/// </summary>
public sealed class PluginDoesNotReferenceStAdapterChecks
{
    private const string Plugin = "FalseGods.Plugin";
    private const string ForbiddenAssembly = "FalseGods.Integration.SulfurTogether";

    private const string RuleId = "FG-ARCH-002";

    private static string Failure(string detail) =>
        $"{RuleId}: {detail}{Environment.NewLine}" +
        $"The adapter must self-register via FalseGodsIntegrations; the base plugin never names it." +
        $"{Environment.NewLine}See {ArchitectureRuleRegistry.DocLinkFor(RuleId)}";

    [Fact]
    [ArchitectureRule(RuleId)]
    public void Plugin_project_graph_does_not_reference_the_st_adapter()
    {
        var offences = new List<string>();

        foreach (var evaluated in ProjectGraphInspector.EvaluateAllConfigurations(RepoLayout.ProjectFile(Plugin)))
        {
            if (!evaluated.ReferencesAssemblyNamed(ForbiddenAssembly))
                continue;

            offences.AddRange(evaluated
                .DescribeReferencesTo(ForbiddenAssembly)
                .Select(reference => $"[{evaluated.Configuration}] {reference}"));
        }

        Assert.True(offences.Count == 0, Failure(
            $"{Plugin}'s evaluated MSBuild project graph references {ForbiddenAssembly}:{Environment.NewLine}  " +
            string.Join(Environment.NewLine + "  ", offences)));
    }

    [Fact]
    [ArchitectureRule(RuleId)]
    public void Plugin_assembly_metadata_does_not_reference_the_st_adapter()
    {
        var assemblyPath = RepoLayout.FindBuiltAssembly(Plugin);

        Assert.True(assemblyPath is not null,
            $"{RuleId}: {Plugin}.dll was not found under src/{Plugin}/bin/. " +
            $"Build the solution before running the architecture checks (scripts/verify.ps1 does this for you).");

        var referenced = AssemblyReferenceInspector.ReadReferencedAssemblyNames(assemblyPath!);

        var offending = referenced
            .Where(name => string.Equals(name, ForbiddenAssembly, StringComparison.OrdinalIgnoreCase))
            .ToList();

        Assert.True(offending.Count == 0, Failure(
            $"{Plugin}.dll has an AssemblyRef to {ForbiddenAssembly}. " +
            $"An assembly reference is resolved at type-load, not at a runtime null check.{Environment.NewLine}" +
            $"  assembly: {assemblyPath}{Environment.NewLine}" +
            $"  refs:     {string.Join(", ", referenced)}"));
    }

    [Fact]
    [ArchitectureRule(RuleId)]
    public void The_st_adapter_project_exists_so_this_check_is_not_vacuous()
    {
        // A check that passes because its subject vanished is worse than no check. If the adapter is
        // renamed, this fails and forces ForbiddenAssembly to be updated with it.
        Assert.True(System.IO.File.Exists(RepoLayout.ProjectFile(ForbiddenAssembly)),
            $"{RuleId}: the project {ForbiddenAssembly}.csproj no longer exists, so " +
            $"'{Plugin} does not reference it' is trivially true and this check proves nothing. " +
            $"Update the rule, or the constant. See {ArchitectureRuleRegistry.DocLinkFor(RuleId)}");
    }
}
