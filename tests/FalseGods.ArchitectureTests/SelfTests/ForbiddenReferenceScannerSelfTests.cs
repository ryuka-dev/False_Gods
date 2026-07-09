using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using FalseGods.ArchitectureTests.Inspection;
using Xunit;

namespace FalseGods.ArchitectureTests.SelfTests;

/// <summary>
/// Proves the project-graph layer shared by FG-ARCH-003, FG-ARCH-005 and FG-ARCH-006 detects what those
/// rules claim it detects, and stays quiet on a conforming graph.
///
/// Two halves, on purpose:
///
///   pure      the scanner's reporting logic, over graphs built by hand. No MSBuild, no disk, no project
///             that violates anything.
///   fixtures  the same logic through the real MSBuild evaluation, against synthetic projects under
///             tests/Fixtures. Nothing temporary is ever written into src/.
/// </summary>
public sealed class ForbiddenReferenceScannerSelfTests
{
    // ----------------------------------------------------------------- pure

    private static EvaluatedReferences Graph(
        string projectName,
        string configuration = "Debug",
        IEnumerable<EvaluatedProjectReference>? projectReferences = null,
        IEnumerable<EvaluatedAssemblyReference>? assemblyReferences = null) =>
        new(ProjectPath: $@"C:\repo\src\{projectName}\{projectName}.csproj",
            Configuration: configuration,
            DeclaredConfigurations: new[] { "Debug", "Release" },
            ProjectReferences: (projectReferences ?? Array.Empty<EvaluatedProjectReference>()).ToList(),
            AssemblyReferences: (assemblyReferences ?? Array.Empty<EvaluatedAssemblyReference>()).ToList());

    private static EvaluatedProjectReference ProjectRef(string name) =>
        new(Identity: $@"..\{name}\{name}.csproj", ProjectName: name, DefiningProject: "definer.csproj");

    private static EvaluatedAssemblyReference AssemblyRef(string identity, string? hintPathFile = null) =>
        new(Identity: identity, AssemblyName: identity.Split(',')[0].Trim(),
            HintPathFileName: hintPathFile, DefiningProject: "definer.csproj");

    [Fact]
    public void A_conforming_graph_produces_no_offence()
    {
        var graph = Graph("FalseGods.UnityRuntime",
            projectReferences: new[] { ProjectRef("FalseGods.Core"), ProjectRef("FalseGods.RuntimeContracts") },
            assemblyReferences: new[] { AssemblyRef("UnityEngine", "UnityEngine") });

        Assert.Empty(ForbiddenReferenceScanner.Scan(new[] { graph }, new[] { "FalseGods.Protocol" }));
    }

    [Fact]
    public void A_forbidden_project_reference_is_reported_with_project_and_configuration()
    {
        var graph = Graph("FalseGods.UnityRuntime", "Release",
            projectReferences: new[] { ProjectRef("FalseGods.Protocol") });

        var offence = Assert.Single(ForbiddenReferenceScanner.Scan(new[] { graph }, new[] { "FalseGods.Protocol" }));

        Assert.Equal("FalseGods.UnityRuntime", offence.ProjectName);
        Assert.Equal("Release", offence.Configuration);
        Assert.Equal("FalseGods.Protocol", offence.ForbiddenAssembly);
        Assert.Contains("ProjectReference", offence.Description, StringComparison.Ordinal);
    }

    [Fact]
    public void A_forbidden_assembly_reached_only_through_a_hint_path_is_reported()
    {
        // The Include names something innocuous; only the HintPath betrays it. This is the shape a check
        // comparing Reference identities would pass.
        var graph = Graph("FalseGods.Plugin",
            assemblyReferences: new[] { AssemblyRef("VendorHarmonyRepack", hintPathFile: "0Harmony") });

        var offence = Assert.Single(ForbiddenReferenceScanner.Scan(new[] { graph }, new[] { "0Harmony" }));

        Assert.Equal("0Harmony", offence.ForbiddenAssembly);
    }

    [Fact]
    public void An_assembly_identity_containing_a_space_is_matched()
    {
        // "SULFUR Together" is the AssemblyName. "SULFURTogether" is the namespace. Confusing the two is
        // how a check ends up matching nothing at all.
        var graph = Graph("FalseGods.Application",
            assemblyReferences: new[] { AssemblyRef("SULFUR Together, Version=1.0.0.0, Culture=neutral") });

        Assert.Single(ForbiddenReferenceScanner.Scan(new[] { graph }, new[] { "SULFUR Together" }));
        Assert.Empty(ForbiddenReferenceScanner.Scan(new[] { graph }, new[] { "SULFURTogether" }));
    }

    [Fact]
    public void Every_forbidden_assembly_and_every_configuration_is_reported_not_just_the_first()
    {
        var debug = Graph("FalseGods.Application", "Debug", assemblyReferences: new[] { AssemblyRef("LiteNetLib") });
        var release = Graph("FalseGods.Application", "Release", assemblyReferences: new[]
        {
            AssemblyRef("LiteNetLib"),
            AssemblyRef("Steamworks.NET"),
        });

        var offences = ForbiddenReferenceScanner.Scan(new[] { debug, release }, new[] { "LiteNetLib", "Steamworks.NET" });

        Assert.Equal(3, offences.Count);
        Assert.Contains(offences, o => o.Configuration == "Debug" && o.ForbiddenAssembly == "LiteNetLib");
        Assert.Contains(offences, o => o.Configuration == "Release" && o.ForbiddenAssembly == "Steamworks.NET");
    }

