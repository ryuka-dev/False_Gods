using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;

namespace FalseGods.ArchitectureTests.Inspection;

/// <summary>
/// Reads the AssemblyRef table of a compiled assembly.
///
/// This is the half of the check that an unused <c>&lt;Reference&gt;</c> cannot trigger and a used type
/// cannot escape. A type in a method signature, a <c>typeof()</c>, a base class, a field, or a touch on a
/// static-initialization path all leave an AssemblyRef behind, and all of them make the CLR resolve the
/// assembly at type-load — which is the failure this whole rule exists to prevent.
///
/// Metadata is read, not loaded: the production assemblies target net472 and are never executed here.
/// </summary>
public static class AssemblyReferenceInspector
{
    public static IReadOnlyList<string> ReadReferencedAssemblyNames(string assemblyPath)
    {
        using var stream = File.OpenRead(assemblyPath);
        return ReadReferencedAssemblyNames(stream);
    }

    public static IReadOnlyList<string> ReadReferencedAssemblyNames(Stream peStream)
    {
        using var peReader = new PEReader(peStream);

        if (!peReader.HasMetadata)
            throw new BadImageFormatException("The file contains no CLI metadata; it is not a managed assembly.");

        var metadata = peReader.GetMetadataReader();

        return metadata.AssemblyReferences
            .Select(handle => metadata.GetString(metadata.GetAssemblyReference(handle).Name))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public static bool ReferencesAssemblyNamed(string assemblyPath, string assemblyName) =>
        ReadReferencedAssemblyNames(assemblyPath)
            .Any(name => string.Equals(name, assemblyName, StringComparison.OrdinalIgnoreCase));
}
