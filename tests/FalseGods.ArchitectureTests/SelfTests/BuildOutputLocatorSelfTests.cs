using System;
using System.IO;
using FalseGods.ArchitectureTests.Inspection;
using Xunit;

namespace FalseGods.ArchitectureTests.SelfTests;

/// <summary>
/// Proves the metadata check reads the configuration it was asked for, and never another.
///
/// The bug this guards: `verify.ps1 -Configuration Release` builds Release, then a Debug-first lookup finds
/// a stale Debug DLL from a previous run and inspects that instead. The check passes, reporting on an
/// artefact nobody just built.
/// </summary>
public sealed class BuildOutputLocatorSelfTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "falsegods-locator-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    private void PlaceAssembly(string projectName, string configuration)
    {
        var path = BuildOutputLocator.AssemblyPath(_root, projectName, configuration);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "not a real assembly; only its presence matters here");
    }

    [Fact]
    public void Verifying_Release_does_not_fall_back_to_an_existing_Debug_assembly()
    {
        PlaceAssembly("FalseGods.Plugin", "Debug");

        Assert.Null(BuildOutputLocator.FindBuiltAssembly(_root, "FalseGods.Plugin", "Release"));
    }

    [Fact]
    public void Verifying_Debug_does_not_fall_back_to_an_existing_Release_assembly()
    {
        PlaceAssembly("FalseGods.Plugin", "Release");

        Assert.Null(BuildOutputLocator.FindBuiltAssembly(_root, "FalseGods.Plugin", "Debug"));
    }

    [Fact]
    public void Each_configuration_is_found_independently_when_both_exist()
    {
        PlaceAssembly("FalseGods.Plugin", "Debug");
        PlaceAssembly("FalseGods.Plugin", "Release");

        var debug = BuildOutputLocator.FindBuiltAssembly(_root, "FalseGods.Plugin", "Debug");
        var release = BuildOutputLocator.FindBuiltAssembly(_root, "FalseGods.Plugin", "Release");

        Assert.NotNull(debug);
        Assert.NotNull(release);
        Assert.Contains(Path.DirectorySeparatorChar + "Debug" + Path.DirectorySeparatorChar, debug!, StringComparison.Ordinal);
        Assert.Contains(Path.DirectorySeparatorChar + "Release" + Path.DirectorySeparatorChar, release!, StringComparison.Ordinal);
        Assert.NotEqual(debug, release);
    }

    [Fact]
    public void A_missing_assembly_for_the_expected_configuration_reports_null_and_the_expected_path()
    {
        Assert.Null(BuildOutputLocator.FindBuiltAssembly(_root, "FalseGods.Plugin", "Debug"));

        // The caller has the full path to put in its failure message.
        var expected = BuildOutputLocator.AssemblyPath(_root, "FalseGods.Plugin", "Debug");
        Assert.EndsWith(Path.Combine("bin", "Debug", "FalseGods.Plugin.dll"), expected, StringComparison.Ordinal);
    }

    [Fact]
    public void The_verification_context_reports_where_its_configuration_came_from()
    {
        // Whatever the ambient value, the source is always explainable — a failure message must never
        // leave a developer guessing which configuration was inspected.
        Assert.False(string.IsNullOrWhiteSpace(VerificationContext.Configuration));
        Assert.False(string.IsNullOrWhiteSpace(VerificationContext.ConfigurationSource));

        if (!VerificationContext.ConfigurationWasProvided)
            Assert.Equal(VerificationContext.DefaultConfiguration, VerificationContext.Configuration);
    }
}
