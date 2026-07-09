using System;
using System.IO;

namespace FalseGods.ArchitectureTests;

/// <summary>Locates the repository and the artefacts the checks inspect.</summary>
public static class RepoLayout
{
    private const string SolutionFileName = "False Gods.slnx";

    public static string Root { get; } = FindRoot();

    public static string ProjectFile(string projectName) =>
        Path.Combine(Root, "src", projectName, projectName + ".csproj");

    public static string FixtureProjectFile(string fixtureName) =>
        Path.Combine(Root, "tests", "Fixtures", fixtureName, fixtureName + ".csproj");

    public static string Doc(string relativePath) =>
        Path.Combine(Root, relativePath.Replace('/', Path.DirectorySeparatorChar));

    /// <summary>Where a production project's assembly is expected for the given configuration.</summary>
    public static string ExpectedAssemblyPath(string projectName, string configuration) =>
        Inspection.BuildOutputLocator.AssemblyPath(Root, projectName, configuration);

    /// <summary>
    /// The built assembly for a production project in EXACTLY the given configuration, or null.
    /// Never falls back to another configuration — see <see cref="Inspection.BuildOutputLocator"/>.
    /// </summary>
    public static string? FindBuiltAssembly(string projectName, string configuration) =>
        Inspection.BuildOutputLocator.FindBuiltAssembly(Root, projectName, configuration);

    private static string FindRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);

        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, SolutionFileName)))
                return dir.FullName;

            dir = dir.Parent;
        }

        throw new InvalidOperationException(
            $"Could not locate the repository root: no '{SolutionFileName}' found above {AppContext.BaseDirectory}.");
    }
}
