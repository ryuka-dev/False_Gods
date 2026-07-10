using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using FalseGods.Protocol.Arena.Internal;

namespace FalseGods.Protocol.Arena
{
    /// <summary>
    /// Computes the canonical <see cref="ContentHash"/> of an <see cref="ArenaContentDefinition"/>
    /// (Docs/MultiplayerLoadingContract.md §5.2.1).
    /// </summary>
    /// <remarks>
    /// The same authored content produces the same hash on any machine: every list is ordered by
    /// <see cref="StableMarkerId"/> (ordinal), every float is quantised to an integer, and nothing
    /// machine-, process-, or frame-specific is admitted. Inputs that cannot yield a reproducible hash — a
    /// non-finite float, a zero-length quaternion, an unassigned marker id, or a duplicate marker id — raise
    /// <see cref="ArenaContentExportException"/> instead of a hash (fail closed).
    /// </remarks>
    public static class ContentHashComputer
    {
        /// <summary>Computes the hash under <see cref="ContentHashSchemaVersion.Current"/>.</summary>
        public static ContentHash Compute(ArenaContentDefinition content) =>
            Compute(content, ContentHashSchemaVersion.Current);

        /// <summary>Computes the hash under an explicit schema version (the version is the first hashed input).</summary>
        public static ContentHash Compute(ArenaContentDefinition content, ContentHashSchemaVersion schemaVersion)
        {
            if (content is null)
                throw new ArgumentNullException(nameof(content));

            Validate(content);

            var encoder = new CanonicalEncoder();

            // 1. schema version
            encoder.WriteInt32(schemaVersion.Value);

            // 2. arena id + version
            encoder.WriteString(content.ArenaId);
            encoder.WriteInt32(content.ArenaVersion);

            // 3. authored content identity
            encoder.WriteString(content.ArenaContentId);

            // 4. hierarchy / markers
            foreach (var node in OrderByMarker(content.Nodes, n => n.MarkerId))
            {
                var context = $"node {node.MarkerId}";
                encoder.WriteMarker(node.MarkerId);
                encoder.WriteString(node.NodeKind);
                encoder.WriteOptionalMarker(node.ParentMarkerId);
                encoder.WriteTransform(node.LocalTransform, context);
            }

            // 5. vanilla proxies (resolved stable asset identity, not the loaded object)
            foreach (var proxy in OrderByMarker(content.VanillaProxies, p => p.MarkerId))
            {
                var context = $"vanilla proxy {proxy.MarkerId}";
                encoder.WriteMarker(proxy.MarkerId);
                encoder.WriteString(proxy.AddressableKeyOrGuid);
                encoder.WriteTransform(proxy.LocalTransform, context);
            }

            // 6. collision authoring
            foreach (var collider in OrderByMarker(content.Colliders, c => c.MarkerId))
            {
                var context = $"collider {collider.MarkerId}";
                encoder.WriteMarker(collider.MarkerId);
                encoder.WriteString(collider.ColliderKind);
                WriteGeometry(encoder, collider.GeometryParameters, context);
                encoder.WriteString(collider.LayerName);
            }

            // 7. navigation authoring
            foreach (var nav in OrderByMarker(content.NavDefinitions, n => n.MarkerId))
            {
                var context = $"nav {nav.MarkerId}";
                encoder.WriteMarker(nav.MarkerId);
                encoder.WriteString(nav.NavKind);
                encoder.WriteBounds(nav.Bounds, context);
                encoder.WriteOptionalString(nav.NavmeshPrefabContentId);
            }

            // 8. spawn definitions
            foreach (var spawn in OrderByMarker(content.Spawns, s => s.MarkerId))
            {
                var context = $"spawn {spawn.MarkerId}";
                encoder.WriteMarker(spawn.MarkerId);
                encoder.WriteString(spawn.SpawnKind);
                encoder.WriteString(spawn.DefinitionId);
                encoder.WriteTransform(spawn.LocalTransform, context);
            }

            // 9. mechanism definitions
            foreach (var mechanism in OrderByMarker(content.Mechanisms, m => m.MarkerId))
            {
                var context = $"mechanism {mechanism.MarkerId}";
                encoder.WriteMarker(mechanism.MarkerId);
                encoder.WriteString(mechanism.MechanismDefinitionId);
                encoder.WriteString(mechanism.MechanismGroupId);
                encoder.WriteTransform(mechanism.LocalTransform, context);
            }

            using var sha256 = SHA256.Create();
            return new ContentHash(sha256.ComputeHash(encoder.ToArray()));
        }

