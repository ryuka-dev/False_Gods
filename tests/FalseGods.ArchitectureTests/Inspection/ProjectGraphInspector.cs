using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace FalseGods.ArchitectureTests.Inspection;

/// <summary>A project's references as MSBuild actually evaluated them, for one configuration.</summary>
public sealed record EvaluatedReferences(
    string ProjectPath,
    string Configuration,
    IReadOnlyList<EvaluatedProjectReference> ProjectReferences,
    IReadOnlyList<EvaluatedAssemblyReference> AssemblyReferences)
{
    /// <summary>
    /// True if this project pulls in the named assembly by ANY route: a project reference to a project
    /// of that name, an assembly reference of that identity, or a HintPath pointing at that file.
    /// </summary>
    public bool ReferencesAssemblyNamed(string assemblyName)
    {
        return ProjectReferences.Any(p => Eq(p.ProjectName, assemblyName))
            || AssemblyReferences.Any(a => Eq(a.AssemblyName, assemblyName) || Eq(a.HintPathFileName, assemblyName));
    }

    public IEnumerable<string> DescribeReferencesTo(string assemblyName)
    {
        foreach (var p in ProjectReferences.Where(p => Eq(p.ProjectName, assemblyName)))
            yield return $"<ProjectReference Include=\"{p.Identity}\" /> (defined in {Path.GetFileName(p.DefiningProject)})";

        foreach (var a in AssemblyReferences.Where(a => Eq(a.AssemblyName, assemblyName) || Eq(a.HintPathFileName, assemblyName)))
            yield return $"<Reference Include=\"{a.Identity}\" /> (defined in {Path.GetFileName(a.DefiningProject)})";
    }

    private static bool Eq(string? a, string? b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
}

public sealed record EvaluatedProjectReference(string Identity, string ProjectName, string DefiningProject);

public sealed record EvaluatedAssemblyReference(string Identity, string AssemblyName, string? HintPathFileName, string DefiningProject);

/// <summary>
/// Reads a project's references from MSBuild's own evaluation, via <c>dotnet msbuild -getItem:...</c>.
///
/// Why not parse the XML? Because the XML is not the graph. Items arrive from imported files
/// (Directory.Build.props, the SDK), and item conditions depend on evaluated properties. A regex over
/// the csproj sees neither, so it would miss precisely the interesting violations — the ones added
/// through an import or behind a condition. MSBuild's evaluated-item output sees all of them, and it
/// reports the DefiningProjectFullPath so a failure can name where the reference actually came from.
///
/// This also catches a reference that is present but UNUSED, which no amount of assembly-metadata
/// inspection can: an unused reference never reaches the compiled AssemblyRef table.
/// </summary>
public static class ProjectGraphInspector
{
    /// <summary>Configurations worth evaluating: a reference can be added behind a Configuration condition.</summary>
    public static readonly IReadOnlyList<string> AllConfigurations = new[] { "Debug", "Release" };

    private static readonly ConcurrentDictionary<(string Project, string Configuration), EvaluatedReferences> Cache = new();

    public static EvaluatedReferences Evaluate(string projectPath, string configuration = "Debug") =>
        Cache.GetOrAdd((projectPath, configuration), key => EvaluateCore(key.Project, key.Configuration));

    /// <summary>Evaluates every configuration in <see cref="AllConfigurations"/>.</summary>
    public static IReadOnlyList<EvaluatedReferences> EvaluateAllConfigurations(string projectPath) =>
        AllConfigurations.Select(c => Evaluate(projectPath, c)).ToList();

    private static EvaluatedReferences EvaluateCore(string projectPath, string configuration)
    {
        if (!File.Exists(projectPath))
            throw new FileNotFoundException($"Project to evaluate does not exist: {projectPath}", projectPath);

        var json = RunMsBuild(projectPath, configuration);

        using var document = JsonDocument.Parse(json);

        var items = document.RootElement.TryGetProperty("Items", out var i) ? i : default;

        var projectReferences = ReadItems(items, "ProjectReference")
            .Select(e => new EvaluatedProjectReference(
                Identity: Get(e, "Identity"),
                ProjectName: Get(e, "Filename"),
                DefiningProject: Get(e, "DefiningProjectFullPath")))
            .ToList();

        var assemblyReferences = ReadItems(items, "Reference")
            .Select(e =>
            {
                var identity = Get(e, "Identity");
                var hintPath = Get(e, "HintPath");
                return new EvaluatedAssemblyReference(
                    Identity: identity,
                    // A Reference identity may be a full assembly name: "Foo, Version=1.0.0.0, ...".
                    AssemblyName: identity.Split(',')[0].Trim(),
                    HintPathFileName: string.IsNullOrEmpty(hintPath) ? null : Path.GetFileNameWithoutExtension(hintPath),
                    DefiningProject: Get(e, "DefiningProjectFullPath"));
            })
            .ToList();

        return new EvaluatedReferences(projectPath, configuration, projectReferences, assemblyReferences);
    }

    private static IEnumerable<JsonElement> ReadItems(JsonElement items, string itemType)
    {
        if (items.ValueKind != JsonValueKind.Object || !items.TryGetProperty(itemType, out var array))
            yield break;

        if (array.ValueKind != JsonValueKind.Array)
            yield break;

        foreach (var element in array.EnumerateArray())
            yield return element;
    }

    private static string Get(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? string.Empty
            : string.Empty;

    private static string RunMsBuild(string projectPath, string configuration)
    {
        var startInfo = new ProcessStartInfo("dotnet")
        {
            WorkingDirectory = RepoLayout.Root,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };

        startInfo.ArgumentList.Add("msbuild");
        startInfo.ArgumentList.Add(projectPath);
        startInfo.ArgumentList.Add("-getItem:ProjectReference");
        startInfo.ArgumentList.Add("-getItem:Reference");
        startInfo.ArgumentList.Add($"-p:Configuration={configuration}");
        startInfo.ArgumentList.Add("-nologo");
        // Keep the checks usable in constrained/sandboxed environments.
        startInfo.ArgumentList.Add("--disable-build-servers");

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Failed to start 'dotnet msbuild'.");

        var stdout = process.StandardOutput.ReadToEnd();
        var stderr = process.StandardError.ReadToEnd();

        if (!process.WaitForExit(milliseconds: 120_000))
        {
            process.Kill(entireProcessTree: true);
            throw new TimeoutException($"'dotnet msbuild -getItem' timed out evaluating {projectPath}.");
        }

        // Exit code, never text matching: an evaluation that failed says so by failing.
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"'dotnet msbuild -getItem' failed with exit code {process.ExitCode} for {projectPath} ({configuration}).{Environment.NewLine}" +
                $"stdout:{Environment.NewLine}{stdout}{Environment.NewLine}stderr:{Environment.NewLine}{stderr}");
        }

        return stdout;
    }
}
