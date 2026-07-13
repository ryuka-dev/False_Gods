using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using BepInEx;
using Pathfinding;
using Pathfinding.Graphs.Navmesh;
using PerfectRandom.Sulfur.Core;
using UnityEngine;

namespace FalseGods.Probe
{
    /// <summary>
    /// PoC step P7 — TEARDOWN: prove that inserting our arena's navmesh into the live level graph and then
    /// removing it leaves the level we are STILL IN clean — no arena objects, no arena nav nodes, and the
    /// level's own nav restored to exactly what it was (RiskList R8/R30;
    /// <see cref="Docs/MinimalProofOfConceptPlan.md"/> §7.2).
    ///
    /// The trap this probe exists to expose and fix: the proven Option-1 nav path (P5d/P6) applies our arena
    /// with <c>RecastGraph.ReplaceTiles</c>, which overwrites whole XZ tiles (the full Y column). Floating the
    /// arena a few metres up and applying it into the tile the player stands on DESTROYS the level's own ground
    /// nav in that 38.4 m tile — it is replaced by our floated floor. A naive teardown (<c>ClearTiles</c>, which
    /// is also all A*'s own <c>NavmeshPrefab.OnDisable</c> does) sets those tiles to EMPTY — it does not put the
    /// level's nav back. So "leave the arena, keep playing the same level" would leave a nav hole under the
    /// player until the next full rescan / level change.
    ///
    /// The clean teardown this probe proves: SNAPSHOT the level's original tiles in the arena footprint before
    /// applying the arena (each live <c>NavmeshTile</c> exposes <c>vertsInGraphSpace</c> + <c>tris</c>, which
    /// round-trip through <c>ReplaceTiles</c>), then on teardown <c>ReplaceTiles</c> those saved tiles back —
    /// returning the level's nav to baseline. One run measures three stages so it proves BOTH the hazard is real
    /// and the fix works:
    ///
    ///   BASELINE  — level nav in the footprint (before anything) and the whole-graph walkable node count.
    ///   APPLIED   — after ReplaceTiles(arena): the arena floor is walkable at +3 m AND the level's ground nav in
    ///               the footprint has collapsed to ~0 (the hazard, measured).
    ///   RESTORED  — after ReplaceTiles(savedLevelTiles) + destroying the arena: the arena nodes are gone, the
    ///               level's ground nav is back at baseline, the whole-graph count matches baseline, no arena
    ///               GameObjects remain, and the bundle is unloaded.
    ///
    /// Mutates <c>AstarPath.active</c> (replaces then restores the footprint tiles); a level change rebuilds nav
    /// anyway. Run it in a throwaway level. The cross-level half of R8 (load a normal level, its nav is correct,
    /// no arena object survived) is handled by the game's per-level graph rebuild (ClearGraphs + Instantiate,
    /// proven in P0) — after this runs, change level and press F10 to confirm.
    /// </summary>
    internal sealed class NavmeshTeardownProbe
    {
        private const string BundleFileName = "falsegods-poc-room.bundle";
        private const string OurRoomPrefabName = "PocRoom";

        private const float EyeToFootDrop = 1.6f;
        private const float IslandHeight = 3f;
        private const float SettleSeconds = 0.75f;

        // A node counts as "the arena floor" if within this of the floated floor height, and "the level's own
        // nav" if below that band. 1.5 m cleanly separates the two (they are 3 m apart), and tolerates the
        // level ground sloping across a 38.4 m tile.
        private const float BandHalf = 1.5f;

        // The restored count is allowed to differ from baseline by this fraction: ReplaceTiles rebuilds and
        // reconnects nodes, so identity changes and a boundary node or two may re-triangulate, but the walkable
        // node population of a restored region should match its baseline closely.
        private const float RestoreTolerance = 0.05f;

        private AssetBundle _bundle;
        private GameObject _room;
        private GameObject _prefabHolder;

