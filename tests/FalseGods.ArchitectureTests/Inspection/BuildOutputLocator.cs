using System.IO;

namespace FalseGods.ArchitectureTests.Inspection;

/// <summary>
/// Locates a production project's build output for ONE named configuration.
///
/// There is deliberately no fallback between configurations. An earlier version searched Debug and then
/// Release, which meant `verify.ps1 -Configuration Release` would build Release and then happily inspect a
/// stale Debug DLL left over from last week. The check reported on an artefact nobody had just built — the
/// worst kind of green.
///
/// Pure functions over an explicit root, so they can be tested against a synthetic directory layout.
/// </summary>
public static class BuildOutputLocator
{
    /// <summary>Where the assembly is expected, whether or not it is there.</summary>
    public static string AssemblyPath(string repoRoot, string projectName, string configuration) =>
        Path.Combine(repoRoot, "src", projectName, "bin", configuration, projectName + ".dll");

    /// <summary>
    /// The built assembly for exactly this configuration, or null if it has not been built.
    ///
    /// Null rather than an exception: the caller decides whether "not built" is a failure, and says so with
    /// a message naming the configuration and the full path it looked at, instead of an opaque FileNotFound.
    /// </summary>
    public static string? FindBuiltAssembly(string repoRoot, string projectName, string configuration)
    {
        var path = AssemblyPath(repoRoot, projectName, configuration);
        return File.Exists(path) ? path : null;
    }
}
