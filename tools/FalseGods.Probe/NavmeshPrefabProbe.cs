using System;
using System.Collections;
using System.IO;
using System.Linq;
using BepInEx;
using Pathfinding;
using Pathfinding.Graphs.Navmesh;
using UnityEngine;

namespace FalseGods.Probe
{
    /// <summary>
    /// PoC step P5b — RiskList R4: is the Option 1 nav mechanism (prebaked <see cref="NavmeshPrefab"/>) usable
    /// from a mod at all? This de-risks Option 1 BEFORE any editor-bake pipeline or bundle rebuild.
    ///
    /// P5 established that a graph-wide runtime rescan (Option 2) never turns our floor walkable. Option 1 is a
    /// different code path: <c>BuildNavMeshNode</c> bakes each arena's navmesh into a serialized tile set and,
    /// at load, calls <c>NavmeshPrefab.Apply()</c> = <c>RecastGraph.ReplaceTiles(...)</c> — it INSERTS tiles
    /// into the live graph instead of rasterizing the whole scene. In the shipping design those tiles come from
    /// an editor bake in FalseGods.Unity (needed for byte-identical host/client parity, R7/R34). But every API
    /// on that path is public and A* 5.x scanning is job-based (runtime-capable), so we can bootstrap the whole
    /// mechanism here with no editor at all:
    ///
    ///   1. spawn our arena as an isolated island (same +3 m placement as <see cref="NavmeshProbe"/>);
    ///   2. <c>NavmeshPrefab.Scan(recastGraph)</c> — a LOCALIZED bake over just the arena bounds, independent of
    ///      the graph's forcedBounds — to get the serialized <c>TileMeshes</c> bytes;
    ///   3. deserialize and report the baked geometry (tiles / triangles): the R4 signal for whether our floor
    ///      even bakes;
    ///   4. replicate <c>NavmeshPrefab.Apply()</c> with the public tile API — <c>SnapToGraph</c> +
    ///      <c>ReplaceTiles</c> inside an <c>AddWorkItem</c> — to insert those tiles into the LIVE graph;
    ///   5. <c>GetNearest</c> at the floor: did a walkable node land on our floor? That is Apply-from-a-mod
    ///      working.
    ///
    /// This MUTATES <c>AstarPath.active</c> (it adds tiles), then removes them with <c>ClearTiles</c> and, in any
    /// case, a level change rebuilds the graph. Run it in a throwaway level. This is the mechanism proof; it does
    /// NOT prove the multiplayer-deterministic shipped-navmesh path, which still needs the editor bake.
    /// </summary>
    internal sealed class NavmeshPrefabProbe
    {
        private const string BundleFileName = "falsegods-poc-room.bundle";
        private const string OurRoomPrefabName = "PocRoom";
        private const string PlayerSpawnMarkerName = "PlayerSpawn";

        private const float EyeToFootDrop = 1.6f;
        private const float IslandHeight = 3f;   // m above the player's feet — its own nav area, still in-graph
        private const float SettleSeconds = 0.75f;

        private AssetBundle _bundle;
        private GameObject _room;
        private GameObject _prefabHolder;

