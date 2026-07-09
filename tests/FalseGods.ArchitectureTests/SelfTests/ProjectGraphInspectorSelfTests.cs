using System;
using System.IO;
using System.Linq;
using FalseGods.ArchitectureTests.Inspection;
using Xunit;

namespace FalseGods.ArchitectureTests.SelfTests;

/// <summary>
/// Proves the project-graph layer of FG-ARCH-002 detects the violations that no compiled-metadata check
/// could see, covers every declared configuration, and stays quiet on a conforming graph.
/// </summary>
public sealed class ProjectGraphInspectorSelfTests
{
    private const string ForbiddenAssembly = "FalseGods.Integration.SulfurTogether";

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
    public void A_conforming_graph_reports_no_violation()
    {
        var evaluated = ProjectGraphInspector.Evaluate(RepoLayout.FixtureProjectFile("AllowedGraph"), "Debug");

        Assert.False(evaluated.ReferencesAssemblyNamed(ForbiddenAssembly));

        // Not vacuous: the fixture really does resolve references, it just resolves allowed ones.
        Assert.Contains(evaluated.ProjectReferences, p => p.ProjectName == "FalseGods.Core");
    }

    [Fact]
    public void An_unused_forbidden_project_reference_is_still_detected()
    {
        var fixturePath = RepoLayout.FixtureProjectFile("ForbiddenProjectReference");

        // The premise of this fixture: it has no source, so nothing about the forbidden reference could
        // ever reach a compiled AssemblyRef table. If someone adds a .cs file, this test is no longer
        // testing what it says it tests.
        var sourceFiles = Directory.GetFiles(Path.GetDirectoryName(fixturePath)!, "*.cs", SearchOption.AllDirectories);
        Assert.Empty(sourceFiles);

        var evaluated = ProjectGraphInspector.Evaluate(fixturePath, "Debug");

        Assert.True(evaluated.ReferencesAssemblyNamed(ForbiddenAssembly));

        var description = string.Join(Environment.NewLine, evaluated.DescribeReferencesTo(ForbiddenAssembly));
        Assert.Contains("ProjectReference", description, StringComparison.Ordinal);
    }

    [Fact]
    public void A_forbidden_reference_added_by_an_import_behind_a_condition_is_detected()
    {
        var fixturePath = RepoLayout.FixtureProjectFile("ForbiddenReferenceViaImport");

        // The premise: the project file itself never names the forbidden assembly. A text search over
        // the csproj would pass. Evaluation must not.
        var csprojText = File.ReadAllText(fixturePath);
        Assert.DoesNotContain(ForbiddenAssembly, csprojText, StringComparison.OrdinalIgnoreCase);

        var evaluated = ProjectGraphInspector.Evaluate(fixturePath, "Debug");

        Assert.True(evaluated.ReferencesAssemblyNamed(ForbiddenAssembly));

        // And the failure can name the file the reference actually came from.
        var offending = evaluated.AssemblyReferences.Single(a =>
            string.Equals(a.AssemblyName, ForbiddenAssembly, StringComparison.OrdinalIgnoreCase));

        Assert.Equal("ForbiddenReference.props", Path.GetFileName(offending.DefiningProject));
    }

    [Fact]
    public void A_forbidden_reference_present_only_in_a_non_default_configuration_is_detected()
    {
        var fixturePath = RepoLayout.FixtureProjectFile("ForbiddenReferenceInReleaseOnly");

        // Invisible under MSBuild's default configuration…
        var debug = ProjectGraphInspector.Evaluate(fixturePath, "Debug");
        Assert.False(debug.ReferencesAssemblyNamed(ForbiddenAssembly));

        // …and present under Release.
        var release = ProjectGraphInspector.Evaluate(fixturePath, "Release");
        Assert.True(release.ReferencesAssemblyNamed(ForbiddenAssembly));

        // Which is precisely why the check evaluates every declared configuration, not just the default.
        var all = ProjectGraphInspector.EvaluateAllConfigurations(fixturePath);
        Assert.Contains(all, e => e.ReferencesAssemblyNamed(ForbiddenAssembly));
    }

    [Fact]
    public void Configurations_are_read_from_msbuild_not_hardcoded()
    {
        var declared = ProjectGraphInspector.GetDeclaredConfigurations(RepoLayout.ProjectFile("FalseGods.Plugin"));

        Assert.NotEmpty(declared);
        Assert.Contains("Debug", declared);
        Assert.Contains("Release", declared);
    }

    [Fact]
    public void Every_production_project_declares_the_same_configurations()
    {
        // The shared declaration lives in Directory.Build.props. If one project overrode it, the
        // per-project evaluation would silently cover a different set than verify.ps1 validates against.
        var baseline = ProjectGraphInspector.GetDeclaredConfigurations(RepoLayout.ProjectFile(ProductionProjects[0]));

        foreach (var project in ProductionProjects)
        {
            var declared = ProjectGraphInspector.GetDeclaredConfigurations(RepoLayout.ProjectFile(project));
            Assert.Equal(baseline, declared);
        }
    }

    [Fact]
    public void Evaluation_covers_every_declared_configuration()
    {
        var projectFile = RepoLayout.ProjectFile("FalseGods.Plugin");

        var declared = ProjectGraphInspector.GetDeclaredConfigurations(projectFile);
        var evaluated = ProjectGraphInspector.EvaluateAllConfigurations(projectFile);

        Assert.Equal(declared.Count, evaluated.Count);
        Assert.Equal(declared.OrderBy(c => c), evaluated.Select(e => e.Configuration).OrderBy(c => c));
    }

    [Fact]
    public void An_undeclared_configuration_is_rejected_rather_than_silently_evaluated()
    {
        var projectFile = RepoLayout.ProjectFile("FalseGods.Plugin");

        // MSBuild itself would happily evaluate `-p:Configuration=Nonsense` and return a graph. Accepting
        // it would mean every reference guarded by a real configuration goes unchecked, with a green result.
        var exception = Assert.Throws<UnknownConfigurationException>(
            () => ProjectGraphInspector.Evaluate(projectFile, "Nonsense"));

        Assert.Equal("Nonsense", exception.Configuration);
        Assert.Contains("Debug", exception.DeclaredConfigurations);
    }

    [Fact]
    public void A_missing_project_fails_loudly_rather_than_reporting_no_references()
    {
        var missing = RepoLayout.FixtureProjectFile("ThisFixtureDoesNotExist");

        Assert.Throws<FileNotFoundException>(() => ProjectGraphInspector.Evaluate(missing, "Debug"));
        Assert.Throws<FileNotFoundException>(() => ProjectGraphInspector.EvaluateAllConfigurations(missing));
    }
}
