using System;
using System.IO;
using FalseGods.ArchitectureTests.Inspection;
using Xunit;

namespace FalseGods.ArchitectureTests.SelfTests;

/// <summary>Proves the AssemblyRef layer of FG-ARCH-002 detects and does not over-detect.</summary>
public sealed class AssemblyReferenceInspectorSelfTests
{
    private const string ForbiddenAssembly = "FalseGods.Integration.SulfurTogether";

    private static byte[] FakeStAdapter() => SyntheticAssembly.Compile(
        ForbiddenAssembly,
        """
        namespace FalseGods.Integration.SulfurTogether
        {
            public sealed class StIntegration { }
        }
        """);

    [Fact]
    public void Detects_a_forbidden_assembly_reference()
    {
        var plugin = SyntheticAssembly.Compile(
            "SyntheticPlugin",
            """
            namespace SyntheticPlugin
            {
                public sealed class CompositionRoot
                {
                    // A field type is enough: it puts the adapter in the AssemblyRef table, and the CLR
                    // will resolve it at type-load whether or not anyone reads the field.
                    public FalseGods.Integration.SulfurTogether.StIntegration? Adapter;
                }
            }
            """,
            FakeStAdapter());

        using var image = new MemoryStream(plugin);
        var referenced = AssemblyReferenceInspector.ReadReferencedAssemblyNames(image);

        Assert.Contains(ForbiddenAssembly, referenced, StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public void Does_not_report_a_clean_assembly()
    {
        // Compiled WITH the adapter available as a reference, but never using it — the compiler omits
        // the AssemblyRef. That is the real behaviour this layer relies on, and it is precisely why an
        // unused reference needs the project-graph layer instead.
        var plugin = SyntheticAssembly.Compile(
            "SyntheticCleanPlugin",
            """
            namespace SyntheticCleanPlugin
            {
                public sealed class CompositionRoot
                {
                    public string Name => "clean";
                }
            }
            """,
            FakeStAdapter());

        using var image = new MemoryStream(plugin);
        var referenced = AssemblyReferenceInspector.ReadReferencedAssemblyNames(image);

        Assert.DoesNotContain(ForbiddenAssembly, referenced, StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public void Rejects_a_file_that_is_not_a_managed_assembly()
    {
        using var garbage = new MemoryStream(new byte[] { 0x4D, 0x5A, 0x00, 0x00 });

        Assert.ThrowsAny<Exception>(() => AssemblyReferenceInspector.ReadReferencedAssemblyNames(garbage));
    }
}