        public IEnumerator Run(ProbeReport report)
        {
            report.Section("P5b — NavmeshPrefab Scan + Apply from a mod (R4, Option 1 mechanism)");

            var astar = AstarPath.active;
            var recast = astar?.data?.recastGraph;
            var camera = Camera.main;
            if (astar == null || recast == null)
            {
                report.Line("  Skipped: no AstarPath.active / recastGraph — enter a level with navigation first.");
                yield break;
            }
            if (camera == null)
            {
                report.Line("  Skipped: no Camera.main. Stand in a loaded level and press the key again.");
                yield break;
            }

            var bundlePath = System.IO.Path.Combine(Paths.BepInExRootPath, "FalseGods.Probe", BundleFileName);
            if (!File.Exists(bundlePath))
            {
                report.Line($"  Skipped: bundle not found at {bundlePath}.");
                yield break;
            }

            var bundleRequest = AssetBundle.LoadFromFileAsync(bundlePath);
            yield return bundleRequest;
            _bundle = bundleRequest.assetBundle;
            if (_bundle == null) { report.Line("  *** FAILED: bundle did not load. ***"); yield break; }

            var ourLoad = _bundle.LoadAssetAsync<GameObject>(OurRoomPrefabName);
            yield return ourLoad;
            if (!(ourLoad.asset is GameObject ourPrefab))
            {
                report.Line($"  *** FAILED: '{OurRoomPrefabName}' not in the bundle. ***");
                Cleanup();
                yield break;
            }

            // Spawn our room as an isolated island a few metres up — its floor becomes its own nav area, still
            // inside the graph's y-bounds so ReplaceTiles indices are valid.
            var islandOrigin = camera.transform.position - Vector3.up * EyeToFootDrop + Vector3.up * IslandHeight;
            _room = UnityEngine.Object.Instantiate(ourPrefab, islandOrigin, Quaternion.identity);
            _room.name = "FalseGodsP5b_NavPrefabIsland";

            var floorSample = SampleFloorPoint(_room, islandOrigin);
            var arenaBounds = ArenaBounds(_room);

            report.Value("island origin (feet + up)", islandOrigin);
            report.Value("floor sample point", floorSample);
            report.Value("arena world bounds (center / size)", $"{arenaBounds.center} / {arenaBounds.size}");
            report.Value("graph cellSize / editorTileSize", $"{recast.cellSize} / {recast.editorTileSize}");

            DiagnoseMeshes(report, _room);
            ReportFloorNode(report, "before (baseline)", recast, floorSample);

            // ── Build a NavmeshPrefab describing the arena region, centred on the arena, bounds.center = 0 so the
            // scan is centred on the holder's world position. A tall-enough y covers the floor + clearance.
            _prefabHolder = new GameObject("FalseGodsP5b_NavmeshPrefab");
            _prefabHolder.transform.SetPositionAndRotation(arenaBounds.center, Quaternion.identity);
            var navPrefab = _prefabHolder.AddComponent<NavmeshPrefab>();
            navPrefab.applyOnStart = false;
            navPrefab.removeTilesWhenDisabled = false;
            navPrefab.bounds = new Bounds(Vector3.zero, new Vector3(arenaBounds.size.x + 2f,
                Mathf.Max(8f, arenaBounds.size.y + 4f), arenaBounds.size.z + 2f));

            // ── Step 1: LOCALIZED bake over just the arena. This is the Option 1 bake, run at runtime. The mesh
            // it collects is our floor (Geometry(3), isReadable, per §4.2) plus whatever level geometry sits in
            // the same tiles — either way it exercises the whole Scan → serialize path.
            byte[] data = null;
            var scanError = (string)null;
            try
            {
                data = navPrefab.Scan(recast);
            }
            catch (Exception exception)
            {
                scanError = exception.ToString();
            }

            if (scanError != null)
            {
                report.Value("NavmeshPrefab.Scan()", $"THREW — {scanError}");
                report.Line("  R4 (Option 1 mechanism): scan itself failed from a mod. Read the exception above.");
                Cleanup();
                yield break;
            }

            report.Value("NavmeshPrefab.Scan() serialized bytes", data?.Length ?? 0);

            // ── Step 2: deserialize and report the baked geometry. Triangles > 0 means the localized bake DID
            // produce a navmesh over the arena region (the R4 rasterization signal, from the Option 1 path).
            TileMeshes tiles;
            try
            {
                tiles = TileMeshes.Deserialize(data);
            }
            catch (Exception exception)
            {
                report.Value("TileMeshes.Deserialize()", $"THREW — {exception}");
                Cleanup();
                yield break;
            }

            var tileCount = tiles.tileMeshes?.Length ?? 0;
            var triangleCount = tiles.tileMeshes == null ? 0
                : tiles.tileMeshes.Sum(t => (t.triangles?.Length ?? 0) / 3);
            var vertexCount = tiles.tileMeshes == null ? 0
                : tiles.tileMeshes.Sum(t => t.verticesInTileSpace?.Length ?? 0);
            report.Value("baked tileRect (W x H)", $"{tiles.tileRect.Width} x {tiles.tileRect.Height}");
            report.Value("baked tiles / triangles / vertices", $"{tileCount} / {triangleCount} / {vertexCount}");
            report.Value("baked something?", triangleCount > 0
                ? "YES — the localized NavmeshPrefab bake produced navmesh geometry over the arena region"
                : "NO — 0 triangles; nothing bakeable in the arena bounds (read the mesh diagnosis above)");

            // ── Step 3: replicate NavmeshPrefab.Apply() with the public tile API — snap to the live graph's tile
            // grid, check the baked tile dimensions match, then ReplaceTiles inside a work item.
            NavmeshPrefab.SnapToGraph(new TileLayout(recast), _prefabHolder.transform.position,
                _prefabHolder.transform.rotation, navPrefab.bounds, out var graphTileRect,
                out var snappedRotation, out var yOffset);
            tiles.Rotate(snappedRotation);

            var dimsMatch = tiles.tileRect.Width == graphTileRect.Width
                            && tiles.tileRect.Height == graphTileRect.Height;
            report.Value("Apply tile dims (baked vs graph)",
                $"{tiles.tileRect.Width}x{tiles.tileRect.Height} vs {graphTileRect.Width}x{graphTileRect.Height} — "
                + (dimsMatch ? "match" : "MISMATCH (Apply would throw in the game)"));

            if (dimsMatch)
            {
                tiles.tileRect = graphTileRect;
                var applied = false;
                astar.AddWorkItem((Action)(() => { recast.ReplaceTiles(tiles, yOffset); applied = true; }));
                var guard = 0;
                while (!applied && guard++ < 600)
                    yield return null;
                yield return new WaitForSeconds(SettleSeconds);

                ReportFloorNode(report, "after ReplaceTiles", recast, floorSample);

                // The nearest-to-sample check can miss if our floor landed at the right XZ but a shifted Y (the
                // runtime bake skips the editor's SnapToClosestTileAlignment). So MEASURE where the inserted
                // nodes actually are: enumerate every node over the arena's XZ footprint and report their Y band
                // and how many sit at our expected floor height. This distinguishes "floor landed, wrong sample"
                // from "floor genuinely misplaced / not walkable".
                var expectedFloorY = floorSample.y - 0.1f; // sample is 0.1 m above the floor top
                var footprintWalkableAtFloor = ReportArenaFootprintNodes(report, recast, arenaBounds, expectedFloorY);

                var node = recast.GetNearest(floorSample, NNConstraint.None).node;
                var distance = node == null ? -1f : Vector3.Distance(floorSample, (Vector3)node.position);
                var floorWalkableAtSample = node != null && node.Walkable && distance >= 0f && distance <= 1f;

                report.Value("R4 verdict (NavmeshPrefab.Apply from a mod)",
                    floorWalkableAtSample
                        ? "WORKS — a walkable node landed on our floor sample via ReplaceTiles. Option 1 mechanism "
                          + "is proven from a mod; the remaining work is the editor bake for deterministic parity."
                        : footprintWalkableAtFloor > 0
                            ? $"WORKS (with a placement note) — {footprintWalkableAtFloor} walkable node(s) landed "
                              + $"at our floor height over the arena footprint, but not at the corner sample "
                              + $"(nearest there was {distance:F2} m). Scan+ReplaceTiles from a mod works; precise "
                              + "tile alignment is an editor-bake concern (SnapToClosestTileAlignment)."
                            : $"FLOOR MISPLACED OR ERASED — {triangleCount} triangles baked and tiles applied, but "
                              + "no walkable node sits at the expected floor height over the footprint (see the Y "
                              + "band above). This is a real yOffset/alignment issue to resolve before the editor bake.");

                // ── Teardown: remove exactly the tiles we inserted, restoring the level's own graph.
                var cleared = false;
                astar.AddWorkItem((Action)(() => { recast.ClearTiles(graphTileRect); cleared = true; }));
                guard = 0;
                while (!cleared && guard++ < 600)
                    yield return null;
                yield return new WaitForSeconds(SettleSeconds);
                report.Line("  Inserted tiles cleared (ClearTiles over the same rect).");
            }
            else
            {
                report.Line("  Skipped ReplaceTiles: baked tile dimensions do not match the graph grid.");
            }

            Cleanup();
            report.Section("P5b — teardown");
            report.Line("  Island + NavmeshPrefab destroyed, bundle unloaded. A level change rebuilds nav anyway.");
        }