        private static void WriteGeometry(CanonicalEncoder encoder, IReadOnlyList<double> parameters, string context)
        {
            // Count-prefixed so two colliders whose parameter lists concatenate the same way still differ.
            encoder.WriteInt32(parameters.Count);
            for (var i = 0; i < parameters.Count; i++)
                encoder.WriteLength(parameters[i], $"{context} geometry[{i}]");
        }

        private static IEnumerable<T> OrderByMarker<T>(IReadOnlyList<T> items, Func<T, StableMarkerId> selector) =>
            // StableMarkerId.CompareTo is ordinal over the canonical string, which equals raw-UTF-8-byte order
            // for the GUID charset. Hierarchy/child/Addressables order are never used as the ordering source.
            items.OrderBy(selector, Comparer<StableMarkerId>.Default);

        // Every reproducibility guarantee assumes well-formed authored data. This pass rejects, up front and as
        // one ArenaContentExportException, everything that would otherwise yield a non-reproducible hash, a
        // silently-ambiguous encoding, or a NullReferenceException mid-encode. Order matters: null lists, then
        // null elements, then marker identity, then per-element fields, then parent references (which need the
        // node-marker set the identity pass proved unique).
        private static void Validate(ArenaContentDefinition content)
        {
            RequireToken(content.ArenaId, "arena", nameof(content.ArenaId));
            RequireToken(content.ArenaContentId, "arena", nameof(content.ArenaContentId));

            RequireNoNullLists(content);

            RequireNoNullElements(content.Nodes, "node");
            RequireNoNullElements(content.VanillaProxies, "vanilla proxy");
            RequireNoNullElements(content.Colliders, "collider");
            RequireNoNullElements(content.NavDefinitions, "nav");
            RequireNoNullElements(content.Spawns, "spawn");
            RequireNoNullElements(content.Mechanisms, "mechanism");

            RequireUniqueAssignedMarkers(content);
            ValidateElementFields(content);
            ValidateParentReferences(content.Nodes);
        }

        private static void ValidateElementFields(ArenaContentDefinition content)
        {
            foreach (var node in content.Nodes)
            {
                var where = $"node {node.MarkerId}";
                RequireToken(node.NodeKind, where, nameof(node.NodeKind));
                RequireTransform(node.LocalTransform, where);
            }

            foreach (var proxy in content.VanillaProxies)
            {
                var where = $"vanilla proxy {proxy.MarkerId}";
                RequireToken(proxy.AddressableKeyOrGuid, where, nameof(proxy.AddressableKeyOrGuid));
                RequireTransform(proxy.LocalTransform, where);
            }

            foreach (var collider in content.Colliders)
            {
                var where = $"collider {collider.MarkerId}";
                RequireToken(collider.ColliderKind, where, nameof(collider.ColliderKind));
                RequireToken(collider.LayerName, where, nameof(collider.LayerName));
                if (collider.GeometryParameters is null)
                {
                    throw new ArenaContentExportException(
                        $"Null GeometryParameters on {where}: pass an empty list, not null. (Individual " +
                        "parameters are validated for NaN/infinity when quantised.)");
                }
            }

            foreach (var nav in content.NavDefinitions)
            {
                var where = $"nav {nav.MarkerId}";
                RequireToken(nav.NavKind, where, nameof(nav.NavKind));
                RequireBounds(nav.Bounds, where);
                RequireOptionalToken(nav.NavmeshPrefabContentId, where, nameof(nav.NavmeshPrefabContentId));
            }

            foreach (var spawn in content.Spawns)
            {
                var where = $"spawn {spawn.MarkerId}";
                RequireToken(spawn.SpawnKind, where, nameof(spawn.SpawnKind));
                RequireToken(spawn.DefinitionId, where, nameof(spawn.DefinitionId));
                RequireTransform(spawn.LocalTransform, where);
            }

            foreach (var mechanism in content.Mechanisms)
            {
                var where = $"mechanism {mechanism.MarkerId}";
                RequireToken(mechanism.MechanismDefinitionId, where, nameof(mechanism.MechanismDefinitionId));
                RequireToken(mechanism.MechanismGroupId, where, nameof(mechanism.MechanismGroupId));
                RequireTransform(mechanism.LocalTransform, where);
            }
        }

        private static void ValidateParentReferences(IReadOnlyList<AuthoredNode> nodes)
        {
            // Marker ids were already proven present and unique, so this set is exact.
            var nodeMarkers = new HashSet<StableMarkerId>();
            foreach (var node in nodes)
                nodeMarkers.Add(node.MarkerId);

            foreach (var node in nodes)
            {
                var parent = node.ParentMarkerId;
                if (!parent.HasValue)
                    continue;

                var where = $"node {node.MarkerId}";
                if (parent.Value.IsUnassigned)
                {
                    throw new ArenaContentExportException(
                        $"Unassigned ParentMarkerId on {where}: a present parent must be an assigned marker, or " +
                        "omitted entirely for a root node.");
                }

                if (parent.Value == node.MarkerId)
                {
                    throw new ArenaContentExportException(
                        $"Node {node.MarkerId} lists itself as its own parent.");
                }

                if (!nodeMarkers.Contains(parent.Value))
                {
                    throw new ArenaContentExportException(
                        $"Dangling ParentMarkerId {parent.Value} on {where}: it matches no authored node, so the " +
                        "hierarchy is broken.");
                }
            }
        }

