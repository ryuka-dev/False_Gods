using System;
using System.IO;
using System.Linq;
using FalseGods.ArchitectureTests.Inspection;
using Xunit;

namespace FalseGods.ArchitectureTests.SelfTests;

/// <summary>
/// Proves the project-graph layer of FG-ARCH-002 detects the two violations that no compiled-metadata
/// check could see, and stays quiet on a conforming graph.
/// </summary>
public sealed class ProjectGraphInspectorSelfTests
{
    private const string ForbiddenAssembly = "FalseGods.Integration.SulfurTogether";

    [Fact]
    public void A_conforming_graph_reports_no_violation()
    {
        var evaluated = ProjectGraphInspector.Evaluate(RepoLayout.FixtureProjectFile("AllowedGraph"));

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

        var evaluated = ProjectGraphInspector.Evaluate(fixturePath);

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

        var evaluated = ProjectGraphInspector.Evaluate(fixturePath);

        Assert.True(evaluated.ReferencesAssemblyNamed(ForbiddenAssembly));

        // And the failure can name the file the reference actually came from.
        var offending = evaluated.AssemblyReferences.Single(a =>
            string.Equals(a.AssemblyName, ForbiddenAssembly, StringComparison.OrdinalIgnoreCase));

        Assert.Equal("ForbiddenReference.props", Path.GetFileName(offending.DefiningProject));
    }

    [Fact]
    public void Evaluation_covers_every_configuration_a_reference_could_hide_behind()
    {
        var evaluated = ProjectGraphInspector.EvaluateAllConfigurations(RepoLayout.ProjectFile("FalseGods.Plugin"));

        Assert.Equal(ProjectGraphInspector.AllConfigurations.Count, evaluated.Count);
        Assert.Contains(evaluated, e => e.Configuration == "Debug");
        Assert.Contains(evaluated, e => e.Configuration == "Release");
    }

    [Fact]
    public void A_missing_project_fails_loudly_rather_than_reporting_no_references()
    {
        var missing = RepoLayout.FixtureProjectFile("ThisFixtureDoesNotExist");

        Assert.Throws<FileNotFoundException>(() => ProjectGraphInspector.Evaluate(missing));
    }
}
