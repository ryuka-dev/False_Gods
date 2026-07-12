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
    ///
    /// REVISION (P5 re-test, Path A): the earlier runs called <c>AstarPath.Scan()</c> WITHOUT first calling
    /// <c>recastGraph.SnapBoundsToScene()</c>, which the game ALWAYS does before a bake (BuildNavMeshNode.cs:44,
    /// NavMeshManager.SetupAstarPathSize, EndlessModeManager.cs:845). That step refits the graph's
    /// rasterization volume (<c>forcedBoundsCenter/Size</c>) to the collected meshes over infinite bounds —
    /// honoring our <see cref="RecastNavmeshModifier"/>(AlwaysInclude) — so skipping it left our floating
    /// island OUTSIDE the level-fitted bounds, where neither <c>CollectMeshes</c> nor the modifier AABB query
    /// (both clipped to <c>forcedBounds</c>) could see it. That is the likely reason the floor showed
    /// "0 nodes, ~6 m from the nearest node". <see cref="Rescan"/> now replicates the game's sequence and logs
    /// the bounds so the in/out signal is visible, separating R4 (does our floor rasterize at all under a
    /// runtime rescan) from R5 (does the cleaner keep only anchored areas).
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
            report.Value("recast collectionMode", recast.collectionSettings.collectionMode);
            report.Value("recast tagMask", recast.collectionSettings.tagMask == null || recast.collectionSettings.tagMask.Count == 0
                ? "<empty>"
                : string.Join(", ", recast.collectionSettings.tagMask));
            report.Line("  NOTE: each re-bake now replicates the game's BuildNavMeshNode EXACTLY — recastGraph");
            report.Line("  .SnapBoundsToScene() FIRST (refits the rasterization volume to include our floor),");
            report.Line("  THEN AstarPath.Scan() (= NavMeshManager.BakeNavMesh). Earlier P5 runs SKIPPED");
            report.Line("  SnapBoundsToScene, so our island sat outside the bounds and never rasterized — the");
            report.Line("  bounds lines below show whether our floor is now inside. It blocks (a brief freeze x3),");
            report.Line("  only APPENDS a cleaner point and restores it, and a level change rebuilds nav.");

            // Recast reads mesh triangles on the CPU (rasterizeMeshes=true, rasterizeColliders=false — P0), so
            // a bundle mesh that is not read/write enabled is invisible to it even though it renders fine.
            DiagnoseMeshes(report, _room);

            // The first two runs failed because recast never collected our floor. CollectMeshes gathers scene
            // meshes only by the graph's collectionMode (Layers vs Tags) — an untagged mesh is skipped in Tags
            // mode — but CollectRecastNavmeshModifiers() runs in BOTH modes, so a RecastNavmeshModifier set to
            // AlwaysInclude gets our floor into the scan regardless (verified against the decompiled A* source).
            AddNavmeshModifiers(report, _room);

            report.Line();
            report.Line("  -- Baseline (before any re-bake): our island floor should not be in the graph yet");
            ReportPhase(report, "baseline", recast, sample, bounds);

            // ── Phase 1: full re-bake with the cleaner points unchanged. R5 predicts our floor is erased.
            report.Line();
            report.Line("  -- Phase 1: SnapBoundsToScene() + Scan() with NO anchor on our floor");
            report.Line("     (R4: our floor should now RASTERIZE — a node within 1 m; R5: but be UNWALKABLE, erased)");
            yield return Rescan(astar, recast, report, sample);
            var phase1 = ReportPhase(report, "phase 1 / no anchor", recast, sample, bounds);

            // ── Phase 2: append a valid point on our floor and re-bake again. Expect the island to survive.
            report.Line();
            report.Line("  -- Phase 2: append a validNavMeshPoint on our floor, re-bake again (expect WALKABLE)");
            cleaner.validNavMeshPoints = Append(savedPoints, sample);
            yield return Rescan(astar, recast, report, sample);
            var phase2 = ReportPhase(report, "phase 2 / anchored", recast, sample, bounds);

            report.Line();
            report.Value("R4 verdict (does a runtime rescan rasterize our floor?)",
                (phase1.FloorRasterized || phase2.FloorRasterized)
                    ? "RASTERIZED — a nav node landed within 1 m of our floor. Runtime rescan (Option 2) IS viable; "
                      + "the earlier 'ruled out' was the missing SnapBoundsToScene, not our geometry."
                    : "NOT RASTERIZED — no node within 1 m of our floor even after SnapBoundsToScene. Option 2 "
                      + "genuinely dead for our floor; the path is a prebaked NavmeshPrefab (Option 1).");
            report.Value("R5 verdict (does the cleaner keep only anchored areas?)",
                (!phase1.FloorWalkable && phase2.FloorWalkable)
                    ? "CONFIRMED — floor erased without an anchor, walkable with one (exactly as predicted)"
                    : $"INCONCLUSIVE — phase1 walkable={phase1.FloorWalkable}, phase2 walkable={phase2.FloorWalkable} "
                      + "(needs the floor to rasterize first — read the R4 verdict and the distance lines above)");

            // ── Restore: put the cleaner points back, drop the island, and re-update so nothing walkable is
            // left behind. Destroy the room BEFORE the final update so the region re-rasterizes to nothing.
            report.Line();
            cleaner.validNavMeshPoints = savedPoints;
            if (_room != null) { UnityEngine.Object.Destroy(_room); _room = null; }
            yield return null; // let the destroy take effect before we re-bake without the island
            yield return Rescan(astar, recast, report, sample);
            UnloadBundle();

            report.Section("P5 — teardown");
            report.Line("  Cleaner points restored, island destroyed, region re-updated, bundle unloaded.");
            report.Line("  Any residual nodes are wiped on the next level change (astarPathPrefab is rebuilt).");
        }

        /// <summary>The signal for one re-bake: whether a nav node landed ON our floor (R4 — rasterization)
        /// and whether that node is walkable (R5 — cleaner). Distance separates our own floor node (~0.1 m) from
        /// a distant level node the query snapped to.</summary>
        private readonly struct PhaseResult
        {
            public readonly bool NearestWalkable;
            public readonly float NearestDistance;

            public PhaseResult(bool nearestWalkable, float nearestDistance)
            {
                NearestWalkable = nearestWalkable;
                NearestDistance = nearestDistance;
            }

            /// <summary>A node landed on our floor — the floor entered the navmesh (R4).</summary>
            public bool FloorRasterized => NearestDistance >= 0f && NearestDistance <= 1f;

            /// <summary>Our floor is rasterized AND walkable — it survived the cleaner (R5).</summary>
            public bool FloorWalkable => FloorRasterized && NearestWalkable;
        }

        /// <summary>Reports, for the node nearest our floor sample: its walkability, area, and DISTANCE (so we
        /// can tell our own floor node — ~0.1 m away — from a distant level node it snapped to), plus how many
        /// nodes fall inside the island bounds and the graph's walkable/total. Uses
        /// <see cref="NNConstraint.None"/> so Phase 1 does not snap past an unwalkable floor node to a far
        /// walkable one.</summary>
        private static PhaseResult ReportPhase(ProbeReport report, string tag, RecastGraph recast, Vector3 sample, Bounds bounds)
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
            return new PhaseResult(node != null && node.Walkable, distance);
        }

        /// <summary>Attaches a <see cref="RecastNavmeshModifier"/> (WalkableSurface, force-included) to each of
        /// our meshes. <c>RecastGraph.CollectMeshes</c> gathers scene meshes only by the graph's collectionMode
        /// (Layers/Tags) — an untagged mesh is skipped in Tags mode — but <c>CollectRecastNavmeshModifiers()</c>
        /// runs in both modes, so this makes our floor visible to the scan either way.</summary>
        private static void AddNavmeshModifiers(ProbeReport report, GameObject room)
        {
            var added = 0;
            foreach (var filter in room.GetComponentsInChildren<MeshFilter>(true))
            {
                var modifier = filter.gameObject.AddComponent<RecastNavmeshModifier>();
                modifier.mode = RecastNavmeshModifier.Mode.WalkableSurface;
                modifier.geometrySource = RecastNavmeshModifier.GeometrySource.MeshFilter;
                modifier.includeInScan = RecastNavmeshModifier.ScanInclusion.AlwaysInclude;
                modifier.dynamic = true;
                added++;
            }
            report.Value("RecastNavmeshModifier added (WalkableSurface, AlwaysInclude)", added);
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

        /// <summary>Re-bakes every graph the way the game's <c>BuildNavMeshNode</c> does: <b>refit the graph
        /// bounds first</b> (<c>recastGraph.SnapBoundsToScene()</c> — BuildNavMeshNode.cs:44), <b>then</b>
        /// <c>AstarPath.Scan()</c> (= <c>NavMeshManager.BakeNavMesh()</c>). SnapBoundsToScene collects scene
        /// meshes over infinite bounds — honoring our <see cref="RecastNavmeshModifier"/>(AlwaysInclude) — and
        /// grows <c>forcedBounds</c> to include our floor, which is exactly the step the earlier P5 runs omitted.
        /// The bounds are logged before/after so we can see our floor move inside them. Blocks (a brief freeze),
        /// then waits for the <see cref="NavMeshCleaner"/>'s post-scan work item.</summary>
        private static IEnumerator Rescan(AstarPath astar, RecastGraph recast, ProbeReport report, Vector3 sample)
        {
            ReportBounds(report, "before SnapBoundsToScene", recast, sample);
            recast.SnapBoundsToScene();
            ReportBounds(report, "after SnapBoundsToScene", recast, sample);
            astar.Scan();
            yield return new WaitForSeconds(SettleSeconds);
        }

        /// <summary>Logs the graph's rasterization volume (<c>forcedBoundsCenter/Size</c>) and whether our floor
        /// sample falls inside it. SULFUR's recast graph has zero rotation (§4.4, Dimension3D), so this
        /// axis-aligned <see cref="Bounds.Contains"/> is exact here; labelled approximate in case a future level
        /// rotates the graph. This is the smoking gun: if the sample is OUTSIDE before SnapBoundsToScene and
        /// INSIDE after, the missing call was the whole reason our floor never rasterized.</summary>
        private static void ReportBounds(ProbeReport report, string tag, RecastGraph recast, Vector3 sample)
        {
            var box = new Bounds(recast.forcedBoundsCenter, recast.forcedBoundsSize);
            report.Value($"[{tag}] forcedBounds (center / size)",
                $"{recast.forcedBoundsCenter} / {recast.forcedBoundsSize}");
            report.Value($"[{tag}] our floor sample inside bounds? (approx, graph rot=0)", box.Contains(sample));
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
