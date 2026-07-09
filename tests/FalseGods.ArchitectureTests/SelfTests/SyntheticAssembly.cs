using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace FalseGods.ArchitectureTests.SelfTests;

/// <summary>
/// Compiles assemblies in memory, so the AssemblyRef checker can be tested against a known-bad binary.
///
/// The alternative — temporarily adding a forbidden reference to the real FalseGods.Plugin, building,
/// and looking at the output — proves the checker works exactly once, on one machine, and leaves the
/// production project one forgotten `git checkout` away from committing the violation it was testing.
/// Nothing here touches disk.
/// </summary>
internal static class SyntheticAssembly
{
    /// <summary>
    /// Emits a library with the given assembly name and source, referencing <paramref name="references"/>.
    /// Returns the raw PE image. Byte arrays rather than streams, because
    /// <c>MetadataReference.CreateFromStream</c> takes ownership of the stream it is handed.
    /// </summary>
    public static byte[] Compile(string assemblyName, string source, params byte[][] references)
    {
        var metadataReferences = new List<MetadataReference>(RuntimeReferences());

        foreach (var reference in references)
            metadataReferences.Add(MetadataReference.CreateFromImage(reference));

        var compilation = CSharpCompilation.Create(
            assemblyName,
            new[] { CSharpSyntaxTree.ParseText(source) },
            metadataReferences,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        using var output = new MemoryStream();
        var result = compilation.Emit(output);

        if (!result.Success)
        {
            var errors = result.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error);
            throw new InvalidOperationException(
                $"Synthetic assembly '{assemblyName}' failed to compile:{Environment.NewLine}" +
                string.Join(Environment.NewLine, errors));
        }

        return output.ToArray();
    }

    private static IEnumerable<MetadataReference> RuntimeReferences()
    {
        var trusted = (AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES") as string ?? string.Empty)
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries);

        // The minimum needed to compile a class with a field: corlib plus the System.Runtime facade.
        var needed = new[] { "System.Private.CoreLib.dll", "System.Runtime.dll" };

        return trusted
            .Where(path => needed.Contains(Path.GetFileName(path), StringComparer.OrdinalIgnoreCase))
            .Select(path => (MetadataReference)MetadataReference.CreateFromFile(path));
    }
}