        /// <summary>Nearest-node line at our floor: walkability, area, and distance (so a node ON our floor,
        /// ~0.1 m, is distinct from the level floor ~3 m below), plus the whole graph's walkable/total so the
        /// insert delta is visible.</summary>
        private static void ReportFloorNode(ProbeReport report, string tag, RecastGraph recast, Vector3 sample)
        {
            var node = recast.GetNearest(sample, NNConstraint.None).node;
            var distance = node == null ? -1f : Vector3.Distance(sample, (Vector3)node.position);
            var total = 0;
            var walkable = 0;
            recast.GetNodes(n => { total++; if (n.Walkable) walkable++; });
            report.Value($"[{tag}] nearest node to floor", node == null
                ? "<none>"
                : $"walkable={node.Walkable}, area={node.Area}, distance={distance:F2} m");
            report.Value($"[{tag}] whole graph (walkable/total)", $"{walkable}/{total}");
        }

        /// <summary>Enumerates every node whose XZ lies within the arena footprint and reports the Y band of the
        /// walkable ones, plus how many sit within 1 m of the expected floor height. Returns that count — the
        /// direct measurement of whether our inserted floor is walkable where the floor actually is, independent
        /// of any single sample point landing in a wall-eroded corner.</summary>
        private static int ReportArenaFootprintNodes(ProbeReport report, RecastGraph recast, Bounds arena, float expectedFloorY)
        {
            var minX = arena.center.x - arena.extents.x;
            var maxX = arena.center.x + arena.extents.x;
            var minZ = arena.center.z - arena.extents.z;
            var maxZ = arena.center.z + arena.extents.z;

            var walkable = 0;
            var yMin = float.PositiveInfinity;
            var yMax = float.NegativeInfinity;
            var walkableAtFloor = 0;
            recast.GetNodes(n =>
            {
                var p = (Vector3)n.position;
                if (p.x < minX || p.x > maxX || p.z < minZ || p.z > maxZ)
                    return;
                if (!n.Walkable)
                    return;
                walkable++;
                if (p.y < yMin) yMin = p.y;
                if (p.y > yMax) yMax = p.y;
                if (Mathf.Abs(p.y - expectedFloorY) <= 1f)
                    walkableAtFloor++;
            });

            report.Value("arena footprint: walkable nodes", walkable);
            report.Value("arena footprint: walkable Y band",
                walkable == 0 ? "<none>" : $"{yMin:F2} .. {yMax:F2} m (expected floor ~{expectedFloorY:F2})");
            report.Value("arena footprint: walkable nodes at floor height (+/-1 m)", walkableAtFloor);
            return walkableAtFloor;
        }

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
            return point + Vector3.up * 0.1f;
        }

        private static Bounds ArenaBounds(GameObject room)
        {
            var renderers = room.GetComponentsInChildren<Renderer>(true);
            if (renderers.Length == 0)
                return new Bounds(room.transform.position, new Vector3(24f, 8f, 24f));
            var bounds = renderers[0].bounds;
            foreach (var renderer in renderers)
                bounds.Encapsulate(renderer.bounds);
            return bounds;
        }

        private void Cleanup()
        {
            if (_prefabHolder != null) { UnityEngine.Object.Destroy(_prefabHolder); _prefabHolder = null; }
            if (_room != null) { UnityEngine.Object.Destroy(_room); _room = null; }
            if (_bundle != null) { _bundle.Unload(unloadAllLoadedObjects: true); _bundle = null; }
        }
    }
}
