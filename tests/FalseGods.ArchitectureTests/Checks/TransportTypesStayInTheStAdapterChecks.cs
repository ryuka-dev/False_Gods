using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using FalseGods.ArchitectureTests.Inspection;
using FalseGods.ArchitectureTests.Rules;
using Xunit;

namespace FalseGods.ArchitectureTests.Checks;

/// <summary>
/// FG-ARCH-005 — SULFUR Together, LiteNetLib and Steamworks stay inside FalseGods.Integration.SulfurTogether.
///
/// Transport is invisible above the adapter: nothing else may know whether ST speaks UDP, LiteNetLib or
/// Steam P2P. Every project under src/ except the adapter is scanned, including projects that do not exist
/// yet — the set comes from disk, not from a list in this file.
///
/// LAYER, and this check is deliberately narrower than the rule:
///
///   covered      the project-graph layer — a `ProjectReference`, `Reference` or `HintPath` naming one of
///                the forbidden assemblies, in any declared configuration, however it arrived
///   NOT covered  the compiled AssemblyRef table (an outer assembly CI cannot build), and the rule's
///                second half — who may register with, revoke, or read the FalseGodsIntegrations broker.
///                The broker does not exist yet; both remain Planned.
///
/// So a green result here means "no project outside the adapter names a transport assembly". It does not
/// mean FG-ARCH-005 is fully enforced. See Docs/ArchitectureEnforcement.md §5 FG-ARCH-005.
/// </summary>
public sealed class TransportTypesStayInTheStAdapterChecks
{
    private const string RuleId = "FG-ARCH-005";

    /// <summary>The one assembly permitted to reach ST, LiteNetLib and Steamworks.</summary>
    private const string Adapter = "FalseGods.Integration.SulfurTogether";

    /// <summary>
    /// Assembly IDENTITIES, verified against the real projects rather than copied from prose.
    ///
    /// The distinction matters: `SULFURTogether` and `Steamworks` are NAMESPACES. A check that forbade
    /// only those two strings would never match anything, because no assembly is called either — and it
    /// would pass forever while claiming to guard the seam. They are listed anyway, as cheap aliases: a
    /// developer hand-writing a &lt;Reference Include="SULFURTogether"&gt; should be caught by the rule,
    /// not by MSB3245.
    /// </summary>
    private static readonly string[] ForbiddenAssemblies =
    {
        "SULFUR Together",                // ST's actual AssemblyName; it ships as "SULFUR Together.dll"
        "SULFURTogether",                 // ST's root namespace — an alias, see above
        "LiteNetLib",                     // ST's transport, taken from NuGet and deployed beside its plugin
        "com.rlabrecque.steamworks.net",  // the Steamworks.NET assembly shipped inside SULFUR's Managed dir
        "Steamworks.NET",                 // the same library's package name — an alias
        "Steamworks",                     // its root namespace — an alias
    };

    private static string Failure(string detail) =>
        $"{RuleId}: {detail}{Environment.NewLine}" +
        $"Transport is invisible above the adapter: upper layers use SessionPeerId, EncodedPayload and " +
        $"MessageDelivery — see Docs/DependencyRules.md §5.{Environment.NewLine}" +
        $"See {ArchitectureRuleRegistry.DocLinkFor(RuleId)}";

    /// <summary>Every production project except the adapter, discovered from disk.</summary>
    private static IReadOnlyList<string> ScannedProjects() =>
        RepoLayout.ProductionProjectNames()
            .Where(name => !string.Equals(name, Adapter, StringComparison.Ordinal))
            .ToList();

    [Fact]
    [ArchitectureRule(RuleId)]
    public void No_project_outside_the_st_adapter_references_transport_or_st()
    {
        var scanned = ScannedProjects();

        Assert.True(scanned.Count > 0, Failure("no production projects were found under src/ to scan."));

        var evaluations = scanned
            .SelectMany(project => ProjectGraphInspector.EvaluateAllConfigurations(RepoLayout.ProjectFile(project)))
            .ToList();

        Assert.True(evaluations.Count > 0, Failure("no project/configuration pairs were evaluated."));

        var offences = ForbiddenReferenceScanner.Scan(evaluations, ForbiddenAssemblies);

        Assert.True(offences.Count == 0, Failure(
            $"a project outside {Adapter} references a transport or SULFUR Together assembly." +
            $"{Environment.NewLine}  projects scanned: {string.Join(", ", scanned)}" +
            $"{Environment.NewLine}{ForbiddenReferenceScanner.Format(offences)}"));
    }

    [Fact]
    [ArchitectureRule(RuleId)]
    public void The_adapter_is_excluded_and_every_other_project_is_covered()
    {
        var all = RepoLayout.ProductionProjectNames();
        var scanned = ScannedProjects();

        // If the adapter were renamed, the exclusion would name a project that no longer exists and the
        // adapter itself would silently start being scanned — or, worse, a renamed project would be
        // excluded from nothing at all. Either way the coverage claim would be false.
        Assert.True(all.Contains(Adapter, StringComparer.Ordinal),
            $"{RuleId}: {Adapter} was not found under src/, so excluding it from the scan is meaningless. " +
            $"Found: {string.Join(", ", all)}. See {ArchitectureRuleRegistry.DocLinkFor(RuleId)}");

        // Exactly one project is exempt. Every other project under src/ — including one added tomorrow —
        // is scanned, because the set is read from disk.
        Assert.Equal(all.Count - 1, scanned.Count);
        Assert.DoesNotContain(Adapter, scanned);
    }
}
