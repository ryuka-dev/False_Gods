using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using BepInEx;
using Pathfinding;
using Pathfinding.Graphs.Navmesh;
using PerfectRandom.Sulfur.Core;
using PerfectRandom.Sulfur.Core.Units;
using UnityEngine;

namespace FalseGods.Probe
{
    /// <summary>
    /// PoC step P6 — prove A* pathing works on OUR applied arena geometry: a path routes around the central
    /// pillar, and a real vanilla enemy follows such a path (RiskList R9;
    /// <see cref="Docs/MinimalProofOfConceptPlan.md"/> §7.2, §4.5).
    ///
    /// Built on the P5c/P5d result (R4): our floor only becomes walkable when a baked navmesh is applied via
    /// <c>ReplaceTiles</c>. So this probe first re-runs that pipeline in memory (bake with
    /// <c>NavmeshPrefab.Scan</c>, then apply with <c>SnapToGraph</c> + <c>ReplaceTiles</c>, exactly P5c+P5d),
    /// on the arena floated a few metres up so it is an ISOLATED nav island — no level nav shares that height
    /// band, so anything that paths there is pathing on OUR geometry, not the level's (the P5b false-positive
    /// trap). Then it runs two layers, in order of robustness:
    ///
    ///   Layer 1 — NAV-GRAPH PROOF (deterministic, judged from the report). Snap the arena's EnemySpawn (7,7)
    ///   and PlayerSpawn (-7,-7) markers to walkable nodes and request an <c>ABPath</c> between them. Their
    ///   straight line passes through the central pillar (a 2x2 box at the origin). If our bake carved the
    ///   pillar out as a nav hole, the path must ROUTE AROUND it: it completes, and its closest approach to
    ///   the pillar centre stays outside the pillar footprint. If the bake produced a solid floor (no hole),
    ///   the path is a straight segment whose closest approach is ~0 — the decisive discriminator.
    ///
    ///   Layer 2 — LIVE ENEMY (visible confirmation, best-effort). Load a real vanilla enemy prefab by
    ///   <see cref="UnitId"/> (P1 proved Addressables resolution works), instantiate it at the EnemySpawn
    ///   corner, register + activate it the way <c>SpawnEnemiesNode.CreateAndRegisterEnemy</c> does, then drive
    ///   it toward the PlayerSpawn corner with the game's own scripted-movement handle
    ///   (<c>Npc.SetForcedDestination</c> + <c>AiAgent.SetDestination</c>, which turns the combat behaviour tree
    ///   off). Because the island is isolated, it can only path on our floor, so watching it thread past the
    ///   pillar is the on-foot version of Layer 1. Targeting a fixed floor corner (not the live player) keeps
    ///   this a PATHING test; enemy activation/targeting for a remote player is a separate concern (R10/P9).
    ///
    /// Mutates <c>AstarPath.active</c> (adds tiles, spawns one NPC), then removes exactly what it added
    /// (<c>ClearTiles</c> over the same rect, unregister + destroy the NPC); a level change rebuilds nav
    /// anyway. Run it in a throwaway level.
    /// </summary>
    internal sealed class NavmeshEnemyProbe
    {
        private const string BundleFileName = "falsegods-poc-room.bundle";
        private const string OurRoomPrefabName = "PocRoom";

        private const float EyeToFootDrop = 1.6f;
        private const float IslandHeight = 3f;
        private const float SettleSeconds = 0.75f;

        // PocRoomGenerator geometry (metres): a 2x2 pillar centred at the origin. Its footprint half-extent is
        // 1.0; the recast agent radius (0.5) inflates the nav hole further, so a path routing around it keeps
        // its closest approach to the pillar centre at ~1.0 m or more. A straight-through path (bake failed to
        // carve the hole) has a closest approach of ~0. We call it "around" at >= this threshold, set a little
        // under the 1.0 half-extent to tolerate node quantisation at cellSize 0.3.
        private const float PillarHalfExtent = 1.0f;
        private const float RoutesAroundThreshold = 0.85f;

