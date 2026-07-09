using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace FalseGods.ArchitectureTests.Inspection;

/// <summary>Thrown when a configuration is requested that the project does not declare.</summary>
public sealed class UnknownConfigurationException : Exception
{
    public UnknownConfigurationException(string projectPath, string configuration, IReadOnlyList<string> declared)
        : base($"Configuration '{configuration}' is not declared by {Path.GetFileName(projectPath)}. " +
               $"Declared configurations: {string.Join(", ", declared)}. " +
               $"An undeclared configuration must be rejected, not evaluated — MSBuild happily evaluates one, " +
               $"and every conditional reference guarded by a real configuration would then go unchecked. " +
               $"Declare it in Directory.Build.props <Configurations> if it is genuinely supported.")
    {
        ProjectPath = projectPath;
        Configuration = configuration;
        DeclaredConfigurations = declared;
    }

    public string ProjectPath { get; }
    public string Configuration { get; }
    public IReadOnlyList<string> DeclaredConfigurations { get; }
}

/// <summary>A project's references as MSBuild actually evaluated them, for one configuration.</summary>
public sealed record EvaluatedReferences(
    string ProjectPath,
    string Configuration,
    IReadOnlyList<string> DeclaredConfigurations,
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
/// Reads a project's references from MSBuild's own evaluation, via <c>dotnet msbuild -getProperty/-getItem</c>.
///
/// Why not parse the XML? Because the XML is not the graph. Items arrive from imported files
/// (Directory.Build.props, the SDK), and item conditions depend on evaluated properties. A regex over
/// the csproj sees neither, so it would miss precisely the interesting violations — the ones added
/// through an import or behind a condition. MSBuild's evaluated-item output sees all of them, and it
/// reports the DefiningProjectFullPath so a failure can name where the reference actually came from.
///
/// This also catches a reference that is present but UNUSED, which no amount of assembly-metadata
/// inspection can: an unused reference never reaches the compiled AssemblyRef table.
///
/// The set of configurations is read from the project's evaluated <c>Configurations</c> property, not
/// hardcoded. A reference hidden behind <c>Condition="'$(Configuration)' == 'Release'"</c> is only found
/// if Release is actually evaluated, and a hardcoded list silently stops covering a configuration the
/// day someone adds one.
/// </summary>
public static class ProjectGraphInspector
{
    private static readonly TimeSpan EvaluationTimeout = TimeSpan.FromSeconds(120);

    private static readonly ConcurrentDictionary<(string Project, string Configuration), EvaluatedReferences> Cache = new();
    private static readonly ConcurrentDictionary<string, IReadOnlyList<string>> DeclaredConfigurationsCache = new();

    /// <summary>The configurations the project declares, from MSBuild evaluation of <c>$(Configurations)</c>.</summary>
    public static IReadOnlyList<string> GetDeclaredConfigurations(string projectPath) =>
        DeclaredConfigurationsCache.GetOrAdd(projectPath, path => EvaluateCore(path, configuration: null).DeclaredConfigurations);

    /// <summary>Evaluates one configuration. Throws <see cref="UnknownConfigurationException"/> for an undeclared one.</summary>
    public static EvaluatedReferences Evaluate(string projectPath, string configuration)
    {
        if (!File.Exists(projectPath))
            throw new FileNotFoundException($"Project to evaluate does not exist: {projectPath}", projectPath);

        var declared = GetDeclaredConfigurations(projectPath);

        if (!declared.Contains(configuration, StringComparer.OrdinalIgnoreCase))
            throw new UnknownConfigurationException(projectPath, configuration, declared);

        return Cache.GetOrAdd((projectPath, configuration), key => EvaluateCore(key.Project, key.Configuration));
    }

    /// <summary>Evaluates every configuration the project declares.</summary>
    public static IReadOnlyList<EvaluatedReferences> EvaluateAllConfigurations(string projectPath)
    {
        if (!File.Exists(projectPath))
            throw new FileNotFoundException($"Project to evaluate does not exist: {projectPath}", projectPath);

        return GetDeclaredConfigurations(projectPath)
            .Select(configuration => Evaluate(projectPath, configuration))
            .ToList();
    }

    private static EvaluatedReferences EvaluateCore(string projectPath, string? configuration)
    {
        var json = RunMsBuild(projectPath, configuration);

        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;

        var declared = ReadDeclaredConfigurations(root);
        var items = root.TryGetProperty("Items", out var i) ? i : default;

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

        return new EvaluatedReferences(
            projectPath,
            configuration ?? "(msbuild default)",
            declared,
            projectReferences,
            assemblyReferences);
    }

    private static IReadOnlyList<string> ReadDeclaredConfigurations(JsonElement root)
    {
        if (!root.TryGetProperty("Properties", out var properties) ||
            !properties.TryGetProperty("Configurations", out var value) ||
            value.ValueKind != JsonValueKind.String)
        {
            return Array.Empty<string>();
        }

        return (value.GetString() ?? string.Empty)
            .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToList();
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

    private static string RunMsBuild(string projectPath, string? configuration)
    {
        var arguments = new List<string>
        {
            "msbuild",
            projectPath,
            // Requesting a property alongside items makes the output JSON, and gets the declared
            // configurations in the same evaluation rather than a second process.
            "-getProperty:Configurations",
            "-getItem:ProjectReference",
            "-getItem:Reference",
            "-nologo",
            // Keep the checks usable in constrained/sandboxed environments.
            "--disable-build-servers",
        };

        if (configuration is not null)
            arguments.Add($"-p:Configuration={configuration}");

        var result = ProcessRunner.Run("dotnet", arguments, RepoLayout.Root, EvaluationTimeout);

        var describedConfiguration = configuration ?? "(msbuild default)";

        if (result.TimedOut)
        {
            throw new TimeoutException(
                $"'dotnet msbuild -getItem' timed out after {EvaluationTimeout.TotalSeconds:0}s " +
                $"evaluating {projectPath} ({describedConfiguration}).{Environment.NewLine}" +
                Describe(result));
        }

        // Exit code, never text matching: an evaluation that failed says so by failing.
        if (result.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"'dotnet msbuild -getItem' failed with exit code {result.ExitCode} " +
                $"for {projectPath} ({describedConfiguration}).{Environment.NewLine}" +
                Describe(result));
        }

        return result.StandardOutput;
    }

    private static string Describe(ProcessResult result) =>
        $"stdout:{Environment.NewLine}{result.StandardOutput}{Environment.NewLine}" +
        $"stderr:{Environment.NewLine}{result.StandardError}";
}