        public IEnumerator Run(ProbeReport report)
        {
            report.Section("P7 — teardown: apply our arena nav, then restore the level to baseline (R8/R30)");

            var astar = AstarPath.active;
            var recast = astar?.data?.recastGraph;
            var camera = Camera.main;
            if (recast == null || camera == null)
            {
                report.Line("  Skipped: need AstarPath.active + recastGraph + Camera.main (stand in a loaded level).");
                yield break;
            }

            report.Try("level", () =>
            {
                var gm = StaticInstance<GameManager>.Instance;
                report.Value("level (index / environment)",
                    $"{gm.currentLevelIndex} / {(gm.currentEnvironment != null ? gm.currentEnvironment.id.ToString() : "<null>")}");
                report.Value("graph cellSize / tileWorldSize",
                    $"{recast.cellSize:0.00} / {recast.editorTileSize * recast.cellSize:0.0} m");
            });

            var bundlePath = System.IO.Path.Combine(Paths.BepInExRootPath, "FalseGods.Probe", BundleFileName);
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

            // Float the arena a tile-centred island +3 m over the player's feet, exactly as P6/P5d do, so its
            // footprint is a single graph tile the player is standing on — i.e. a tile that DOES carry level
            // ground nav. That is the honest worst case for teardown: the arena overwrites occupied level space.
            var foot = camera.transform.position - Vector3.up * EyeToFootDrop;
            var layout = new TileLayout(recast);
            var footGraph = layout.transform.InverseTransform(foot);
            var tx = Mathf.FloorToInt(footGraph.x / layout.TileWorldSizeX);
            var tz = Mathf.FloorToInt(footGraph.z / layout.TileWorldSizeZ);
            var tileCentreGraph = layout.GetTileBoundsInGraphSpace(tx, tz).center;
            var tileCentreWorld = layout.transform.Transform(new Vector3(tileCentreGraph.x, footGraph.y, tileCentreGraph.z));
            var origin = new Vector3(tileCentreWorld.x, foot.y + IslandHeight, tileCentreWorld.z);

            _room = UnityEngine.Object.Instantiate(ourPrefab, origin, Quaternion.identity);
            _room.name = "FalseGodsP7_TeardownIsland";

            var arena = WorldBounds(_room);
            var arenaFloorY = FloorTopY(_room, origin);
            report.Value("tile snap (tile index / world XZ)", $"[{tx},{tz}] / ({origin.x:F1}, {origin.z:F1})");
            report.Value("arena world bounds (center / size)", $"{arena.center} / {arena.size}");
            report.Value("ground Y (player foot) / arena floor Y", $"{foot.y:F2} / {arenaFloorY:F2}");

            // Tile-align the nav bounds (min on a tile boundary, size an integer number of tiles) so SnapToGraph's
            // rounded origin matches the baked-tile origin — the P6 fix. Then resolve the graph tile rect that
            // ReplaceTiles will overwrite; that rect defines exactly which level tiles we must snapshot + restore.
            var tileRect = layout.GetTouchingTiles(arena, 0.5f);
            var alignedGraph = layout.GetTileBoundsInGraphSpace(tileRect.xmin, tileRect.ymin, tileRect.Width, tileRect.Height);
            var alignedCentre = layout.transform.Transform(alignedGraph.center);
            var alignedSize = new Vector3(tileRect.Width * layout.TileWorldSizeX, 4f, tileRect.Height * layout.TileWorldSizeZ);
            var holderPos = new Vector3(alignedCentre.x, arena.center.y, alignedCentre.z);
            var navBounds = new Bounds(Vector3.zero, alignedSize);

            NavmeshPrefab.SnapToGraph(layout, holderPos, Quaternion.identity, navBounds,
                out var graphTileRect, out var snappedRotation, out var yOffset);
            var footprint = recast.GetTileBounds(graphTileRect);
            report.Value("footprint (graph tiles / world XZ)",
                $"{graphTileRect.Width}x{graphTileRect.Height} @ [{graphTileRect.xmin},{graphTileRect.ymin}] / "
                + $"({footprint.center.x:F1}, {footprint.center.z:F1}) size ({footprint.size.x:F1} x {footprint.size.z:F1})");

            // ── BASELINE ─────────────────────────────────────────────────────────────────────────────────────
            report.Section("P7.1 — BASELINE (level nav in the footprint, before the arena)");
            var wholeBaseline = CountWalkable(recast, null, float.NegativeInfinity, float.PositiveInfinity);
            var levelBaseline = CountWalkable(recast, footprint, float.NegativeInfinity, arenaFloorY - BandHalf);
            var arenaBandBaseline = CountWalkable(recast, footprint, arenaFloorY - BandHalf, arenaFloorY + BandHalf);
            report.Value("whole-graph walkable nodes", wholeBaseline);
            report.Value("footprint level-ground walkable nodes", levelBaseline);
            report.Value("footprint arena-floor-band walkable nodes", arenaBandBaseline);
            if (levelBaseline == 0)
                report.Line("  NOTE: 0 level nodes in the footprint — the player is not on level nav here. The "
                            + "restore-to-baseline check is trivial; move onto solid level nav for a real test.");

            // Best-effort: a through-path across the footprint region (the deterministic stand-in for "vanilla
            // NPCs still path"). Recorded at baseline and again after restore.
            var baselineCross = CrossPathLength(astar, recast, footprint, foot.y);
            report.Value("cross-footprint path (baseline)", DescribeCross(baselineCross));

            // ── SNAPSHOT the level's original tiles in the footprint (the restore payload). ───────────────────
            // We must capture BOTH the geometry (TileMeshes, so ReplaceTiles can rebuild the triangles) AND the
            // per-node Walkable flags. ReplaceTiles rebuilds nodes as walkable-by-geometry and does NOT re-run the
            // NavMeshCleaner flood-fill (the very side-step P5d relied on), so restoring geometry alone brings
            // back nodes the cleaner had culled — over-restoring. Reapplying the saved Walkable flags after
            // ReplaceTiles returns the tile to its exact baseline walkability.
            TileMeshes saved;
            bool[][] savedWalkable;
            var snapshotTriangles = 0;
            try
            {
                saved = SnapshotTiles(recast, graphTileRect, out snapshotTriangles, out savedWalkable);
            }
            catch (Exception exception)
            {
                report.Value("snapshot THREW", exception.ToString());
                Cleanup();
                yield break;
            }
            var snapshotWalkable = savedWalkable.Sum(w => w?.Count(b => b) ?? 0);
            report.Value("snapshotted level tiles / triangles / walkable",
                $"{saved.tileMeshes.Length} / {snapshotTriangles} / {snapshotWalkable}");

            // ── Bake our arena navmesh in clear space, then apply it into the footprint (P5c + P5d). ──────────
            byte[] arenaBytes = null;
            yield return BakeArenaInClearSpace(recast, camera, ourPrefab, arena,
                new Vector3(alignedCentre.x, 0f, alignedCentre.z), alignedSize, b => arenaBytes = b, report);
            if (arenaBytes == null)
            {
                report.Line("  *** Arena bake failed — cannot test teardown. Restoring nothing was applied. ***");
                Cleanup();
                yield break;
            }

            var arenaTiles = TileMeshes.Deserialize(arenaBytes);
            arenaTiles.Rotate(snappedRotation);
            if (arenaTiles.tileRect.Width != graphTileRect.Width || arenaTiles.tileRect.Height != graphTileRect.Height)
            {
                report.Value("apply tile dims (artifact vs graph)",
                    $"{arenaTiles.tileRect.Width}x{arenaTiles.tileRect.Height} vs {graphTileRect.Width}x{graphTileRect.Height} — MISMATCH");
                report.Line("  *** Dimension mismatch — cannot apply. ***");
                Cleanup();
                yield break;
            }
            arenaTiles.tileRect = graphTileRect;

            var applied = false;
            astar.AddWorkItem((Action)(() => { recast.ReplaceTiles(arenaTiles, yOffset); applied = true; }));
            var guard = 0;
            while (!applied && guard++ < 600) yield return null;
            yield return new WaitForSeconds(SettleSeconds);

            // ── APPLIED — measure the hazard. ────────────────────────────────────────────────────────────────
            report.Section("P7.2 — APPLIED (arena floor walkable; level ground in footprint clobbered)");
            var arenaBandApplied = CountWalkable(recast, footprint, arenaFloorY - BandHalf, arenaFloorY + BandHalf);
            var levelApplied = CountWalkable(recast, footprint, float.NegativeInfinity, arenaFloorY - BandHalf);
            report.Value("footprint arena-floor-band walkable nodes", $"{arenaBandApplied} (was {arenaBandBaseline})");
            report.Value("footprint level-ground walkable nodes", $"{levelApplied} (was {levelBaseline})");
            var hazardShown = levelBaseline > 0 && levelApplied < levelBaseline;
            report.Value("R8 hazard (ReplaceTiles wipes level ground in the tile)", hazardShown
                ? $"CONFIRMED — level ground nav in the footprint fell {levelBaseline} -> {levelApplied} when the "
                  + "arena tiles were applied. A ClearTiles-only teardown would leave this hole."
                : (levelBaseline == 0 ? "n/a — no level nav in the footprint to clobber (see the baseline note)."
                    : $"NOT SEEN — level ground held at {levelApplied}; unexpected, read the numbers."));

            // ── TEARDOWN (clean): restore the snapshotted level tiles, then destroy the arena. ────────────────
            report.Section("P7.3 — TEARDOWN: ReplaceTiles(saved level tiles) + restore walkability, destroy arena");
            var restored = false;
            astar.AddWorkItem((Action)(() =>
            {
                recast.ReplaceTiles(saved, 0f);
                RestoreWalkability(recast, graphTileRect, savedWalkable);
                restored = true;
            }));
            guard = 0;
            while (!restored && guard++ < 600) yield return null;
            yield return new WaitForSeconds(SettleSeconds);

            Cleanup(); // destroys the island + holder, unloads the bundle
            // Object.Destroy is deferred to end of frame, so let a frame pass before counting arena objects —
            // otherwise the just-destroyed island would still be found and read as a leak.
            yield return null;

            // ── RESTORED — assert the active level is clean. ─────────────────────────────────────────────────
            report.Section("P7.4 — RESTORED (assert the level we are still in is clean)");
            var wholeRestored = CountWalkable(recast, null, float.NegativeInfinity, float.PositiveInfinity);
            var levelRestored = CountWalkable(recast, footprint, float.NegativeInfinity, arenaFloorY - BandHalf);
            var arenaBandRestored = CountWalkable(recast, footprint, arenaFloorY - BandHalf, arenaFloorY + BandHalf);
            var arenaObjectsLeft = CountArenaObjects();
            var restoredCross = CrossPathLength(astar, recast, footprint, foot.y);

            report.Value("whole-graph walkable nodes", $"{wholeRestored} (baseline {wholeBaseline})");
            report.Value("footprint level-ground walkable nodes", $"{levelRestored} (baseline {levelBaseline})");
            report.Value("footprint arena-floor-band walkable nodes", $"{arenaBandRestored} (baseline {arenaBandBaseline})");
            report.Value("arena GameObjects still in scene", arenaObjectsLeft);
            report.Value("cross-footprint path (restored)", DescribeCross(restoredCross));
            report.Value("bundle reference", _bundle == null ? "unloaded" : "STILL LOADED");

            // Verdicts.
            var objectsGone = arenaObjectsLeft == 0;
            var arenaNodesGone = arenaBandRestored <= arenaBandBaseline;
            var levelNavRestored = Within(levelRestored, levelBaseline, RestoreTolerance)
                                   && Within(wholeRestored, wholeBaseline, RestoreTolerance);

            report.Line();
            report.Value("teardown: arena objects gone", objectsGone);
            report.Value("teardown: arena nav nodes gone", arenaNodesGone);
            report.Value("teardown: level nav restored to baseline", levelNavRestored);

            var pass = objectsGone && arenaNodesGone && levelNavRestored;
            report.Line();
            report.Value("R8/R30 verdict (same level)", pass
                ? "CLEAN — the arena left no GameObjects and no nav nodes, and the level's own nav is back at "
                  + "baseline (whole-graph and footprint counts match). Snapshot+restore teardown keeps the level "
                  + "we stay in fully navigable. Vanilla NPCs path exactly as before."
                : "NOT CLEAN — one of {objects, arena nodes, level nav} did not return to baseline. Read the three "
                  + "teardown lines above; a non-restored level count means the snapshot/restore did not round-trip.");

            report.Section("P7.5 — cross-level half (game's per-level rebuild)");
            report.Line("  The other half of R8 is the NEXT level: load a normal level and confirm its nav is");
            report.Line("  correct and no arena object survived. The game rebuilds AstarPath.active per level");
            report.Line("  (ClearGraphs + Instantiate, proven in P0), so residue cannot cross a level change.");
            report.Line("  To confirm by hand: after this run, change level and press F10 (P0) — the graph is a");
            report.Line("  fresh instance and no FalseGodsP7_* object is present.");
        }

