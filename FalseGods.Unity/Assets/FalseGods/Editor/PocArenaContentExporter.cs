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
    /// Exports the authored arena content artifact for the cave arena: the canonical authored inputs (nodes,
    /// colliders, navigation, spawns, material borrows) from which the runtime recomputes the content hash
    /// (RiskList R34), plus a parity map the runtime checks the realized hierarchy against (RiskList R14). This is
    /// PoC step P8.1 (Docs/OriginalContentPipeline.md §8.3/§8.6, Docs/MultiplayerLoadingContract.md §5.2.1).
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
    ///
    /// Direction B: our own Floor/Walls/Ceiling/Rocks wear vanilla cave materials borrowed BY NAME at runtime
    /// from a pinned donor carrier (CaveNormal3New — one clean prefab whose renderers carry the whole cave
    /// material set, each name resolving to a single distinct material, verified against the carrier). The GUID
    /// names the player's own installed asset; nothing vanilla ships. Each borrow targets sub-material 0 of the
    /// target renderer.
    /// </summary>
    public static class PocArenaContentExporter
    {
        public const string ArtifactFileName = "arena-content-PocRoom.artifact";
        private const string OutputDirectory = "Build";

        // These stamp the manifest peers exchange. SchemaVersion MUST equal
        // FalseGods.Protocol.Arena.ContentHashSchemaVersion.Current — a divergence means the runtime and the
        // artifact disagree on which hash definition is in force. Adding more rows of existing kinds (walls,
        // ceiling, rocks, their borrows) does NOT change the hash *definition*, so SchemaVersion stays 2; only the
        // hash *value* changes with the content. ProtocolVersion/BundleVersion are constants for the PoC.
        private const int ArtifactFormatVersion = 2;
        private const int SchemaVersion = 2;
        private const int ProtocolVersion = 1;
        private const string BundleVersion = "false_gods.poc.1";

        private const string ArenaId = "false_gods.arena.poc_room";
        private const int ArenaVersion = 1;
        private const string ArenaContentId = "assets/falsegods/arenas/pocroom/pocroom.prefab";

        // The pinned donor carrier (CaveNormal3New). Its renderers carry CaveFloor/CaveWall{,Bot,Mid,Top}/
        // CaveCeilingOther/Rocks_Caves, each resolving to exactly one distinct material (verified on the carrier),
        // so name-borrow is deterministic and fail-closed.
        private const string CaveCarrierGuid = "92103c239550ca740906311170fcc458";

        // Cave surface material names to borrow (must exist, uniquely, on CaveCarrierGuid).
        private const string MatFloor = "CaveFloor";
        private const string MatWall = "CaveWall";
        private const string MatCeiling = "CaveCeilingOther";
        private const string MatRock = "Rocks_Caves";

        private const int RockCount = 10; // must match PocRoomGenerator.Rocks.Length

        private const char Sep = '\t';
        private const string Nil = "-";

        // The four boundary walls have visual + collider counterparts; keep the ordering in one place.
        private static readonly string[] WallSuffixes = { "N", "E", "S", "W" };
        private static readonly string[] BoundaryColliderNames = { "WallNorth", "WallSouth", "WallEast", "WallWest" };

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

        // ── Deterministic GUID number allocation (stable across regenerations) ──────────────────────────────
        //   1..5   roots            6        Floor
        //   40..43 walls N/E/S/W    44       Ceiling             70..79  Rock_1..10
        //   10     FloorCollider    12..15   boundary walls
        //   20     nav              30/31    player/enemy spawn
        //   50     Floor borrow     52..55   wall borrows        56      Ceiling borrow   80..89 Rock borrows
        private static int WallNode(int i) => 40 + i;      // i = 0..3
        private static int RockNode(int i) => 70 + i;      // i = 0..RockCount-1
        private static int WallBorrow(int i) => 52 + i;
        private static int RockBorrow(int i) => 80 + i;

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
            for (var i = 0; i < WallSuffixes.Length; i++)
                NodeRow(sb, root, WallPath(i), Guid(WallNode(i)), WallName(i), visualRoot);
            NodeRow(sb, root, "VisualRoot/Ceiling", Guid(44), "Ceiling", visualRoot);
            for (var i = 0; i < RockCount; i++)
                NodeRow(sb, root, RockPath(i), Guid(RockNode(i)), RockName(i), visualRoot);

            // ── Colliders (kind + half-extents geometry + layer NAME; position is not hashed, per §5.2.1 input 6).
            ColliderRow(sb, root, "CollisionRoot/FloorCollider", Guid(10));
            ColliderRow(sb, root, "CollisionRoot/WallNorth", Guid(12));
            ColliderRow(sb, root, "CollisionRoot/WallSouth", Guid(13));
            ColliderRow(sb, root, "CollisionRoot/WallEast", Guid(14));
            ColliderRow(sb, root, "CollisionRoot/WallWest", Guid(15));

            // ── Navigation authoring: the walkable floor surface, realized at runtime via the prebaked
            //    NavmeshPrefab path (Option 1, RiskList R4). Bounds are the 60x60 m floor top at y=0.
            Row(sb, "nav", Guid(20), "WalkableSurface", "arena-nav-PocRoom",
                Flt(0f), Flt(0f), Flt(0f), Flt(60f), Flt(0.1f), Flt(60f));

            // ── Spawns.
            SpawnRow(sb, root, "GameplayRoot/PlayerSpawn", Guid(30), "Player", "false_gods.spawn.player");
            SpawnRow(sb, root, "GameplayRoot/EnemySpawn", Guid(31), "Enemy", "false_gods.spawn.dummy");

            // ── Material borrows (input 10): every visible surface wears a vanilla cave material (direction B).
            //    Target marker + carrier GUID + material name + sub-material index are the hashed donor identity;
            //    the trailing runtime path is a locator, not hashed.
            MaterialBorrowRow(sb, Guid(50), Guid(6), 0, CaveCarrierGuid, MatFloor, "VisualRoot/Floor");
            for (var i = 0; i < WallSuffixes.Length; i++)
                MaterialBorrowRow(sb, Guid(WallBorrow(i)), Guid(WallNode(i)), 0, CaveCarrierGuid, MatWall, WallPath(i));
            MaterialBorrowRow(sb, Guid(56), Guid(44), 0, CaveCarrierGuid, MatCeiling, "VisualRoot/Ceiling");
            for (var i = 0; i < RockCount; i++)
                MaterialBorrowRow(sb, Guid(RockBorrow(i)), Guid(RockNode(i)), 0, CaveCarrierGuid, MatRock, RockPath(i));

            // ── Parity map (R14): every identity node/collider/spawn the runtime should find, by path, with the
            //    authored local transform to compare against. Never fed to the hash.
            foreach (var (path, kind) in ParityTargets())
                ParityRow(sb, root, path, kind);

            return sb.ToString();
        }

        private static string WallName(int i) => "Wall_" + WallSuffixes[i];
        private static string WallPath(int i) => "VisualRoot/" + WallName(i);
        private static string RockName(int i) => "Rock_" + (i + 1).ToString(CultureInfo.InvariantCulture);
        private static string RockPath(int i) => "VisualRoot/" + RockName(i);

        private static IEnumerable<(string Path, string Kind)> ParityTargets()
        {
            yield return ("VisualRoot", "VisualRoot");
            yield return ("CollisionRoot", "CollisionRoot");
            yield return ("NavigationRoot", "NavigationRoot");
            yield return ("GameplayRoot", "GameplayRoot");

            yield return ("VisualRoot/Floor", "Floor");
            for (var i = 0; i < WallSuffixes.Length; i++)
                yield return (WallPath(i), "Wall");
            yield return ("VisualRoot/Ceiling", "Ceiling");
            for (var i = 0; i < RockCount; i++)
                yield return (RockPath(i), "Rock");

            yield return ("CollisionRoot/FloorCollider", "Box");
            foreach (var name in BoundaryColliderNames)
                yield return ("CollisionRoot/" + name, "Box");

            yield return ("GameplayRoot/PlayerSpawn", "Player");
            yield return ("GameplayRoot/EnemySpawn", "Enemy");
        }

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

        private static void MaterialBorrowRow(StringBuilder sb, string marker, string targetMarker,
            int subMaterialIndex, string carrierGuid, string materialName, string targetPath)
        {
            Row(sb, "matborrow", marker, targetMarker, Int(subMaterialIndex), carrierGuid, materialName, targetPath);
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
