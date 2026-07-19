using System;
using System.IO;
using System.Linq;
using FalseGods.ArchitectureTests.Inspection;
using FalseGods.ArchitectureTests.Rules;
using Xunit;

namespace FalseGods.ArchitectureTests.Checks;

/// <summary>
/// FG-ARCH-011 — no field signature in the ST adapter binds to the SULFUR Together assembly.
///
/// The adapter references ST legitimately (it is the one project allowed to), and method bodies/signatures
/// naming ST types are fine — the JIT resolves them lazily, only on the guarded call paths. A FIELD is
/// different: the CLR resolves field types at type-load, so on a machine whose installed ST lacks the bridge
/// surface, one ST-typed field makes Assembly.GetTypes() throw for the whole adapter — and the BepInEx
/// ecosystem's scanners (XNode's NodeDataCache, EasySettings' attribute scan, ...) call GetTypes() over every
/// loaded assembly, taking unrelated plugins down with it. Hit twice in this project (2026-07-14) before this
/// tripwire existed; the traps are the fields the COMPILER generates — an ST-typed local hoisted into an
/// iterator/async state machine, or a lambda/method-group cache — which no code review reliably sees.
/// </summary>
public sealed class StAdapterFieldSignaturesChecks
{
    private const string Adapter = "FalseGods.Integration.SulfurTogether";

    // The real assembly identity, WITH the space — the namespace spelling "SULFURTogether" names no assembly
    // (the FG-ARCH-005 section documents the same trap).
    private const string StAssembly = "SULFUR Together";

    private const string RuleId = "FG-ARCH-011";

    private static string Failure(string detail) =>
        $"{RuleId}: {detail}{Environment.NewLine}" +
        $"Hold every ST value in a regular method body or a plain-typed snapshot; store the channel " +
        $"registration as IDisposable; keep coroutines/lambdas/LINQ on plain types only." +
        $"{Environment.NewLine}See {ArchitectureRuleRegistry.DocLinkFor(RuleId)}";

    // Reads the compiled adapter DLL — an OUTER assembly that needs the game + BepInEx + ST DLLs to build.
    // CI has none of them, so this runs locally and at L3 only (scripts/verify.ps1 -CiSafe filters it out),
    // the same split as FG-ARCH-002's metadata layer.
    [Fact]
    [Trait("Requires", "BuiltOuterAssemblies")]
    [ArchitectureRule(RuleId)]
    public void Adapter_field_signatures_never_bind_to_sulfur_together()
    {
        var configuration = VerificationContext.Configuration;
        var assemblyPath = RepoLayout.FindBuiltAssembly(Adapter, configuration);

        // No fallback to another configuration: inspecting a stale DLL from a build nobody just ran is worse
        // than reporting that the expected one is missing.
        Assert.True(assemblyPath is not null,
            $"{RuleId}: {Adapter}.dll was not found for configuration '{configuration}'." +
            $"{Environment.NewLine}  expected: {RepoLayout.ExpectedAssemblyPath(Adapter, configuration)}" +
            $"{Environment.NewLine}  configuration came from: {VerificationContext.ConfigurationSource}" +
            $"{Environment.NewLine}Build that configuration first — scripts/verify.ps1 -Configuration {configuration} does it for you." +
            $"{Environment.NewLine}See {ArchitectureRuleRegistry.DocLinkFor(RuleId)}");

        var offenders = FieldSignatureInspector.DescribeFieldsBindingToAssembly(assemblyPath!, StAssembly);

        Assert.True(offenders.Count == 0, Failure(
            $"these fields in {Adapter}.dll bind to the '{StAssembly}' assembly, so the adapter cannot load " +
            $"when the installed ST lacks that surface (compiler-generated names mean a hoisted iterator/async " +
            $"local or a lambda/method-group cache):{Environment.NewLine}  " +
            string.Join(Environment.NewLine + "  ", offenders) +
            $"{Environment.NewLine}  configuration: {configuration} (from {VerificationContext.ConfigurationSource})" +
            $"{Environment.NewLine}  assembly:      {assemblyPath}"));
    }

    // The scanner must be provably capable of finding a cross-assembly field binding, or a green run above
    // proves nothing. FalseGods.Application is an INNER assembly (built everywhere, including CI) whose
    // replication types hold RuntimeContracts-typed fields by design — if the scanner cannot see those, it is
    // broken, and this fails before the vacuous green does damage.
    [Fact]
    [ArchitectureRule(RuleId)]
    public void The_scanner_detects_cross_assembly_field_bindings_or_this_check_is_vacuous()
    {
        var configuration = VerificationContext.Configuration;
        var application = RepoLayout.FindBuiltAssembly("FalseGods.Application", configuration);

        Assert.True(application is not null,
            $"{RuleId}: FalseGods.Application.dll not built for '{configuration}' — cannot self-test the scanner. " +
            $"Expected {RepoLayout.ExpectedAssemblyPath("FalseGods.Application", configuration)}.");

        var found = FieldSignatureInspector.DescribeFieldsBindingToAssembly(application!, "FalseGods.RuntimeContracts");

        Assert.True(found.Count > 0, Failure(
            "the field-signature scanner found no RuntimeContracts-typed field in FalseGods.Application.dll, " +
            "which is known to hold several (ReplicationSender._channel among them). The scanner is broken, " +
            "and the adapter check above would pass vacuously."));
    }

    [Fact]
    [ArchitectureRule(RuleId)]
    public void The_adapter_project_exists_so_this_check_is_not_vacuous()
    {
        Assert.True(File.Exists(RepoLayout.ProjectFile(Adapter)),
            $"{RuleId}: the project {Adapter}.csproj no longer exists, so this check scans nothing. " +
            $"Update the rule, or the constant. See {ArchitectureRuleRegistry.DocLinkFor(RuleId)}");
    }
}
