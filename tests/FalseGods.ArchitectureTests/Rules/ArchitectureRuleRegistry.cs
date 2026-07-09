using System.Collections.Generic;

namespace FalseGods.ArchitectureTests.Rules;

/// <summary>
/// The state of a rule's AUTOMATED CHECK. Deliberately not the same thing as whether the compiler
/// happens to make the violation uncompilable today — see <see cref="ArchitectureRule"/>.
/// </summary>
public enum CheckStatus
{
    /// <summary>Agreed, but no check exists.</summary>
    Planned,

    /// <summary>A check exists and fails when explicitly run. Not yet mandatory on every PR.</summary>
    Implemented,

    /// <summary>The check is mandatory on every pull request.</summary>
    RequiredInCi,
}

/// <summary>How much protection the project reference graph gives today, independent of any check.</summary>
public enum CompilerProtection
{
    /// <summary>The violation compiles fine.</summary>
    None,

    /// <summary>Some forms of the violation are compile errors; others are not.</summary>
    Partial,

    /// <summary>Using the forbidden type is a compile error, because the reference is absent.</summary>
    Full,
}

public sealed record ArchitectureRule(string Id, string Title, CheckStatus CheckStatus, CompilerProtection CompilerProtection);

/// <summary>
/// Mirrors the rule registry in <c>Docs/ArchitectureEnforcement.md §5</c>.
///
/// <see cref="Checks.RuleRegistryChecks"/> asserts this list and the document agree, so the two cannot
/// drift apart silently. The document is the authority; this is a machine-readable projection of it.
/// </summary>
public static class ArchitectureRuleRegistry
{
    public const string DocumentPath = "Docs/ArchitectureEnforcement.md";

    public static IReadOnlyList<ArchitectureRule> All { get; } = new[]
    {
        new ArchitectureRule("FG-ARCH-001", "Core references no outer technology",
            CheckStatus.Planned, CompilerProtection.Full),

        new ArchitectureRule("FG-ARCH-002", "The base plugin does not reference the ST adapter",
            CheckStatus.Implemented, CompilerProtection.Full),

        new ArchitectureRule("FG-ARCH-003", "UnityRuntime does not reference Protocol",
            CheckStatus.Planned, CompilerProtection.Full),

        new ArchitectureRule("FG-ARCH-004", "Presentation's public API accepts no wire DTO",
            CheckStatus.Planned, CompilerProtection.Partial),

        new ArchitectureRule("FG-ARCH-005", "ST types and broker access stay at their seams",
            CheckStatus.Planned, CompilerProtection.Partial),

        new ArchitectureRule("FG-ARCH-006", "Harmony patches live only in Integration.Sulfur",
            CheckStatus.Planned, CompilerProtection.Full),

        new ArchitectureRule("FG-ARCH-007", "RuntimeContracts stays dependency-light",
            CheckStatus.Planned, CompilerProtection.Full),

        new ArchitectureRule("FG-ARCH-008", "Boss and Arena wire state/events stay separate",
            CheckStatus.Planned, CompilerProtection.None),

        new ArchitectureRule("FG-ARCH-009", "The base plugin loads with the optional adapter absent",
            CheckStatus.Planned, CompilerProtection.Partial),

        new ArchitectureRule("FG-ARCH-010", "Every enforced architecture test cites a valid rule id",
            CheckStatus.Implemented, CompilerProtection.None),
    };

    /// <summary>The documentation anchor a failure message should point a developer at.</summary>
    public static string DocLinkFor(string ruleId) =>
        $"{DocumentPath}#{ruleId.ToLowerInvariant()}";
}