        // Layer 2 driving: how long to push the enemy toward the far corner, and the progress bar for "it moved".
        private const float EnemyDriveSeconds = 10f;
        // A coarse recast agent stops ~1 characterRadius + endReachedDistance short; the far corner also snaps a
        // few metres in on a big-triangle navmesh. Treat "within ~3.5 m of B" as reached.
        private const float ReachedDistance = 3.5f;

        private readonly string _enemyUnitIdName;

        private AssetBundle _bundle;
        private GameObject _room;
        private GameObject _prefabHolder;
        private GameObject _enemy;
        private Npc _enemyNpc;

        public NavmeshEnemyProbe(string enemyUnitIdName) => _enemyUnitIdName = enemyUnitIdName;

        public IEnumerator Run(ProbeReport report)
        {
            report.Section("P6 — A* pathing on our arena: route around the pillar + live enemy (R9)");

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

            // Float the arena so its floor is a distinct, isolated nav band (still in-graph for ReplaceTiles),
            // and snap its XZ onto a graph TILE CENTRE. ReplaceTiles works a whole tile at a time (128 voxels *
            // 0.3 = 38.4 m here); if our 20 m arena straddles a tile boundary, a single-tile apply covers only
            // half of it and a far corner has no walkable node (measured: A or B snapped to Infinity, flaky
            // run-to-run with where the player stood). Centring the arena in one tile leaves ~9 m of margin on
            // every side, so the whole floor lands in that tile every time.
            var foot = camera.transform.position - Vector3.up * EyeToFootDrop;
            var layout = new TileLayout(recast);
            var footGraph = layout.transform.InverseTransform(foot);
            var tx = Mathf.FloorToInt(footGraph.x / layout.TileWorldSizeX);
            var tz = Mathf.FloorToInt(footGraph.z / layout.TileWorldSizeZ);
            var tileCentreGraph = layout.GetTileBoundsInGraphSpace(tx, tz).center;
            var tileCentreWorld = layout.transform.Transform(new Vector3(tileCentreGraph.x, footGraph.y, tileCentreGraph.z));
            var origin = new Vector3(tileCentreWorld.x, foot.y + IslandHeight, tileCentreWorld.z);
            report.Value("tile snap (tile index / world XZ)", $"[{tx},{tz}] / ({origin.x:F1}, {origin.z:F1})");

            _room = UnityEngine.Object.Instantiate(ourPrefab, origin, Quaternion.identity);
            _room.name = "FalseGodsP6_EnemyIsland";

            var arena = WorldBounds(_room);
            var spawnA = MarkerWorld(_room, "EnemySpawn", new Vector3(7f, 0f, 7f) + origin);   // enemy start
            var targetB = MarkerWorld(_room, "PlayerSpawn", new Vector3(-7f, 0f, -7f) + origin); // far corner
            var pillar = PillarWorldCentre(_room, origin);
            report.Value("arena world bounds (center / size)", $"{arena.center} / {arena.size}");
            report.Value("enemy start A (EnemySpawn)", spawnA.ToString("F2"));
            report.Value("target  B (PlayerSpawn)", targetB.ToString("F2"));
            report.Value("pillar centre (XZ)", $"({pillar.x:F2}, {pillar.z:F2})");

            // Tile-ALIGN the nav bounds. SnapToGraph rounds the bounds-min to the nearest tile boundary and
            // ScanAsync lays baked tiles out from the *true* bounds-min; if the bounds are arena-sized and
            // centred in a big tile, those two origins differ by the ~8 m margin and ReplaceTiles places our
            // floor shifted, leaving a far corner uncovered (measured on the coarse-tile boss levels). Using a
            // bounds that spans exactly the tiles the arena touches (min on a tile boundary, size an integer
            // number of tiles) makes both origins identical, so the floor lands where it actually is.
            var tileRect = layout.GetTouchingTiles(arena, 0.5f);
            var alignedGraph = layout.GetTileBoundsInGraphSpace(tileRect.xmin, tileRect.ymin, tileRect.Width, tileRect.Height);
            var alignedCentre = layout.transform.Transform(alignedGraph.center);
            var alignedSize = new Vector3(tileRect.Width * layout.TileWorldSizeX, 4f, tileRect.Height * layout.TileWorldSizeZ);
            report.Value("aligned nav bounds (tiles / world XZ)", $"{tileRect.Width}x{tileRect.Height} / ({alignedCentre.x:F1}, {alignedCentre.z:F1})");

            // ── Bake our arena navmesh in memory and apply it (P5c bake + P5d apply, combined). ──────────────
            var graphTileRect = default(IntRect);
            var applied = false;
            yield return BakeAndApply(astar, recast, camera, ourPrefab, arena,
                new Vector3(alignedCentre.x, 0f, alignedCentre.z), alignedSize,
                r => { graphTileRect = r; applied = true; }, report);
            if (!applied)
            {
                report.Line("  *** Nav apply failed — cannot test pathing. See the apply lines above. ***");
                Cleanup();
                yield break;
            }

            // ── Layer 1: the nav-graph proof. ────────────────────────────────────────────────────────────
            yield return NavGraphProof(astar, recast, spawnA, targetB, pillar, report);

            // ── Layer 2: the live enemy. Best-effort; Layer 1 already stands as the result. ────────────────
            yield return LiveEnemy(spawnA, targetB, pillar, report);

            // ── Teardown: remove exactly what we added. ────────────────────────────────────────────────────
            DespawnEnemy();
            var cleared = false;
            astar.AddWorkItem((Action)(() => { recast.ClearTiles(graphTileRect); cleared = true; }));
            var guard = 0;
            while (!cleared && guard++ < 600) yield return null;
            yield return new WaitForSeconds(SettleSeconds);

            Cleanup();
            report.Section("P6 — teardown");
            report.Line("  Enemy unregistered + destroyed, inserted tiles cleared, island destroyed, bundle unloaded.");
            report.Line("  A level change rebuilds nav anyway.");
        }

