using System;
using System.Collections;
using System.IO;
using System.Linq;
using BepInEx;
using LevelGeneration;
using Pathfinding;
using UnityEngine;

namespace FalseGods.Probe
{
    /// <summary>
    /// PoC step P5 — RiskList R4/R5, MinimalProofOfConceptPlan §7.2: can a mod make its own arena floor
    /// WALKABLE at runtime, and does it survive <see cref="NavMeshCleaner"/>?
    ///
    /// The game's own pipeline (BuildNavMeshNode) rescans <c>AstarPath.active</c>, then
    /// <c>NavMeshCleaner.OnGraphsPostUpdate</c> sets every node's <c>Walkable = validAreas.Contains(Area)</c>,
    /// where the valid areas are the ones nearest to <c>validNavMeshPoints</c> (level connector + room-anchor
    /// positions). A custom arena contributes none of those, so R5 predicts its floor is erased unless we
    /// register a point on it. This runs that as a two-phase experiment:
    ///   Phase 1 — UpdateGraphs over our floor with the cleaner points unchanged: expect UNWALKABLE.
    ///   Phase 2 — append a validNavMeshPoint on our floor and UpdateGraphs again: expect WALKABLE.
    ///
    /// To read the signal cleanly the room is spawned as an ISOLATED island a few metres above the player's
    /// feet, so its floor is its own nav Area rather than merging with the already-walkable level floor below.
    ///
    /// This is the one probe step that MUTATES <c>AstarPath.active</c> — the live level's shared nav graph. It
    /// only APPENDS to the cleaner's point list (the level's own areas stay valid), restores that list and
    /// re-updates before it returns, and in any case a level change rebuilds the graph from scratch
    /// (GameManager destroys and re-instantiates astarPathPrefab per level). Run it in a throwaway level.
    /// </summary>
    internal sealed class NavmeshProbe
    {
        private const string BundleFileName = "falsegods-poc-room.bundle";
        private const string OurRoomPrefabName = "PocRoom";
        private const string PlayerSpawnMarkerName = "PlayerSpawn";

        private const float EyeToFootDrop = 1.6f;
        private const float IslandHeight = 3f;   // m above the player's feet — clear of the level floor's nodes
        private const float SettleSeconds = 1f;  // wait for the async graph update + cleaner work item to run

        private AssetBundle _bundle;
        private GameObject _room;