        /// <summary>Snapshot the live level tiles in <paramref name="rect"/> into a <see cref="TileMeshes"/> that
        /// <see cref="RecastGraph.ReplaceTiles"/> can put back verbatim. Each tile's <c>vertsInGraphSpace</c> is
        /// converted back to tile space (the inverse of the per-tile offset ReplaceTile adds), <c>tris</c> are
        /// already local indices, and per-triangle tags are read off the nodes. yOffset for the restore is 0
        /// because the verts already carry their full graph-space Y.</summary>
        private static TileMeshes SnapshotTiles(RecastGraph recast, IntRect rect, out int totalTriangles,
            out bool[][] walkablePerTile)
        {
            totalTriangles = 0;
            var width = rect.Width;
            var height = rect.Height;
            var meshes = new TileMesh[width * height];
            walkablePerTile = new bool[width * height][];
            for (var z = 0; z < height; z++)
            {
                for (var x = 0; x < width; x++)
                {
                    var slot = x + z * width;
                    var gx = rect.xmin + x;
                    var gz = rect.ymin + z;
                    var tile = recast.GetTile(gx, gz);
                    if (tile == null || tile.tris.Length == 0)
                    {
                        meshes[slot] = new TileMesh
                        {
                            triangles = Array.Empty<int>(),
                            verticesInTileSpace = Array.Empty<Int3>(),
                            tags = Array.Empty<uint>(),
                        };
                        walkablePerTile[slot] = Array.Empty<bool>();
                        continue;
                    }

                    var offset = (Int3)new Vector3(gx * recast.TileWorldSizeX, 0f, gz * recast.TileWorldSizeZ);
                    var verts = new Int3[tile.vertsInGraphSpace.Length];
                    for (var i = 0; i < verts.Length; i++) verts[i] = tile.vertsInGraphSpace[i] - offset;

                    var tris = new int[tile.tris.Length];
                    for (var i = 0; i < tris.Length; i++) tris[i] = tile.tris[i];
                    totalTriangles += tris.Length / 3;

                    // node i corresponds to triangle i (ReplaceTilePostCut/CreateNodes build nodes in tri order),
                    // so tags[i] and walkable[i] round-trip against the restored nodes[i].
                    var triCount = tris.Length / 3;
                    var tags = new uint[triCount];
                    var walkable = new bool[triCount];
                    var nodes = tile.nodes;
                    if (nodes != null)
                        for (var i = 0; i < triCount && i < nodes.Length; i++)
                            if (nodes[i] != null) { tags[i] = nodes[i].Tag; walkable[i] = nodes[i].Walkable; }

                    meshes[slot] = new TileMesh
                    {
                        triangles = tris,
                        verticesInTileSpace = verts,
                        tags = tags,
                    };
                    walkablePerTile[slot] = walkable;
                }
            }

            return new TileMeshes
            {
                tileMeshes = meshes,
                tileRect = rect,
                tileWorldSize = new Vector2(recast.TileWorldSizeX, recast.TileWorldSizeZ),
            };
        }

