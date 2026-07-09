using System;
using System.Collections.Generic;
using System.Linq;

namespace FalseGods.ArchitectureTests.Rules;

/// <summary>
/// The state of ONE automated check layer. Deliberately not the same thing as whether the compiler happens
/// to make the violation uncompilable today — see <see cref="CompilerProtection"/>.
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

/// <summary>
/// One way a rule is checked, and how far that particular way has got.
///
/// A rule is not a single check. FG-ARCH-002 is verified at two layers — the evaluated project graph and
/// the compiled AssemblyRef table — and they are blind to different violations, run in different places,
/// and are promoted at different times: the project graph needs no game and runs in CI; the metadata layer
/// reads an outer DLL that CI cannot build.
///
/// Collapsing that into one status per rule forces a choice between two lies. Call FG-ARCH-002
/// "Required in CI" and you claim CI catches a typeof() it cannot see; call it "Implemented" and you hide
/// that a red CI already blocks the merge. Layers exist so neither sentence has to be written down.
///
/// The same applies to a rule whose check covers only part of its text: FG-ARCH-006's reference layer is a
/// required merge gate, while the [HarmonyPatch] attribute scan its rule also demands does not exist. The
/// registry says exactly that, and <see cref="Checks.RuleRegistryChecks"/> holds the document to it.
/// </summary>
/// <param name="Name">
/// Stable, human-readable, and repeated verbatim in the document's layer-status table. Layers that use the
/// same technique share a name across rules ("project graph", "assembly metadata"), because they fail for
/// the same reasons and get promoted for the same reasons.
/// </param>
public sealed record RuleCheckLayer(string Name, CheckStatus Status);

public sealed record ArchitectureRule(
    string Id,
    string Title,
    IReadOnlyList<RuleCheckLayer> Layers,
    CompilerProtection CompilerProtection)
{
    /// <summary>
    /// True when at least one layer actually runs. This — not "the rule is done" — is what obliges the rule
    /// to be cited by a check (FG-ARCH-010).
    /// </summary>
    public bool IsEnforcedByAnyLayer =>
        Layers.Any(layer => layer.Status is CheckStatus.Implemented or CheckStatus.RequiredInCi);
}

/// <summary>
/// Mirrors the rule registry in <c>Docs/ArchitectureEnforcement.md §5</c>, layer by layer.
///
/// <see cref="Checks.RuleRegistryChecks"/> asserts this list and the document agree — both the set of rule
/// ids and every layer's status — so the two cannot drift apart silently. The document is the authority;
/// this is a machine-readable projection of it.
/// </summary>
public static class ArchitectureRuleRegistry
{
    public const string DocumentPath = "Docs/ArchitectureEnforcement.md";

    // Layer names. Constants rather than literals: the document's table must repeat them exactly, and a
    // typo should be a compile error here rather than a puzzling mismatch there.
    public const string ProjectGraph = "project graph";
    public const string AssemblyMetadata = "assembly metadata";
    public const string PublicApiSignatures = "public API signatures";
    public const string BrokerAccess = "broker access";
    public const string PatchAttributeScan = "patch attribute scan";
    public const string CompositionRootBehaviour = "composition root behaviour";
    public const string RegistryConsistency = "registry consistency";

    public static IReadOnlyList<ArchitectureRule> All { get; } = new[]
    {
        new ArchitectureRule("FG-ARCH-001", "Core references no outer technology",
            new[] { new RuleCheckLayer(AssemblyMetadata, CheckStatus.Planned) },
            CompilerProtection.Full),

        new ArchitectureRule("FG-ARCH-002", "The base plugin does not reference the ST adapter",
            new[]
            {
                new RuleCheckLayer(ProjectGraph, CheckStatus.RequiredInCi),
                // Reads FalseGods.Plugin.dll, which CI cannot build. Local + L3 only.
                new RuleCheckLayer(AssemblyMetadata, CheckStatus.Implemented),
            },
            CompilerProtection.Full),

        new ArchitectureRule("FG-ARCH-003", "UnityRuntime does not reference Protocol",
            new[]
            {
                new RuleCheckLayer(ProjectGraph, CheckStatus.RequiredInCi),
                // Would catch a Protocol type reached transitively. Needs FalseGods.UnityRuntime.dll.
                new RuleCheckLayer(AssemblyMetadata, CheckStatus.Planned),
            },
            CompilerProtection.Full),

        new ArchitectureRule("FG-ARCH-004", "Presentation's public API accepts no wire DTO",
            new[] { new RuleCheckLayer(PublicApiSignatures, CheckStatus.Planned) },
            CompilerProtection.Partial),

        new ArchitectureRule("FG-ARCH-005", "ST types and broker access stay at their seams",
            new[]
            {
                new RuleCheckLayer(ProjectGraph, CheckStatus.RequiredInCi),
                new RuleCheckLayer(AssemblyMetadata, CheckStatus.Planned),
                // The rule's second half. The broker type does not exist yet.
                new RuleCheckLayer(BrokerAccess, CheckStatus.Planned),
            },
            CompilerProtection.Partial),

        new ArchitectureRule("FG-ARCH-006", "Harmony patches live only in Integration.Sulfur",
            new[]
            {
                new RuleCheckLayer(ProjectGraph, CheckStatus.RequiredInCi),
                new RuleCheckLayer(AssemblyMetadata, CheckStatus.Planned),
                // "No type outside Integration.Sulfur carries [HarmonyPatch]" — a different claim from
                // "no other project references 0Harmony", and not checked by the reference layer.
                new RuleCheckLayer(PatchAttributeScan, CheckStatus.Planned),
            },
            CompilerProtection.Full),

        new ArchitectureRule("FG-ARCH-007", "RuntimeContracts stays dependency-light",
            new[] { new RuleCheckLayer(AssemblyMetadata, CheckStatus.Planned) },
            CompilerProtection.Full),

        new ArchitectureRule("FG-ARCH-008", "Boss and Arena wire state/events stay separate",
            new[] { new RuleCheckLayer(PublicApiSignatures, CheckStatus.Planned) },
            CompilerProtection.None),

        new ArchitectureRule("FG-ARCH-009", "The base plugin loads with the optional adapter absent",
            new[] { new RuleCheckLayer(CompositionRootBehaviour, CheckStatus.Planned) },
            CompilerProtection.Partial),

        new ArchitectureRule("FG-ARCH-010", "Every enforced architecture test cites a valid rule id",
            new[] { new RuleCheckLayer(RegistryConsistency, CheckStatus.RequiredInCi) },
            CompilerProtection.None),
    };

    /// <summary>The documentation anchor a failure message should point a developer at.</summary>
    public static string DocLinkFor(string ruleId) =>
        $"{DocumentPath}#{ruleId.ToLowerInvariant()}";

    /// <summary>The exact words the document's layer-status table uses for a status.</summary>
    public static string Display(CheckStatus status) => status switch
    {
        CheckStatus.Planned => "Planned",
        CheckStatus.Implemented => "Implemented",
        CheckStatus.RequiredInCi => "Required in CI",
        _ => throw new ArgumentOutOfRangeException(nameof(status), status, "Unknown check status."),
    };
}
