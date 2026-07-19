using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;

namespace FalseGods.ArchitectureTests.Inspection;

/// <summary>
/// Reads every FieldDef signature in a compiled assembly and reports the fields whose type binds to a given
/// AssemblyRef — including the fields the compiler generates (iterator/async state-machine locals hoisted into
/// <c>&lt;Method&gt;d__N</c> fields, lambda/method-group caches in <c>&lt;&gt;c</c>/<c>&lt;&gt;O</c> types).
///
/// This exists because an AssemblyRef scan is the wrong altitude for one specific failure: an assembly may
/// legitimately reference another (method bodies and signatures resolve lazily at JIT), but a <b>field</b> of an
/// unresolvable type makes <c>Assembly.GetTypes()</c> throw <c>ReflectionTypeLoadException</c> for the whole
/// assembly — and BepInEx ecosystems are full of scanners that call <c>GetTypes()</c> over every loaded assembly,
/// so one bad field breaks unrelated plugins process-wide. Hit twice in this project before this check existed
/// (Docs/ArchitectureEnforcement.md FG-ARCH-011).
///
/// Metadata is read, not loaded, exactly like <see cref="AssemblyReferenceInspector"/>.
/// </summary>
public static class FieldSignatureInspector
{
    /// <summary>
    /// Every field whose signature binds to <paramref name="assemblyName"/>, described as
    /// <c>Namespace.DeclaringType.FieldName : FieldType</c>. Empty when the assembly is clean.
    /// </summary>
    public static IReadOnlyList<string> DescribeFieldsBindingToAssembly(string assemblyPath, string assemblyName)
    {
        using var stream = File.OpenRead(assemblyPath);
        using var peReader = new PEReader(stream);

        if (!peReader.HasMetadata)
            throw new BadImageFormatException("The file contains no CLI metadata; it is not a managed assembly.");

        var metadata = peReader.GetMetadataReader();
        var offenders = new List<string>();

        foreach (var handle in metadata.FieldDefinitions)
        {
            var field = metadata.GetFieldDefinition(handle);
            var provider = new AssemblyCollectingTypeProvider(metadata);
            var fieldType = field.DecodeSignature(provider, genericContext: null);

            if (!provider.ReferencedAssemblyNames.Contains(assemblyName, StringComparer.OrdinalIgnoreCase))
                continue;

            var declaringType = metadata.GetTypeDefinition(field.GetDeclaringType());
            var ns = metadata.GetString(declaringType.Namespace);
            var typeName = DescribeDeclaringType(metadata, declaringType);
            var fieldName = metadata.GetString(field.Name);
            offenders.Add($"{(ns.Length == 0 ? "" : ns + ".")}{typeName}.{fieldName} : {fieldType}");
        }

        return offenders;
    }

    /// <summary>Nested types (state machines, closure classes) shown as Outer/Nested so the culprit is findable.</summary>
    private static string DescribeDeclaringType(MetadataReader metadata, TypeDefinition type)
    {
        var name = metadata.GetString(type.Name);
        if (!type.IsNested)
            return name;

        var outer = metadata.GetTypeDefinition(type.GetDeclaringType());
        return $"{DescribeDeclaringType(metadata, outer)}/{name}";
    }

    /// <summary>
    /// A signature decoder that renders each type as a display string while recording, on the side, the name of
    /// every assembly a type reference in the signature resolves to (walking nested TypeReference scopes out to
    /// their AssemblyReference).
    /// </summary>
    private sealed class AssemblyCollectingTypeProvider : ISignatureTypeProvider<string, object?>
    {
        private readonly MetadataReader _metadata;

        public AssemblyCollectingTypeProvider(MetadataReader metadata) => _metadata = metadata;

        public HashSet<string> ReferencedAssemblyNames { get; } = new(StringComparer.OrdinalIgnoreCase);

        public string GetTypeFromReference(MetadataReader reader, TypeReferenceHandle handle, byte rawTypeKind)
        {
            var typeReference = reader.GetTypeReference(handle);
            RecordResolutionScope(reader, typeReference);
            return $"{reader.GetString(typeReference.Namespace)}.{reader.GetString(typeReference.Name)}";
        }

        private void RecordResolutionScope(MetadataReader reader, TypeReference typeReference)
        {
            var scope = typeReference.ResolutionScope;
            while (scope.Kind == HandleKind.TypeReference)
            {
                typeReference = reader.GetTypeReference((TypeReferenceHandle)scope);
                scope = typeReference.ResolutionScope;
            }

            if (scope.Kind == HandleKind.AssemblyReference)
            {
                var assembly = reader.GetAssemblyReference((AssemblyReferenceHandle)scope);
                ReferencedAssemblyNames.Add(reader.GetString(assembly.Name));
            }
        }

        public string GetTypeFromDefinition(MetadataReader reader, TypeDefinitionHandle handle, byte rawTypeKind)
        {
            var type = reader.GetTypeDefinition(handle);
            return $"{reader.GetString(type.Namespace)}.{reader.GetString(type.Name)}";
        }

        public string GetTypeFromSpecification(MetadataReader reader, object? genericContext, TypeSpecificationHandle handle, byte rawTypeKind) =>
            reader.GetTypeSpecification(handle).DecodeSignature(this, genericContext);

        public string GetPrimitiveType(PrimitiveTypeCode typeCode) => typeCode.ToString();

        public string GetSZArrayType(string elementType) => elementType + "[]";

        public string GetArrayType(string elementType, ArrayShape shape) => $"{elementType}[{new string(',', shape.Rank - 1)}]";

        public string GetByReferenceType(string elementType) => elementType + "&";

        public string GetPointerType(string elementType) => elementType + "*";

        public string GetGenericInstantiation(string genericType, ImmutableArray<string> typeArguments) =>
            $"{genericType}<{string.Join(", ", typeArguments)}>";

        public string GetGenericMethodParameter(object? genericContext, int index) => $"!!{index}";

        public string GetGenericTypeParameter(object? genericContext, int index) => $"!{index}";

        public string GetModifiedType(string modifier, string unmodifiedType, bool isRequired) => unmodifiedType;

        public string GetPinnedType(string elementType) => elementType;

        public string GetFunctionPointerType(MethodSignature<string> signature) => "fnptr";
    }
}