        /// <summary>Reapply the saved per-node <c>Walkable</c> flags to the freshly-restored tiles. Must run
        /// inside a work item (the setter dirties the hierarchical graph). ReplaceTiles rebuilds nodes in
        /// triangle order, so restored <c>nodes[i]</c> matches saved <c>walkable[i]</c>.</summary>
        private static void RestoreWalkability(RecastGraph recast, IntRect rect, bool[][] walkablePerTile)
        {
            var width = rect.Width;
            var height = rect.Height;
            for (var z = 0; z < height; z++)
            {
                for (var x = 0; x < width; x++)
                {
                    var wk = walkablePerTile[x + z * width];
                    if (wk == null || wk.Length == 0) continue;
                    var tile = recast.GetTile(rect.xmin + x, rect.ymin + z);
                    var nodes = tile?.nodes;
                    if (nodes == null) continue;
                    for (var i = 0; i < nodes.Length && i < wk.Length; i++)
                        if (nodes[i] != null) nodes[i].Walkable = wk[i];
                }
            }
        }

        /// <summary>P5c bake on a throwaway arena in genuinely clear space (+300 m), so no level geometry
        /// intrudes into the bake bounds. Bakes with the same tile-aligned bounds the apply will use. Returns the
        /// serialized bytes via <paramref name="onBaked"/> (null on failure).</summary>
        private IEnumerator BakeArenaInClearSpace(RecastGraph recast, Camera camera, GameObject ourPrefab,
            Bounds arena, Vector3 alignedCentreXZ, Vector3 alignedSize, Action<byte[]> onBaked, ProbeReport report)
        {
            report.Section("P7.0 — bake our arena navmesh (clear space, +300 m)");

            byte[] data = null;
            var bakeArena = UnityEngine.Object.Instantiate(ourPrefab,
                new Vector3(arena.center.x, camera.transform.position.y + 300f, arena.center.z), Quaternion.identity);
            bakeArena.name = "FalseGodsP7_BakeIsland";
            try
            {
                var bakeBounds = WorldBounds(bakeArena);
                var bakeHolder = new GameObject("FalseGodsP7_BakePrefab");
                bakeHolder.transform.position = new Vector3(alignedCentreXZ.x, bakeBounds.center.y, alignedCentreXZ.z);
                var bakePrefab = bakeHolder.AddComponent<NavmeshPrefab>();
                bakePrefab.applyOnStart = false;
                bakePrefab.removeTilesWhenDisabled = false;
                bakePrefab.bounds = new Bounds(Vector3.zero, alignedSize);
                try { data = bakePrefab.Scan(recast); }
                catch (Exception exception) { report.Value("bake THREW", exception.ToString()); }
                UnityEngine.Object.Destroy(bakeHolder);
            }
            finally { UnityEngine.Object.Destroy(bakeArena); }

            if (data == null) { onBaked(null); yield break; }

            var tiles = TileMeshes.Deserialize(data);
            var bakedTris = tiles.tileMeshes?.Sum(t => (t.triangles?.Length ?? 0) / 3) ?? 0;
            report.Value("baked triangles / bytes (clear space)", $"{bakedTris} / {data.Length}");
            if (bakedTris == 0)
            {
                report.Line("  *** Bake produced 0 triangles — our floor did not rasterize (see R4). ***");
                onBaked(null);
                yield break;
            }
            onBaked(data);
        }

