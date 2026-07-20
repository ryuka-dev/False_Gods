using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using UnityEditor;
using UnityEngine;

namespace FalseGods.EditorTools
{
    /// <summary>
    /// Exports the authored arena content artifact for the PoC room: the canonical authored inputs (nodes,
    /// colliders, navigation, spawns) from which the runtime recomputes the content hash (RiskList R34), plus a
    /// parity map the runtime checks the realized hierarchy against (RiskList R14). This is PoC step P8.1
    /// (Docs/OriginalContentPipeline.md §8.3/§8.6, Docs/MultiplayerLoadingContract.md §5.2.1).
    ///
    /// The on-disk format is the tab-separated line format that <c>FalseGods.Protocol.Arena.ArenaContentArtifact</c>
    /// reads. This project cannot reference FalseGods.Protocol (Unity is .NET Standard; Protocol is net472), so
    /// the writer is re-implemented here and kept byte-compatible with that reader — the runtime proves agreement
    /// by parsing this file, and a Protocol fixture test pins the shape. Keep the two in lockstep.
    ///
    /// StableMarkerIds are fixed GUIDs assigned here (never names/paths/InstanceIDs), so the authored identity —
    /// and therefore the hash — is deterministic across machines and builds. The transforms and collider
    /// geometry are read from the actual generated prefab, so the artifact reflects exactly what ships; a missing
    /// authored node is a hard export failure, mirroring the "fail the build" rule (§8.3).
    /// </summary>
    public static class PocArenaContentExporter
    {
        public const string ArtifactFileName = "arena-content-PocRoom.artifact";
        private const string OutputDirectory = "Build";

        // These stamp the manifest peers exchange. SchemaVersion MUST equal
        // FalseGods.Protocol.Arena.ContentHashSchemaVersion.Current — a divergence means the runtime and the
        // artifact disagree on which hash definition is in force. ProtocolVersion/BundleVersion are constants for
        // the PoC (a constant BundleVersion is deliberate: two peers who shipped the same bundle must stamp the
        // same value, so a build date would be wrong here).
        private const int ArtifactFormatVersion = 2;
        private const int SchemaVersion = 2;
        private const int ProtocolVersion = 1;
        private const string BundleVersion = "false_gods.poc.1";

        private const string ArenaId = "false_gods.arena.poc_room";
        private const int ArenaVersion = 1;
        private const string ArenaContentId = "assets/falsegods/arenas/pocroom/pocroom.prefab";

        private const char Sep = '\t';
        private const string Nil = "-";

        [MenuItem("False Gods/Export Arena Content Artifact")]
        public static void Export()
        {
            var path = ExportInternal();
            Debug.Log($"[FalseGods] Arena content artifact written to {path}.");
        }

        /// <summary>Headless entry point, same contract as <see cref="PocBundleBuilder.BuildFromBatchMode"/>:
        /// explicit process exit code, no -quit, trust the code not the log.</summary>
        public static void ExportFromBatchMode()
        {
            try
            {
                var path = ExportInternal();
                Debug.Log($"[FalseGods] Batch artifact export OK: {path}");
                EditorApplication.Exit(0);
            }
            catch (Exception exception)
            {
                Debug.LogError($"[FalseGods] Batch artifact export FAILED: {exception}");
                EditorApplication.Exit(1);
            }
        }

        /// <summary>Regenerates the room, then writes the artifact next to the bundle and returns its full path.
        /// A stale prefab is exactly the editor/runtime divergence R14 exists to catch, so the standalone entry
        /// points regenerate first. The bundle builder, which has just generated the room itself, calls
        /// <see cref="WriteArtifactForCurrentPrefab"/> to avoid regenerating twice.</summary>
        public static string ExportInternal()
        {
            PocRoomGenerator.Generate();
            return WriteArtifactForCurrentPrefab();
        }

