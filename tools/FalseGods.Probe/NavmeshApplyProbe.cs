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
    /// PoC step P5d — the "ship → apply" half of the in-game Option 1 pipeline (RiskList R4). P5c bakes the
    /// arena navmesh and writes <c>arena-nav-PocRoom-cell&lt;size&gt;.bytes</c>. This step proves that a SAVED
    /// artifact (not a live scan) applies and makes OUR floor walkable: it reads the bytes file matching the
    /// live graph's <c>cellSize</c>, spawns the arena, and applies the bytes with the shippable path —
    /// <c>TileMeshes.Deserialize</c> + <c>NavmeshPrefab.SnapToGraph</c> + <c>RecastGraph.ReplaceTiles</c> — the
    /// exact operations the plugin will run at runtime (no A* in FalseGods.Unity, just these public calls).
    ///
    /// It measures the walkable nodes over the arena footprint at floor height BEFORE and AFTER applying, so the
    /// delta is unambiguously our floor (not level geometry — the P5b false-positive trap). The arena is floated
    /// a few metres up so its floor is its own height band, still inside the graph so <c>ReplaceTiles</c> indices
    /// are valid. Mutates <c>AstarPath.active</c> (adds tiles), then <c>ClearTiles</c>-es them; a level change
    /// rebuilds nav anyway. Run in a level whose <c>cellSize</c> matches an available bytes file.
    /// </summary>
    internal sealed class NavmeshApplyProbe
    {
        private const string BundleFileName = "falsegods-poc-room.bundle";
        private const string OurRoomPrefabName = "PocRoom";

        private const float EyeToFootDrop = 1.6f;
        private const float IslandHeight = 3f;
        private const float SettleSeconds = 0.75f;

        private AssetBundle _bundle;
        private GameObject _room;
        private GameObject _prefabHolder;

        public IEnumerator Run(ProbeReport report)
        {
            report.Section("P5d — apply a SHIPPED arena navmesh artifact (R4, Option 1 ship->apply)");

            var astar = AstarPath.active;
            var recast = astar?.data?.recastGraph;
            var camera = Camera.main;
            if (recast == null || camera == null)
            {
                report.Line("  Skipped: need AstarPath.active + recastGraph + Camera.main.");
                yield break;
            }

            // Find the bytes file matching this level's cellSize (a bake is cellSize-specific).
            var dir = System.IO.Path.Combine(Paths.BepInExRootPath, "FalseGods.Probe");
            var fileName = $"arena-nav-{OurRoomPrefabName}-cell{recast.cellSize:0.00}.bytes";
            var bytesPath = System.IO.Path.Combine(dir, fileName);
            report.Value("live graph cellSize", recast.cellSize.ToString("0.00"));
            report.Value("looking for artifact", fileName);
            if (!File.Exists(bytesPath))
            {
                report.Line($"  Skipped: no artifact for cellSize {recast.cellSize:0.00}. Bake one with F6 in this level first.");
                var present = Directory.Exists(dir)
                    ? string.Join(", ", Directory.GetFiles(dir, "arena-nav-*.bytes").Select(System.IO.Path.GetFileName))
                    : "<none>";
                report.Value("artifacts present", string.IsNullOrEmpty(present) ? "<none>" : present);
                yield break;
            }

            byte[] data;
            try { data = File.ReadAllBytes(bytesPath); }
            catch (Exception exception) { report.Value("read FAILED", exception.ToString()); yield break; }
            report.Value("artifact bytes", data.Length);

            var bundlePath = System.IO.Path.Combine(dir, BundleFileName);
            var bundleRequest = AssetBundle.LoadFromFileAsync(bundlePath);
            yield return bundleRequest;
            _bundle = bundleRequest.assetBundle;
            var ourLoad = _bundle == null ? null : _bundle.LoadAssetAsync<GameObject>(OurRoomPrefabName);
            if (ourLoad != null) yield return ourLoad;
            if (!(ourLoad?.asset is GameObject ourPrefab))
            {
                report.Line("  *** FAILED: bundle / PocRoom did not load. ***");
                Cleanup();
                yield break;
            }

            // Float the arena a few metres up so its floor is a distinct height band, still in-graph.
            var origin = camera.transform.position - Vector3.up * EyeToFootDrop + Vector3.up * IslandHeight;
            _room = UnityEngine.Object.Instantiate(ourPrefab, origin, Quaternion.identity);
            _room.name = "FalseGodsP5d_ApplyIsland";

            var arena = WorldBounds(_room);
            var floorTopY = FloorTopY(_room, origin);
            report.Value("arena world bounds (center / size)", $"{arena.center} / {arena.size}");
            report.Value("floor top height", floorTopY.ToString("F2"));

            var before = FootprintWalkableAtFloor(recast, arena, floorTopY);
            report.Value("[before apply] walkable nodes over footprint at floor height", before);

            // ── Apply the shipped bytes: deserialize, snap to the live graph's tile grid, ReplaceTiles.
            var tiles = TileMeshes.Deserialize(data);
            _prefabHolder = new GameObject("FalseGodsP5d_NavmeshPrefab");
            _prefabHolder.transform.position = new Vector3(arena.center.x, floorTopY + 1.5f, arena.center.z);
            var navPrefab = _prefabHolder.AddComponent<NavmeshPrefab>();
            navPrefab.applyOnStart = false;
            navPrefab.removeTilesWhenDisabled = false;
            navPrefab.bounds = new Bounds(Vector3.zero, new Vector3(arena.size.x + 2f, 4f, arena.size.z + 2f));

            NavmeshPrefab.SnapToGraph(new TileLayout(recast), _prefabHolder.transform.position,
                _prefabHolder.transform.rotation, navPrefab.bounds, out var graphTileRect, out var snappedRotation, out var yOffset);
            tiles.Rotate(snappedRotation);

            var dimsMatch = tiles.tileRect.Width == graphTileRect.Width && tiles.tileRect.Height == graphTileRect.Height;
            report.Value("Apply tile dims (artifact vs graph)",
                $"{tiles.tileRect.Width}x{tiles.tileRect.Height} vs {graphTileRect.Width}x{graphTileRect.Height} — "
                + (dimsMatch ? "match" : "MISMATCH — cellSize/size differ; Apply would throw in the game"));

            if (!dimsMatch)
            {
                report.Line("  Skipped ReplaceTiles: dimensions do not match. (Expected only if the bake and this "
                            + "level differ in cellSize or the arena size changed.)");
                Cleanup();
                yield break;
            }

            tiles.tileRect = graphTileRect;
            var applied = false;
            astar.AddWorkItem((Action)(() => { recast.ReplaceTiles(tiles, yOffset); applied = true; }));
            var guard = 0;
            while (!applied && guard++ < 600) yield return null;
            yield return new WaitForSeconds(SettleSeconds);

            var after = FootprintWalkableAtFloor(recast, arena, floorTopY);
            report.Value("[after apply] walkable nodes over footprint at floor height", after);

            report.Line();
            report.Value("R4 verdict (shipped artifact -> our floor walkable)", after > before
                ? $"WORKS END-TO-END — applying the saved bytes added {after - before} walkable node(s) at our "
                  + "floor height. A baked-once artifact makes our arena floor walkable from a mod. Option 1 is real."
                : $"NO FLOOR — before={before}, after={after}. The shipped bytes did not add walkable floor nodes "
                  + "(read the dims line and the artifact byte count).");

            // ── Teardown: remove exactly the tiles we inserted.
            var cleared = false;
            astar.AddWorkItem((Action)(() => { recast.ClearTiles(graphTileRect); cleared = true; }));
            guard = 0;
            while (!cleared && guard++ < 600) yield return null;
            yield return new WaitForSeconds(SettleSeconds);
            report.Line("  Inserted tiles cleared (ClearTiles over the same rect).");

            Cleanup();
            report.Section("P5d — teardown");
            report.Line("  Island + NavmeshPrefab destroyed, bundle unloaded. A level change rebuilds nav anyway.");
        }

        private static int FootprintWalkableAtFloor(RecastGraph recast, Bounds arena, float floorTopY)
        {
            var minX = arena.center.x - arena.extents.x;
            var maxX = arena.center.x + arena.extents.x;
            var minZ = arena.center.z - arena.extents.z;
            var maxZ = arena.center.z + arena.extents.z;
            var count = 0;
            recast.GetNodes(n =>
            {
                if (!n.Walkable) return;
                var p = (Vector3)n.position;
                if (p.x < minX || p.x > maxX || p.z < minZ || p.z > maxZ) return;
                if (Mathf.Abs(p.y - floorTopY) <= 1f) count++;
            });
            return count;
        }

        private static float FloorTopY(GameObject room, Vector3 fallback)
        {
            var spawn = room.GetComponentsInChildren<Transform>(true).FirstOrDefault(t => t.name == "PlayerSpawn");
            return spawn != null ? spawn.position.y : fallback.y;
        }

        private static Bounds WorldBounds(GameObject go)
        {
            var renderers = go.GetComponentsInChildren<Renderer>(true);
            if (renderers.Length == 0)
                return new Bounds(go.transform.position, new Vector3(24f, 8f, 24f));
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