        public IEnumerator Run(ProbeReport report)
        {
            report.Section("P5 — A* nav on our arena floor (R4/R5)");

            var astar = AstarPath.active;
            if (astar == null)
            {
                report.Line("  Skipped: AstarPath.active is null — enter a level with navigation first.");
                yield break;
            }

            var recast = astar.data?.recastGraph;
            if (recast == null)
            {
                report.Line("  Skipped: the active AstarPath has no recastGraph.");
                yield break;
            }

            var cleaner = Resources.FindObjectsOfTypeAll<NavMeshCleaner>().FirstOrDefault();
            if (cleaner == null)
            {
                report.Line("  Skipped: no NavMeshCleaner in the scene (nothing would erase or keep our island).");
                yield break;
            }

            var camera = Camera.main;
            if (camera == null)
            {
                report.Line("  Skipped: no Camera.main. Stand in a loaded level and press the key again.");
                yield break;
            }

            var bundlePath = System.IO.Path.Combine(Paths.BepInExRootPath, "FalseGods.Probe", BundleFileName);
            if (!File.Exists(bundlePath))
            {
                report.Line($"  Skipped: bundle not found at {bundlePath}.");
                report.Line("  Build it in FalseGods.Unity and deploy (dotnet build tools/FalseGods.Probe -p:DeployProbe=true).");
                yield break;
            }

            var bundleRequest = AssetBundle.LoadFromFileAsync(bundlePath);
            yield return bundleRequest;
            _bundle = bundleRequest.assetBundle;
            if (_bundle == null)
            {
                report.Line("  *** FAILED: our bundle did not load. ***");
                yield break;
            }

            var ourLoad = _bundle.LoadAssetAsync<GameObject>(OurRoomPrefabName);
            yield return ourLoad;
            var ourPrefab = ourLoad.asset as GameObject;
            if (ourPrefab == null)
            {
                report.Line($"  *** FAILED: '{OurRoomPrefabName}' not in the bundle. ***");
                UnloadBundle();
                yield break;
            }

            // Spawn our room as an isolated island a few metres above the player's feet: its floor becomes its
            // own nav Area rather than merging with the walkable level floor directly below, which would mask
            // the R5 erase/survive signal.
            var islandOrigin = camera.transform.position - Vector3.up * EyeToFootDrop + Vector3.up * IslandHeight;
            _room = UnityEngine.Object.Instantiate(ourPrefab, islandOrigin, Quaternion.identity);
            _room.name = "FalseGodsP5_NavIsland";

            var sample = SampleFloorPoint(_room, islandOrigin);
            var bounds = ArenaBounds(_room);
            var savedPoints = cleaner.validNavMeshPoints;

            report.Value("island origin (feet + up)", islandOrigin);
            report.Value("floor sample point (clear of the pillar)", sample);
            report.Value("update bounds", bounds);
            report.Value("existing validNavMeshPoints", savedPoints?.Length ?? 0);
            report.Line("  NOTE: this mutates AstarPath.active (the live level's nav). It only APPENDS a point,");
            report.Line("  and a level change rebuilds the graph — so the change is additive and recoverable.");

            // Recast reads mesh triangles on the CPU (rasterizeMeshes=true, rasterizeColliders=false — P0), so
            // a bundle mesh that is not read/write enabled is invisible to it even though it renders fine.
            DiagnoseMeshes(report, _room);

            report.Line();
            report.Line("  -- Baseline (before any UpdateGraphs): our island floor should not be in the graph yet");
            ReportPhase(report, "baseline", recast, sample, bounds);

            // ── Phase 1: rescan our island with the cleaner points unchanged. R5 predicts our floor is erased.
            report.Line();
            report.Line("  -- Phase 1: UpdateGraphs(island) with NO anchor on our floor (R5 predicts UNWALKABLE)");
            astar.UpdateGraphs(bounds);
            yield return new WaitForSeconds(SettleSeconds);
            var walkable1 = ReportPhase(report, "phase 1 / no anchor", recast, sample, bounds);

            // ── Phase 2: append a valid point on our floor and rescan again. Expect the island to survive.
            report.Line();
            report.Line("  -- Phase 2: append a validNavMeshPoint on our floor, UpdateGraphs again (expect WALKABLE)");
            cleaner.validNavMeshPoints = Append(savedPoints, sample);
            astar.UpdateGraphs(bounds);
            yield return new WaitForSeconds(SettleSeconds);
            var walkable2 = ReportPhase(report, "phase 2 / anchored", recast, sample, bounds);

            report.Line();
            report.Value("R5 verdict", (!walkable1 && walkable2)
                ? "CONFIRMED — floor erased without an anchor, walkable with one (exactly as predicted)"
                : $"UNEXPECTED — phase1 walkable={walkable1}, phase2 walkable={walkable2} (read the phase lines above)");

            // ── Restore: put the cleaner points back, drop the island, and re-update so nothing walkable is
            // left behind. Destroy the room BEFORE the final update so the region re-rasterizes to nothing.
            report.Line();
            cleaner.validNavMeshPoints = savedPoints;
            if (_room != null) { UnityEngine.Object.Destroy(_room); _room = null; }
            yield return null; // let the destroy take effect before we re-rasterize the region
            astar.UpdateGraphs(bounds);
            yield return new WaitForSeconds(SettleSeconds);
            UnloadBundle();

            report.Section("P5 — teardown");
            report.Line("  Cleaner points restored, island destroyed, region re-updated, bundle unloaded.");
            report.Line("  Any residual nodes are wiped on the next level change (astarPathPrefab is rebuilt).");
        }