        /// <summary>Writes the artifact from the prefab currently on disk (no regenerate). Returns its full path.</summary>
        public static string WriteArtifactForCurrentPrefab()
        {
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(PocRoomGenerator.PrefabPath);
            if (prefab == null)
                throw new InvalidOperationException($"PoC room prefab not found at {PocRoomGenerator.PrefabPath}.");

            var text = BuildArtifact(prefab.transform);

            Directory.CreateDirectory(OutputDirectory);
            var path = Path.Combine(OutputDirectory, ArtifactFileName);
            // '\n' line endings (LZ-free plain text): the reader normalises CRLF, but keep the bytes canonical.
            File.WriteAllText(path, text, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            return Path.GetFullPath(path);
        }

        // Fixed StableMarkerId GUIDs (canonical "D" form). Deterministic and unique across the arena.
        private static string Guid(int n) => $"fa15e900-0000-0000-0000-{n:000000000000}";

        private static string BuildArtifact(Transform root)
        {
            var sb = new StringBuilder();

            Row(sb, "FGARENA", ArtifactFormatVersion.ToString(CultureInfo.InvariantCulture));
            Row(sb, "arenaId", ArenaId);
            Row(sb, "arenaVersion", Int(ArenaVersion));
            Row(sb, "arenaContentId", ArenaContentId);
            Row(sb, "schemaVersion", Int(SchemaVersion));
            Row(sb, "protocolVersion", Int(ProtocolVersion));
            Row(sb, "bundleVersion", BundleVersion);

            // ── Hierarchy nodes (identity skeleton). LightingRoot + its lights are presentation, not identity,
            //    and are deliberately excluded (Docs/MultiplayerLoadingContract.md §5.2.1 "explicitly excluded").
            var pocRoom = Guid(1);
            var visualRoot = Guid(2);
            var collisionRoot = Guid(3);
            var navigationRoot = Guid(4);
            var gameplayRoot = Guid(5);

            NodeRow(sb, root, "", pocRoom, "ArenaRoot", null);
            NodeRow(sb, root, "VisualRoot", visualRoot, "VisualRoot", pocRoom);
            NodeRow(sb, root, "CollisionRoot", collisionRoot, "CollisionRoot", pocRoom);
            NodeRow(sb, root, "NavigationRoot", navigationRoot, "NavigationRoot", pocRoom);
            NodeRow(sb, root, "GameplayRoot", gameplayRoot, "GameplayRoot", pocRoom);
            NodeRow(sb, root, "VisualRoot/Floor", Guid(6), "Floor", visualRoot);
            NodeRow(sb, root, "VisualRoot/Pillar", Guid(7), "Pillar", visualRoot);

            // ── Colliders (kind + half-extents geometry + layer NAME; position is not hashed, per §5.2.1 input 6).
            ColliderRow(sb, root, "CollisionRoot/FloorCollider", Guid(10));
            ColliderRow(sb, root, "CollisionRoot/PillarCollider", Guid(11));
            ColliderRow(sb, root, "CollisionRoot/WallNorth", Guid(12));
            ColliderRow(sb, root, "CollisionRoot/WallSouth", Guid(13));
            ColliderRow(sb, root, "CollisionRoot/WallEast", Guid(14));
            ColliderRow(sb, root, "CollisionRoot/WallWest", Guid(15));

            // ── Navigation authoring: the walkable floor surface, realized at runtime via the prebaked
            //    NavmeshPrefab path (Option 1, RiskList R4). Bounds are the 20x20 m floor top at y=0; the content
            //    id names the baked artifact family (cellSize-specific bytes are chosen at load).
            Row(sb, "nav", Guid(20), "WalkableSurface", "arena-nav-PocRoom",
                Flt(0f), Flt(0f), Flt(0f), Flt(20f), Flt(0.1f), Flt(20f));

            // ── Spawns.
            SpawnRow(sb, root, "GameplayRoot/PlayerSpawn", Guid(30), "Player", "false_gods.spawn.player");
            SpawnRow(sb, root, "GameplayRoot/EnemySpawn", Guid(31), "Enemy", "false_gods.spawn.dummy");

            // ── Parity map (R14): every identity node/collider/spawn the runtime should find, by path, with the
            //    authored local transform to compare against. Never fed to the hash.
            foreach (var (path, kind) in ParityTargets())
                ParityRow(sb, root, path, kind);

            return sb.ToString();
        }

        private static IEnumerable<(string Path, string Kind)> ParityTargets() => new[]
        {
            ("VisualRoot", "VisualRoot"),
            ("CollisionRoot", "CollisionRoot"),
            ("NavigationRoot", "NavigationRoot"),
            ("GameplayRoot", "GameplayRoot"),
            ("VisualRoot/Floor", "Floor"),
            ("VisualRoot/Pillar", "Pillar"),
            ("CollisionRoot/FloorCollider", "Box"),
            ("CollisionRoot/PillarCollider", "Box"),
            ("CollisionRoot/WallNorth", "Box"),
            ("CollisionRoot/WallSouth", "Box"),
            ("CollisionRoot/WallEast", "Box"),
            ("CollisionRoot/WallWest", "Box"),
            ("GameplayRoot/PlayerSpawn", "Player"),
            ("GameplayRoot/EnemySpawn", "Enemy"),
        };

        private static void NodeRow(StringBuilder sb, Transform root, string path, string marker, string kind, string parentMarker)
        {
            var t = path.Length == 0 ? root : Find(root, path);
            Row(sb, Prepend("node", marker, kind, parentMarker ?? Nil, Transform(t)));
        }

        private static void ColliderRow(StringBuilder sb, Transform root, string path, string marker)
        {
            var t = Find(root, path);
            var box = t.GetComponent<BoxCollider>();
            if (box == null)
                throw new InvalidOperationException($"Authored collider '{path}' has no BoxCollider.");
            var layerName = LayerMask.LayerToName(t.gameObject.layer);
            if (string.IsNullOrEmpty(layerName))
                throw new InvalidOperationException($"Collider '{path}' is on unnamed layer {t.gameObject.layer}.");
            // Box half-extents in a fixed order (x, y, z), per §5.2.1 input 6.
            var half = box.size * 0.5f;
            Row(sb, "collider", marker, "Box", layerName, "3", Dbl(half.x), Dbl(half.y), Dbl(half.z));
        }

        private static void SpawnRow(StringBuilder sb, Transform root, string path, string marker, string kind, string definitionId)
        {
            var t = Find(root, path);
            Row(sb, Prepend("spawn", marker, kind, definitionId, Transform(t)));
        }

        private static void ParityRow(StringBuilder sb, Transform root, string path, string kind)
        {
            var t = Find(root, path);
            Row(sb, Prepend("parity", path, kind, Transform(t)));
        }

        private static Transform Find(Transform root, string path)
        {
            var t = root.Find(path);
            if (t == null)
                throw new InvalidOperationException(
                    $"Authored node '{path}' not found under '{root.name}'. The prefab and the exporter's " +
                    "authored set have diverged — regenerate the room or update the exporter (fail-closed).");
            return t;
        }

        // ── Row helpers: byte-compatible with FalseGods.Protocol.Arena.ArenaContentArtifact's reader. ─────────

        private static string[] Prepend(string tag, params object[] parts)
        {
            var flat = new List<string> { tag };
            foreach (var part in parts)
            {
                if (part is string[] group) flat.AddRange(group);
                else flat.Add((string)part);
            }
            return flat.ToArray();
        }

        private static void Row(StringBuilder sb, params string[] fields)
        {
            for (var i = 0; i < fields.Length; i++)
            {
                if (i > 0) sb.Append(Sep);
                var f = fields[i];
                if (f.IndexOf(Sep) >= 0 || f.IndexOf('\n') >= 0)
                    throw new InvalidOperationException($"Artifact field contains a tab or newline: '{f}'.");
                sb.Append(f);
            }
            sb.Append('\n');
        }

        private static string[] Transform(Transform t)
        {
            var p = t.localPosition;
            var r = t.localRotation;
            var s = t.localScale;
            return new[]
            {
                Flt(p.x), Flt(p.y), Flt(p.z),
                Flt(r.x), Flt(r.y), Flt(r.z), Flt(r.w),
                Flt(s.x), Flt(s.y), Flt(s.z),
            };
        }

        private static string Int(int value) => value.ToString(CultureInfo.InvariantCulture);
        private static string Flt(float value) => value.ToString("R", CultureInfo.InvariantCulture);
        private static string Dbl(double value) => value.ToString("R", CultureInfo.InvariantCulture);
    }
}