    [Fact]
    public void Scanning_for_an_empty_forbidden_set_throws_rather_than_passing()
    {
        // A scan for nothing passes for every input. That is the failure mode where a check reports green
        // while inspecting nothing, and it must be impossible to reach by accident.
        var graph = Graph("FalseGods.Core");

        Assert.Throws<ArgumentException>(() => ForbiddenReferenceScanner.Scan(new[] { graph }, Array.Empty<string>()));
    }

    [Fact]
    public void Matching_is_case_insensitive_as_msbuild_and_the_clr_are()
    {
        var graph = Graph("FalseGods.Plugin", assemblyReferences: new[] { AssemblyRef("litenetlib") });

        Assert.Single(ForbiddenReferenceScanner.Scan(new[] { graph }, new[] { "LiteNetLib" }));
    }

    // ------------------------------------------------------------- fixtures

    private static IReadOnlyList<ForbiddenReference> ScanFixture(string fixture, string configuration, params string[] forbidden) =>
        ForbiddenReferenceScanner.Scan(
            new[] { ProjectGraphInspector.Evaluate(RepoLayout.FixtureProjectFile(fixture), configuration) },
            forbidden);

    [Fact]
    public void FG_ARCH_003_an_unused_project_reference_to_protocol_is_detected()
    {
        const string fixture = "ForbiddenProtocolReference";
        var fixturePath = RepoLayout.FixtureProjectFile(fixture);

        // The premise: no source, so nothing about the forbidden reference could reach an AssemblyRef table.
        Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(fixturePath)!, "*.cs", SearchOption.AllDirectories));

        var offence = Assert.Single(ScanFixture(fixture, "Debug", "FalseGods.Protocol"));
        Assert.Contains("ProjectReference", offence.Description, StringComparison.Ordinal);
    }

    [Fact]
    public void FG_ARCH_003_the_real_unity_runtime_project_is_clean()
    {
        // Not vacuous: the same scan that fires on the fixture is quiet on the real project.
        var evaluations = ProjectGraphInspector.EvaluateAllConfigurations(RepoLayout.ProjectFile("FalseGods.UnityRuntime"));

        Assert.Empty(ForbiddenReferenceScanner.Scan(evaluations, new[] { "FalseGods.Protocol" }));
        Assert.NotEmpty(evaluations.SelectMany(e => e.ProjectReferences));
    }

    [Fact]
    public void FG_ARCH_005_litenetlib_is_detected_in_every_configuration()
    {
        Assert.Single(ScanFixture("ForbiddenTransportReference", "Debug", "LiteNetLib"));
        Assert.Single(ScanFixture("ForbiddenTransportReference", "Release", "LiteNetLib"));
    }

    [Fact]
    public void FG_ARCH_005_a_release_only_reference_to_sulfur_together_is_detected()
    {
        const string fixture = "ForbiddenTransportReference";

        // Invisible under MSBuild's default configuration…
        Assert.Empty(ScanFixture(fixture, "Debug", "SULFUR Together"));

        // …and present under Release, which is why the checks evaluate every declared configuration.
        var offence = Assert.Single(ScanFixture(fixture, "Release", "SULFUR Together"));
        Assert.Equal("SULFUR Together", offence.ForbiddenAssembly);

        var all = ProjectGraphInspector.EvaluateAllConfigurations(RepoLayout.FixtureProjectFile(fixture));
        Assert.NotEmpty(ForbiddenReferenceScanner.Scan(all, new[] { "SULFUR Together" }));
    }

    [Fact]
    public void FG_ARCH_006_harmony_behind_an_unrelated_reference_identity_is_detected()
    {
        const string fixture = "ForbiddenHarmonyReference";

        // The premise: the csproj never names 0Harmony as a Reference identity. Only its HintPath does.
        var csprojText = File.ReadAllText(RepoLayout.FixtureProjectFile(fixture));
        Assert.DoesNotContain("Include=\"0Harmony\"", csprojText, StringComparison.OrdinalIgnoreCase);

        var offence = Assert.Single(ScanFixture(fixture, "Debug", "0Harmony", "HarmonyLib"));
        Assert.Equal("0Harmony", offence.ForbiddenAssembly);
    }

    [Fact]
    public void The_allowed_fixture_graph_trips_none_of_the_three_rules()
    {
        // AllowedGraph references Integration.Sulfur, which does reference 0Harmony. Evaluation is
        // per-project and not transitive, so the forbidden name must not appear here. This test pins that
        // property: it is the exact reason the project-graph layer cannot claim to cover transitive
        // dependencies, and it would fail loudly if evaluation ever started resolving them.
        var evaluations = ProjectGraphInspector.EvaluateAllConfigurations(RepoLayout.FixtureProjectFile("AllowedGraph"));

        Assert.Empty(ForbiddenReferenceScanner.Scan(evaluations, new[] { "FalseGods.Protocol" }));
        Assert.Empty(ForbiddenReferenceScanner.Scan(evaluations, new[] { "0Harmony", "HarmonyLib" }));
        Assert.Empty(ForbiddenReferenceScanner.Scan(evaluations, new[] { "LiteNetLib", "SULFUR Together" }));

        Assert.Contains(evaluations.SelectMany(e => e.ProjectReferences),
            reference => reference.ProjectName == "FalseGods.Integration.Sulfur");
    }
}
