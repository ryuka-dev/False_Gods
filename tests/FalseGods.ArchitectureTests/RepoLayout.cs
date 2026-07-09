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

    /// <summary>
    /// The built assembly for a production project, in whichever configuration exists.
    ///
    /// Returns null rather than throwing: the caller decides whether "not built" is a failure, and
    /// says so with a message that tells you to build first, instead of an opaque FileNotFound.
    /// </summary>
    public static string? FindBuiltAssembly(string projectName)
    {
        foreach (var configuration in new[] { "Debug", "Release" })
        {
            var candidate = Path.Combine(Root, "src", projectName, "bin", configuration, projectName + ".dll");
            if (File.Exists(candidate))
                return candidate;
        }

        return null;
    }

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