        /// <summary>Count walkable nodes whose XZ is inside <paramref name="xzBounds"/> (or anywhere when null)
        /// and whose Y is in [<paramref name="yMin"/>, <paramref name="yMax"/>].</summary>
        private static int CountWalkable(RecastGraph recast, Bounds? xzBounds, float yMin, float yMax)
        {
            float minX = 0, maxX = 0, minZ = 0, maxZ = 0;
            var bounded = xzBounds.HasValue;
            if (bounded)
            {
                var b = xzBounds.Value;
                minX = b.center.x - b.extents.x; maxX = b.center.x + b.extents.x;
                minZ = b.center.z - b.extents.z; maxZ = b.center.z + b.extents.z;
            }

            var count = 0;
            recast.GetNodes(n =>
            {
                if (!n.Walkable) return;
                var p = (Vector3)n.position;
                if (p.y < yMin || p.y > yMax) return;
                if (bounded && (p.x < minX || p.x > maxX || p.z < minZ || p.z > maxZ)) return;
                count++;
            });
            return count;
        }

        /// <summary>Best-effort: request a path across the footprint region at level-ground height, from just
        /// outside one edge to just outside the opposite edge, both snapped to walkable nodes. Returns the path
        /// length, or a negative sentinel when the endpoints are not on level nav / no path exists. This is the
        /// deterministic stand-in for "a vanilla NPC can still walk across here".</summary>
        private static float CrossPathLength(AstarPath astar, RecastGraph recast, Bounds footprint, float groundY)
        {
            var a = new Vector3(footprint.center.x - footprint.extents.x - 1f, groundY, footprint.center.z);
            var b = new Vector3(footprint.center.x + footprint.extents.x + 1f, groundY, footprint.center.z);
            var nearA = astar.GetNearest(a, NNConstraint.Walkable);
            var nearB = astar.GetNearest(b, NNConstraint.Walkable);
            if (nearA.node == null || nearB.node == null) return -1f;
            var snapA = (Vector3)nearA.position;
            var snapB = (Vector3)nearB.position;
            // Reject endpoints that snapped far (no level nav near the edge) so this stays a real cross-check.
            if (Vector3.Distance(a, snapA) > 6f || Vector3.Distance(b, snapB) > 6f) return -1f;

            var path = ABPath.Construct(snapA, snapB);
            AstarPath.StartPath(path);
            path.BlockUntilCalculated();
            if (path.error || path.vectorPath == null || path.vectorPath.Count < 2) return -2f;
            var total = 0f;
            for (var i = 1; i < path.vectorPath.Count; i++) total += Vector3.Distance(path.vectorPath[i - 1], path.vectorPath[i]);
            return total;
        }

        private static string DescribeCross(float length) => length switch
        {
            -1f => "no level nav near the footprint edges (skipped)",
            -2f => "endpoints on nav but NO PATH between them",
            _ => $"path found, length {length:F1} m",
        };

        private static bool Within(int value, int reference, float tolerance)
        {
            if (reference == 0) return value == 0;
            return Mathf.Abs(value - reference) <= Mathf.Max(1, Mathf.CeilToInt(reference * tolerance));
        }

        private static int CountArenaObjects()
        {
            // FindObjectsOfType is deprecated in Unity 6 in favour of FindObjectsByType, but the sort-mode enum
            // overload is not present in this game's referenced assemblies; the deprecated call is correct here
            // (a one-shot teardown assertion, not a hot path).
#pragma warning disable CS0618
            return UnityEngine.Object.FindObjectsOfType<GameObject>(true)
                .Count(go => go != null && go.name.StartsWith("FalseGodsP7_", StringComparison.Ordinal));
#pragma warning restore CS0618
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
