using System;

namespace FalseGods.ArchitectureTests;

/// <summary>
/// Which build configuration this verification run is about.
///
/// <c>scripts/verify.ps1</c> sets <see cref="ConfigurationVariable"/> before invoking the tests, so a check
/// that reads a compiled assembly reads the one that was just built, and never a stale artefact from another
/// configuration.
///
/// When the variable is absent — a bare `dotnet test`, or an IDE test run — the configuration falls back to
/// <see cref="DefaultConfiguration"/>. That default is documented rather than silent: any check that then
/// fails to find its assembly must say which configuration it expected and why.
/// </summary>
public static class VerificationContext
{
    public const string ConfigurationVariable = "FALSEGODS_VERIFY_CONFIGURATION";

    /// <summary>Used when <see cref="ConfigurationVariable"/> is not set. Matches the default of verify.ps1.</summary>
    public const string DefaultConfiguration = "Debug";

    /// <summary>True when the configuration came from the environment rather than the documented default.</summary>
    public static bool ConfigurationWasProvided { get; } =
        !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(ConfigurationVariable));

    public static string Configuration { get; } =
        ConfigurationWasProvided
            ? Environment.GetEnvironmentVariable(ConfigurationVariable)!.Trim()
            : DefaultConfiguration;

    /// <summary>Explains where <see cref="Configuration"/> came from, for inclusion in a failure message.</summary>
    public static string ConfigurationSource =>
        ConfigurationWasProvided
            ? $"environment variable {ConfigurationVariable}"
            : $"documented default (\"{DefaultConfiguration}\"), because {ConfigurationVariable} is not set";
}