        /// <summary>P5c bake (NavmeshPrefab.Scan) + P5d apply (SnapToGraph + ReplaceTiles), in memory. The bake is
        /// done on a THROWAWAY copy of the arena spawned in genuinely clear space (+300 m), because baking at the
        /// floated apply height let nearby level geometry intrude into the bake bounds and corrupt it (a run
        /// baked only 24 of ~40 triangles, leaving half the floor unwalkable). The clean bytes are then applied
        /// at the floated test-arena location, exactly as P5c bakes and P5d applies. Calls <paramref
        /// name="onApplied"/> with the graph tile rect (for teardown) only when tiles were actually replaced.</summary>
        private IEnumerator BakeAndApply(AstarPath astar, RecastGraph recast, Camera camera, GameObject ourPrefab,
            Bounds arena, Vector3 alignedCentreXZ, Vector3 alignedSize, Action<IntRect> onApplied, ProbeReport report)
        {
            report.Section("P6.0 — bake (clear space, +300 m) + apply at the floated arena (P5c + P5d)");

            // ── Bake on a throwaway arena, far above all level geometry, so nothing pollutes the bake. ──────
            // Bake with the SAME tile-aligned bounds (centre XZ + size) the apply will use, only far up in Y —
            // so the baked tile grid matches the tiles ReplaceTiles overwrites and there is no origin shift.
            byte[] data = null;
            var bakeArena = UnityEngine.Object.Instantiate(ourPrefab,
                new Vector3(arena.center.x, camera.transform.position.y + 300f, arena.center.z), Quaternion.identity);
            bakeArena.name = "FalseGodsP6_BakeIsland";
            try
            {
                var bakeBounds = WorldBounds(bakeArena);
                var bakeHolder = new GameObject("FalseGodsP6_BakePrefab");
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
            if (data == null) yield break;

            var tiles = TileMeshes.Deserialize(data);
            var bakedTris = tiles.tileMeshes?.Sum(t => (t.triangles?.Length ?? 0) / 3) ?? 0;
            report.Value("baked triangles / bytes (clear space)", $"{bakedTris} / {data.Length}");
            if (bakedTris == 0)
            {
                report.Line("  *** Bake produced 0 triangles — our floor did not rasterize (see R4). ***");
                yield break;
            }

            // ── Apply the clean bytes onto the graph at the floated test-arena location (tile-aligned bounds). ─
            _prefabHolder = new GameObject("FalseGodsP6_NavmeshPrefab");
            _prefabHolder.transform.position = new Vector3(alignedCentreXZ.x, arena.center.y, alignedCentreXZ.z);
            var navPrefab = _prefabHolder.AddComponent<NavmeshPrefab>();
            navPrefab.applyOnStart = false;
            navPrefab.removeTilesWhenDisabled = false;
            navPrefab.bounds = new Bounds(Vector3.zero, alignedSize);

            NavmeshPrefab.SnapToGraph(new TileLayout(recast), _prefabHolder.transform.position,
                _prefabHolder.transform.rotation, navPrefab.bounds,
                out var graphTileRect, out var snappedRotation, out var yOffset);
            tiles.Rotate(snappedRotation);

            var dimsMatch = tiles.tileRect.Width == graphTileRect.Width && tiles.tileRect.Height == graphTileRect.Height;
            report.Value("apply tile dims (artifact vs graph)",
                $"{tiles.tileRect.Width}x{tiles.tileRect.Height} vs {graphTileRect.Width}x{graphTileRect.Height} — "
                + (dimsMatch ? "match" : "MISMATCH"));
            if (!dimsMatch)
            {
                report.Line("  Skipped ReplaceTiles: tile dimensions do not match.");
                yield break;
            }

            tiles.tileRect = graphTileRect;
            var done = false;
            astar.AddWorkItem((Action)(() => { recast.ReplaceTiles(tiles, yOffset); done = true; }));
            var guard = 0;
            while (!done && guard++ < 600) yield return null;
            yield return new WaitForSeconds(SettleSeconds);
            report.Line("  Applied our clear-space baked tiles into the live graph at the floated arena (ReplaceTiles).");
            onApplied(graphTileRect);
        }

        /// <summary>Layer 1: request an ABPath across the pillar and prove it routes around, not through.</summary>
        private IEnumerator NavGraphProof(AstarPath astar, RecastGraph recast, Vector3 a, Vector3 b,
            Vector3 pillar, ProbeReport report)
        {
            report.Section("P6.1 — nav-graph proof: ABPath must route AROUND the pillar");

            var nearA = astar.GetNearest(a, NNConstraint.Walkable);
            var nearB = astar.GetNearest(b, NNConstraint.Walkable);
            var snapA = (Vector3)nearA.position;
            var snapB = (Vector3)nearB.position;
            report.Value("A snapped to walkable node", $"{snapA:F2} (from {a:F2}, {Vector3.Distance(a, snapA):F2} m away)");
            report.Value("B snapped to walkable node", $"{snapB:F2} (from {b:F2}, {Vector3.Distance(b, snapB):F2} m away)");
            // A coarse navmesh (few big triangles) can put the nearest node centroid a few metres from the exact
            // corner even where the floor is walkable, so the discriminator for "no floor" is Infinity / a large
            // gap, not a metre or two. 6 m cleanly separates "on our floor" from "no node at all".
            if (nearA.node == null || nearB.node == null || Vector3.Distance(a, snapA) > 6f || Vector3.Distance(b, snapB) > 6f)
            {
                report.Line("  *** No walkable node near a corner — our floor is not walkable here. Pathing untestable. ***");
                yield break;
            }

            var path = ABPath.Construct(snapA, snapB);
            AstarPath.StartPath(path);
            var guard = 0;
            while (!path.IsDone() && guard++ < 600) yield return null;

            report.Value("path CompleteState", path.CompleteState);
            if (path.error || path.vectorPath == null || path.vectorPath.Count < 2)
            {
                report.Value("path error", path.errorLog);
                report.Line("  *** No path returned between the corners. ***");
                yield break;
            }

            var waypoints = path.vectorPath;
            var pathLength = PolylineLength(waypoints);
            var straight = Vector3.Distance(snapA, snapB);
            var closest = ClosestApproachXZ(waypoints, pillar);
            report.Value("waypoints", waypoints.Count);
            report.Value("path length / straight-line", $"{pathLength:F2} m / {straight:F2} m");
            report.Value("closest approach to pillar centre (XZ)", $"{closest:F2} m (footprint half = {PillarHalfExtent:F2})");

            var routesAround = closest >= RoutesAroundThreshold;
            report.Line();
            report.Value("R9 verdict (nav-graph)", routesAround
                ? $"ROUTES AROUND — the path stays {closest:F2} m from the pillar centre (outside the "
                  + $"{PillarHalfExtent:F2} m footprint), so our applied nav has the pillar as a hole and A* threads "
                  + "past it on our floor. A* pathing works on our geometry."
                : $"THROUGH THE PILLAR — closest approach {closest:F2} m ~ 0, so the path crosses where the pillar "
                  + "is. Either the bake did not carve the hole or the wrong tiles applied (read the bake lines).");
        }

        /// <summary>Layer 2: spawn a real vanilla enemy, register + activate it, and drive it toward the far
        /// corner past the pillar. Best-effort and self-contained; failures are reported, never thrown.</summary>
        private IEnumerator LiveEnemy(Vector3 a, Vector3 b, Vector3 pillar, ProbeReport report)
        {
            report.Section($"P6.2 — live enemy '{_enemyUnitIdName}' paths past the pillar");

            UnitSO unitSo = null;
            report.Try("resolve UnitId", () => unitSo = ResolveUnit(_enemyUnitIdName));
            if (unitSo == null)
            {
                report.Line($"  Skipped: could not resolve UnitId '{_enemyUnitIdName}'. "
                            + "Set Probe/EnemyUnitId to a UnitIds field name (e.g. HellshrewSticka). Layer 1 stands.");
                yield break;
            }

            var handle = unitSo.FetchAndLoadUnitLoader(); // cached on the UnitSO (as the game does); do not release
            var guard = 0;
            while (!handle.IsDone && guard++ < 600) yield return null;
            if (handle.Result == null)
            {
                report.Line("  Skipped: enemy prefab did not load. Layer 1 stands.");
                yield break;
            }

            // Instantiate ACTIVE and configure in the game's own order (SpawnEnemiesNode.CreateAndRegisterEnemy):
            // the prefab must run Awake BEFORE Spawn(), or AiAgent.navMeshAgent is still null and Spawn() skips the
            // canMove / nav setup — which left the enemy inert on the first run (it never entered nav-movement
            // mode). SetStats runs synchronously before the end-of-frame Start() that reads Owner.Stats.
            _enemy = UnityEngine.Object.Instantiate(handle.Result, a + Vector3.up * 0.1f, Quaternion.identity);
            _enemy.name = "FalseGodsP6_Enemy";
            var unit = _enemy.GetComponent<Unit>();
            _enemyNpc = _enemy.GetComponent<Npc>();
            if (unit == null || _enemyNpc == null || _enemyNpc.AiAgent == null)
            {
                report.Line("  Skipped: prefab has no Unit/Npc/AiAgent. Pick a normal enemy UnitId. Layer 1 stands.");
                DespawnEnemy();
                yield break;
            }

            report.Try("SetStats + Spawn", () =>
            {
                unit.SetStats(unit.unitSO);
                unit.Spawn();
            });

            StaticInstance<GameManager>.Instance.npcs.Add(_enemyNpc);
            _enemyNpc.excludeFromNpcLOD = true; // keep the LOD manager from deactivating our test enemy

            yield return null; // let Start() run (reads Owner.Stats, sets playerObject / masks)

            // Force the physics -> nav handoff the way the game does when an NPC starts moving. The FixedUpdate
            // state machine (Npc.UpdatePhysicsEnabling / UpdateNavMeshEnabling) only enables the RichAI and
            // updatePosition once the NPC is switched into nav mode; without this it sits in neither mode and
            // does not move. SetNavAndDisablePhysics makes the Rigidbody kinematic, sets NavMeshEnabledTarget,
            // and the next FixedUpdate enables the RichAI + updatePosition/updateRotation.
            var richAi = _enemyNpc.AiAgent.navMeshAgent;
            if (richAi != null) richAi.canMove = true;
            _enemyNpc.SetForcedDestination(b);   // behaviour tree OFF; ActivateBehaviour paths to b
            report.Try("ActivateBehaviour", () => _enemyNpc.ActivateBehaviour());
            _enemyNpc.SetNavAndDisablePhysics();
            // Give the FixedUpdate state machine several ticks to run the handoff (it enables the RichAI and
            // updatePosition only inside FixedUpdate — a single frame may contain no FixedUpdate tick).
            for (var i = 0; i < 8; i++) yield return new WaitForFixedUpdate();
            // Fallback: if the state machine still has not enabled the RichAI, force it on directly so a
            // disabled component is never the reason the enemy sits still (we want to isolate PATHING).
            if (richAi != null && !((Behaviour)richAi).enabled)
            {
                _enemyNpc.NavMeshEnabledTarget = true;
                ((Behaviour)richAi).enabled = true;
                richAi.updatePosition = true;
                richAi.updateRotation = true;
                if (_enemyNpc.Rigidbody != null) _enemyNpc.Rigidbody.isKinematic = true;
            }
            report.Value("RichAI after handoff (enabled / canMove / updatePos)", richAi == null ? "<null>"
                : $"{((Behaviour)richAi).enabled} / {richAi.canMove} / {richAi.updatePosition}");

            var start = _enemy.transform.position;
            var closestToPillar = float.PositiveInfinity;
            var maxProgress = 0f;
            var t = 0f;
            while (t < EnemyDriveSeconds && _enemyNpc != null)
            {
                // Re-assert every frame so nothing (physics settle, a stray behaviour) steals the destination.
                _enemyNpc.AiAgent.SetNavMeshAgentState(true);
                _enemyNpc.AiAgent.SetDestination(b);

                var p = _enemy.transform.position;
                closestToPillar = Mathf.Min(closestToPillar, DistanceXZ(p, pillar));
                maxProgress = Mathf.Max(maxProgress, DistanceXZ(start, p));
                if (DistanceXZ(p, b) <= ReachedDistance) break;

                t += Time.deltaTime;
                yield return null;
            }

            var end = _enemy == null ? start : _enemy.transform.position;
            var reached = DistanceXZ(end, b);
            report.Value("enemy start / end (XZ)", $"({start.x:F1},{start.z:F1}) -> ({end.x:F1},{end.z:F1})");
            report.Value("distance travelled (max from start)", $"{maxProgress:F2} m");
            report.Value("remaining distance to target B", $"{reached:F2} m");
            report.Value("closest the enemy came to the pillar centre", $"{closestToPillar:F2} m");

            report.Line();
            var moved = maxProgress > 3f;
            var clearedPillar = closestToPillar >= RoutesAroundThreshold;
            report.Value("R9 verdict (live enemy)",
                (moved && clearedPillar && reached <= ReachedDistance)
                    ? $"TRACKS + ROUTES AROUND — the enemy walked {maxProgress:F1} m to within {reached:F1} m of B, "
                      + $"keeping {closestToPillar:F1} m clear of the pillar. A real agent paths our geometry around the pillar."
                : (moved && clearedPillar)
                    ? $"PATHS + CLEARS PILLAR — moved {maxProgress:F1} m, stayed {closestToPillar:F1} m clear of the "
                      + $"pillar, but did not reach B ({reached:F1} m left) in {EnemyDriveSeconds:F0} s. Nav is correct; "
                      + "may need more time or the enemy stops at attack range."
                : moved
                    ? $"MOVED BUT CLIPPED PILLAR — travelled {maxProgress:F1} m but came {closestToPillar:F1} m from the "
                      + "pillar centre. Read Layer 1; if that routed around, the enemy may have been shoved by physics."
                    : $"DID NOT MOVE — travelled {maxProgress:F1} m. The enemy activated but the RichAI did not drive "
                      + "it (nav state / physics). Layer 1 is the authoritative pathing result; iterate the enemy here.");
        }

        private static UnitSO ResolveUnit(string unitIdName)
        {
            var field = typeof(UnitIds).GetField(unitIdName, BindingFlags.Public | BindingFlags.Static)
                        ?? throw new InvalidOperationException($"UnitIds has no field '{unitIdName}'.");
            var id = (UnitId)field.GetValue(null);
            return id.GetAsset();
        }

        private void DespawnEnemy()
        {
            if (_enemyNpc != null)
            {
                var gm = StaticInstance<GameManager>.Instance;
                if (gm != null && gm.npcs != null) gm.npcs.Remove(_enemyNpc);
                _enemyNpc = null;
            }
            if (_enemy != null) { UnityEngine.Object.Destroy(_enemy); _enemy = null; }
        }

        private static float DistanceXZ(Vector3 a, Vector3 b)
        {
            var dx = a.x - b.x;
            var dz = a.z - b.z;
            return Mathf.Sqrt(dx * dx + dz * dz);
        }

        private static float PolylineLength(List<Vector3> pts)
        {
            var total = 0f;
            for (var i = 1; i < pts.Count; i++) total += Vector3.Distance(pts[i - 1], pts[i]);
            return total;
        }

        /// <summary>Minimum XZ distance from <paramref name="centre"/> to the polyline — measured along each
        /// segment, not only at vertices, so a straight segment passing through the pillar reads as ~0.</summary>
        private static float ClosestApproachXZ(List<Vector3> pts, Vector3 centre)
        {
            var best = float.PositiveInfinity;
            var c = new Vector2(centre.x, centre.z);
            for (var i = 1; i < pts.Count; i++)
            {
                var p0 = new Vector2(pts[i - 1].x, pts[i - 1].z);
                var p1 = new Vector2(pts[i].x, pts[i].z);
                best = Mathf.Min(best, PointToSegment(c, p0, p1));
            }
            return best;
        }

        private static float PointToSegment(Vector2 p, Vector2 a, Vector2 b)
        {
            var ab = b - a;
            var lenSq = ab.sqrMagnitude;
            var t = lenSq <= 1e-6f ? 0f : Mathf.Clamp01(Vector2.Dot(p - a, ab) / lenSq);
            return Vector2.Distance(p, a + t * ab);
        }

        private static Vector3 MarkerWorld(GameObject room, string markerName, Vector3 fallback)
        {
            var marker = room.GetComponentsInChildren<Transform>(true).FirstOrDefault(t => t.name == markerName);
            return marker != null ? marker.position : fallback;
        }

        private static Vector3 PillarWorldCentre(GameObject room, Vector3 origin)
        {
            var pillar = room.GetComponentsInChildren<Transform>(true).FirstOrDefault(t => t.name == "Pillar");
            return pillar != null ? new Vector3(pillar.position.x, origin.y, pillar.position.z) : origin;
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
            DespawnEnemy();
            if (_prefabHolder != null) { UnityEngine.Object.Destroy(_prefabHolder); _prefabHolder = null; }
            if (_room != null) { UnityEngine.Object.Destroy(_room); _room = null; }
            if (_bundle != null) { _bundle.Unload(unloadAllLoadedObjects: true); _bundle = null; }
        }
    }
}