        /// <summary>Reports, for the node nearest our floor sample: its walkability, area, and DISTANCE (so we
        /// can tell our own floor node — ~0.1 m away — from a distant level node it snapped to), plus how many
        /// nodes fall inside the island bounds and the graph's walkable/total. Returns true only when the
        /// nearest node is both walkable AND close, i.e. our floor really is walkable. Uses
        /// <see cref="NNConstraint.None"/> so Phase 1 does not snap past an unwalkable floor node to a far
        /// walkable one.</summary>
        private static bool ReportPhase(ProbeReport report, string tag, RecastGraph recast, Vector3 sample, Bounds bounds)
        {
            var node = recast.GetNearest(sample, NNConstraint.None).node;
            var distance = node == null ? -1f : Vector3.Distance(sample, (Vector3)node.position);

            var total = 0;
            var walkable = 0;
            var inBounds = 0;
            var inBoundsWalkable = 0;
            recast.GetNodes(n =>
            {
                total++;
                if (n.Walkable)
                    walkable++;
                if (bounds.Contains((Vector3)n.position))
                {
                    inBounds++;
                    if (n.Walkable)
                        inBoundsWalkable++;
                }
            });

            report.Value($"[{tag}] nearest node to our floor", node == null
                ? "<none>"
                : $"walkable={node.Walkable}, area={node.Area}, distance={distance:F2} m");
            report.Value($"[{tag}] nodes inside island bounds (walkable/total)", $"{inBoundsWalkable}/{inBounds}");
            report.Value($"[{tag}] whole graph (walkable/total)", $"{walkable}/{total}");
            return node != null && node.Walkable && distance <= 1f;
        }

        /// <summary>Reports each of our room meshes' readability. Recast rasterizes mesh triangles read on the
        /// CPU, so a mesh with <c>isReadable == false</c> is skipped by the nav scan (it still renders — that is
        /// GPU-only). This is the first thing to check when a floor will not rasterize.</summary>
        private static void DiagnoseMeshes(ProbeReport report, GameObject room)
        {
            foreach (var filter in room.GetComponentsInChildren<MeshFilter>(true))
            {
                var mesh = filter.sharedMesh;
                if (mesh == null)
                    continue;
                report.Value($"mesh '{filter.name}' (layer {filter.gameObject.layer})",
                    $"isReadable={mesh.isReadable}, vertices={mesh.vertexCount}");
            }
        }

        private static Vector3 SampleFloorPoint(GameObject room, Vector3 fallback)
        {
            var spawn = room.GetComponentsInChildren<Transform>(true)
                .FirstOrDefault(t => t.name == PlayerSpawnMarkerName);
            var point = spawn != null ? spawn.position : fallback;
            return point + Vector3.up * 0.1f; // just above the floor top, clear of the central pillar
        }

        private static Bounds ArenaBounds(GameObject room)
        {
            var renderers = room.GetComponentsInChildren<Renderer>(true);
            if (renderers.Length == 0)
                return new Bounds(room.transform.position, new Vector3(24f, 8f, 24f));

            var bounds = renderers[0].bounds;
            foreach (var renderer in renderers)
                bounds.Encapsulate(renderer.bounds);
            bounds.Expand(2f); // a margin so the rescan comfortably covers the whole floor
            return bounds;
        }

        private static Vector3[] Append(Vector3[] points, Vector3 add)
        {
            if (points == null || points.Length == 0)
                return new[] { add };

            var result = new Vector3[points.Length + 1];
            Array.Copy(points, result, points.Length);
            result[points.Length] = add;
            return result;
        }

        private void UnloadBundle()
        {
            if (_room != null) { UnityEngine.Object.Destroy(_room); _room = null; }
            if (_bundle != null) { _bundle.Unload(unloadAllLoadedObjects: true); _bundle = null; }
        }
    }
}