        private static void RequireToken(string? value, string where, string field)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                throw new ArenaContentExportException(
                    $"Missing required '{field}' on {where}: a required identifier/kind must be a non-empty, " +
                    "non-whitespace token authored in canonical form, never null or blank.");
            }
        }

        private static void RequireOptionalToken(string? value, string where, string field)
        {
            // Absent (null) is fine; a present-but-blank value is not — it would encode identically to absent.
            if (value != null && string.IsNullOrWhiteSpace(value))
            {
                throw new ArenaContentExportException(
                    $"Blank '{field}' on {where}: an optional token is either absent (null) or a real non-empty " +
                    "value. A present-but-blank value is ambiguous with absent in the hash.");
            }
        }

        private static void RequireTransform(AuthoredTransform? transform, string where)
        {
            if (transform is null)
                throw new ArenaContentExportException($"Null local transform on {where}.");
        }

        private static void RequireBounds(AuthoredBounds? bounds, string where)
        {
            if (bounds is null)
                throw new ArenaContentExportException($"Null bounds on {where}.");
        }

        private static void RequireNoNullElements<T>(IReadOnlyList<T> items, string kind)
            where T : class
        {
            for (var i = 0; i < items.Count; i++)
            {
                if (items[i] is null)
                {
                    throw new ArenaContentExportException(
                        $"Null {kind} at index {i}: a content section must not contain null elements.");
                }
            }
        }

        private static void RequireNoNullLists(ArenaContentDefinition content)
        {
            RequireNotNull(content.Nodes, nameof(content.Nodes));
            RequireNotNull(content.VanillaProxies, nameof(content.VanillaProxies));
            RequireNotNull(content.Colliders, nameof(content.Colliders));
            RequireNotNull(content.NavDefinitions, nameof(content.NavDefinitions));
            RequireNotNull(content.Spawns, nameof(content.Spawns));
            RequireNotNull(content.Mechanisms, nameof(content.Mechanisms));

            static void RequireNotNull(object? list, string name)
            {
                if (list is null)
                {
                    throw new ArenaContentExportException(
                        $"Arena content section '{name}' is null. A dropped section must not silently hash the " +
                        "same as an empty one; pass an empty list (or use ArenaContentDefinition.Create).");
                }
            }
        }

        private static void RequireUniqueAssignedMarkers(ArenaContentDefinition content)
        {
            // Every StableMarkerId across the whole arena must be present and unique — the authored ids are what
            // both the ordering and the parent references rely on (Docs/OriginalContentPipeline.md §8.3).
            var seen = new HashSet<StableMarkerId>();

            foreach (var (marker, description) in EnumerateMarkers(content))
            {
                if (marker.IsUnassigned)
                {
                    throw new ArenaContentExportException(
                        $"Unassigned StableMarkerId on {description}: every authored node that participates in " +
                        "content identity must carry an editor-assigned GUID, never a name or a default id.");
                }

                if (!seen.Add(marker))
                {
                    throw new ArenaContentExportException(
                        $"Duplicate StableMarkerId {marker} (seen again on {description}): marker ids must be " +
                        "unique across the whole arena, or ordering and parent references become ambiguous.");
                }
            }
        }

        private static IEnumerable<(StableMarkerId Marker, string Description)> EnumerateMarkers(ArenaContentDefinition content)
        {
            foreach (var node in content.Nodes)
                yield return (node.MarkerId, $"node (kind '{node.NodeKind}')");

            foreach (var proxy in content.VanillaProxies)
                yield return (proxy.MarkerId, $"vanilla proxy ('{proxy.AddressableKeyOrGuid}')");

            foreach (var collider in content.Colliders)
                yield return (collider.MarkerId, $"collider (kind '{collider.ColliderKind}')");

            foreach (var nav in content.NavDefinitions)
                yield return (nav.MarkerId, $"nav (kind '{nav.NavKind}')");

            foreach (var spawn in content.Spawns)
                yield return (spawn.MarkerId, $"spawn (kind '{spawn.SpawnKind}')");

            foreach (var mechanism in content.Mechanisms)
                yield return (mechanism.MarkerId, $"mechanism ('{mechanism.MechanismDefinitionId}')");
        }
    }
}
