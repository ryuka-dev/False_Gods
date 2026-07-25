using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using LevelGeneration;
using Pathfinding;
using PerfectRandom.Sulfur.Core;
using UnityEngine;

namespace FalseGods.Probe
{
    /// <summary>
    /// PoC step P0. Reads, from the live game, everything the arena design is currently guessing at:
    ///
    ///   R3  the recast graph's rasterization mask and the game's geometry layer. If our CollisionRoot is
    ///       not on a layer the graph rasterizes, the arena is simply not walkable.
    ///   R5  NavMeshCleaner's validNavMeshPoints. Its OnGraphsPostUpdate flood-fills from those points and
    ///       marks every node outside the resulting areas UNWALKABLE. A custom arena island that is not
    ///       represented there gets erased, and the boss never moves.
    ///   §4.4 the real recast agent parameters, which are serialized on the AstarPath component and appear
    ///       nowhere in code.
    ///
    /// Everything is read-only, and every read is individually guarded: the point is to learn which members
    /// exist and what they hold, so one wrong assumption must not abort the rest of the report.
    /// </summary>
    internal static class NavigationProbe
    {
        public static void Run(ProbeReport report)
        {
            report.Section("P0 — environment");
            // Fully qualified: the probe now also references the FalseGods.Application assembly, whose namespace
            // would otherwise shadow UnityEngine.Application here.
            report.Value("Application.unityVersion", UnityEngine.Application.unityVersion);
            report.Value("Time.time", Time.time);

            report.Section("P0 — GameManager (R3: geometry layer)");
            report.Try("GameManager", () =>
            {
                var gameManager = GameManager.Instance;
                if (gameManager == null)
                {
                    report.Line("  GameManager.Instance is null — no level loaded yet.");
                    return;
                }

                report.Value("geometryLayer.value", gameManager.geometryLayer.value);
                report.Value("geometryLayer (layers)", DescribeLayerMask(gameManager.geometryLayer));
                report.Value("currentLevelIndex", gameManager.currentLevelIndex);

                var environment = gameManager.currentEnvironment;
                if (environment == null)
                {
                    report.Line("  currentEnvironment is null.");
                    return;
                }

                report.Value("currentEnvironment.id", environment.id);
                report.Value("navMeshVoxelSize (= cellSize)", environment.navMeshVoxelSize);
                report.Value("npcActiveDistanceToPlayer", environment.npcActiveDistanceToPlayer);
                report.Value("npcActiveRoomMargin", environment.npcActiveRoomMargin);
            });

            report.Section("P0 — AstarPath (lifecycle)");
            var astar = AstarPath.active;
            if (astar == null)
            {
                report.Line("  AstarPath.active is null — the level's graph does not exist yet.");
                report.Line("  (Expected before a level loads: GameManager destroys and re-instantiates");
                report.Line("   astarPathPrefab on every level change — GameManager.cs:1097 / :1137.)");
                return;
            }

            report.Try("AstarPath identity", () =>
            {
                report.Value("AstarPath.Version", AstarPath.Version);
                report.Value("active.gameObject.name", astar.gameObject.name);
                report.Value("active.GetInstanceID()", astar.GetInstanceID());
                report.Value("scanOnStartup", astar.scanOnStartup);
                report.Value("threadCount", astar.threadCount);
            });

            report.Section("P0 — graphs");
            report.Try("graphs", () =>
            {
                var graphs = astar.data.graphs;
                report.Value("graph count", graphs?.Length ?? 0);

                if (graphs == null)
                    return;

                for (var i = 0; i < graphs.Length; i++)
                {
                    var graph = graphs[i];
                    report.Value($"graphs[{i}]", graph == null ? "<null>" : graph.GetType().Name);
                }

                // Docs assert the recast graph is index 0 (NNConstraint.graphMask = 1 throughout the code).
                var recastIndex = graphs.ToList().FindIndex(g => g is RecastGraph);
                report.Value("index of RecastGraph", recastIndex);
                report.Value("docs claim index 0", recastIndex == 0 ? "CONFIRMED" : "*** DIFFERS ***");
            });

            report.Section("P0 — RecastGraph (R3 + §4.4 agent parameters)");
            report.Try("recastGraph", () =>
            {
                var recast = astar.data.recastGraph;
                if (recast == null)
                {
                    report.Line("  data.recastGraph is null.");
                    return;
                }

                report.Line("  -- rasterization (R3): what geometry the scan even looks at");
                // The rasterization inputs moved from RecastGraph fields to recast.collectionSettings in this
                // A* version (RecastGraph.mask etc. are now [Obsolete] shims). This is itself a correction to
                // CollisionAndNavigationProposal.md §4.2, which still names them as top-level fields.
                var collection = recast.collectionSettings;
                if (collection == null)
                {
                    report.Line("  recast.collectionSettings is null (unexpected).");
                }
                else
                {
                    report.Value("collectionSettings.layerMask.value", collection.layerMask.value);
                    report.Value("collectionSettings.layerMask (layers)", DescribeLayerMask(collection.layerMask));
                    report.Value("collectionSettings.rasterizeColliders", collection.rasterizeColliders);
                    report.Value("collectionSettings.rasterizeMeshes", collection.rasterizeMeshes);
                    report.Value("collectionSettings.rasterizeTerrain", collection.rasterizeTerrain);
                    report.Value("collectionSettings.rasterizeTrees", collection.rasterizeTrees);
                    report.Value("collectionSettings.colliderRasterizeDetail", collection.colliderRasterizeDetail);
                }

                report.Line("  -- agent fit (§4.4): design the arena inside these limits");
                report.Value("cellSize (voxel size)", recast.cellSize);
                report.Value("characterRadius", recast.characterRadius);
                report.Value("walkableHeight", recast.walkableHeight);
                report.Value("walkableClimb", recast.walkableClimb);
                report.Value("maxSlope", recast.maxSlope);
                report.Value("maxEdgeLength", recast.maxEdgeLength);
                report.Value("contourMaxError", recast.contourMaxError);
                report.Value("minRegionSize", recast.minRegionSize);

                report.Line("  -- tiling and bounds");
                report.Value("useTiles", recast.useTiles);
                report.Value("editorTileSize", recast.editorTileSize);
                report.Value("tileSizeX / tileSizeZ", $"{recast.tileSizeX} / {recast.tileSizeZ}");
                report.Value("dimensionMode", recast.dimensionMode);
                report.Value("bounds", recast.bounds);
                report.Value("forcedBoundsCenter", recast.forcedBoundsCenter);
                report.Value("rotation", recast.rotation);
            });

            report.Section("P0 — node walkability snapshot");
            report.Try("node counts", () =>
            {
                var recast = astar.data.recastGraph;
                if (recast == null)
                    return;

                var total = 0;
                var walkable = 0;
                var areas = new HashSet<uint>();

                // Where the walkable surface actually IS, in height. A cave with terraces reads as one number
                // otherwise: "walkable nodes: lots" says nothing about whether the raised ledges got any. Bucketed
                // by 2 m, with the area ids per band, so an isolated terrace shows up as its own area.
                var nodesByBand = new SortedDictionary<int, int>();
                var areasByBand = new SortedDictionary<int, HashSet<uint>>();
                var minY = float.MaxValue;
                var maxY = float.MinValue;

                // A snapshot taken outside a work item. Good enough for a diagnostic; never do this in
                // production code, where it would race the pathfinding threads.
                recast.GetNodes(node =>
                {
                    total++;
                    if (!node.Walkable)
                        return;

                    walkable++;
                    areas.Add(node.Area);

                    var y = ((Vector3)node.position).y;
                    if (y < minY) minY = y;
                    if (y > maxY) maxY = y;

                    var band = Mathf.FloorToInt(y / 2f) * 2;
                    nodesByBand.TryGetValue(band, out var count);
                    nodesByBand[band] = count + 1;

                    if (!areasByBand.TryGetValue(band, out var bandAreas))
                    {
                        bandAreas = new HashSet<uint>();
                        areasByBand[band] = bandAreas;
                    }

                    bandAreas.Add(node.Area);
                });

                report.Value("total nodes", total);
                report.Value("walkable nodes", walkable);
                report.Value("unwalkable nodes", total - walkable);
                report.Value("distinct areas", areas.Count);

                if (walkable > 0)
                {
                    report.Value("walkable Y range", $"{minY:0.00} .. {maxY:0.00}");
                    report.Line("  walkable nodes by height band (band: nodes, area ids):");
                    foreach (var band in nodesByBand)
                    {
                        var ids = string.Join(",", areasByBand[band.Key].Select(a => a.ToString()).ToArray());
                        report.Line($"    y {band.Key,4} .. {band.Key + 2,-4} : {band.Value,6} nodes   areas [{ids}]");
                    }
                }
            });

            report.Section("P0 — off-mesh links (the authored jump connections)");
            report.Try("NodeLink2", () =>
            {
                var links = UnityEngine.Object.FindObjectsByType<NodeLink2>(
                    FindObjectsInactive.Include, FindObjectsSortMode.None);

                report.Value("NodeLink2 components in scene", links.Length);
                if (links.Length == 0)
                {
                    report.Line("  None. Enemies can only reach what the walkable surface itself connects.");
                    return;
                }

                // A link that cannot snap to the navmesh is dropped SILENTLY by TryAddLink — the endpoint has to
                // land within maxSnappingDistance (1 m) of a walkable node. The status on its source is the only
                // place that failure is visible, and it names which END failed. The field is protected, so this
                // diagnostic reads it by reflection rather than pretending the API is public.
                var sourceField = typeof(NodeLink2).GetField("linkSource",
                    BindingFlags.Instance | BindingFlags.NonPublic);

                foreach (var link in links)
                {
                    var start = link.StartTransform == null ? "<none>" : link.StartTransform.position.ToString("0.00");
                    var end = link.EndTransform == null ? "<none>" : link.EndTransform.position.ToString("0.00");
                    var source = sourceField?.GetValue(link) as OffMeshLinks.OffMeshLinkSource;
                    var status = source == null ? "not registered (TryAddLink never ran)" : source.status.ToString();

                    report.Line($"  [{link.name}] {start} -> {end}  {(link.oneWay ? "one-way" : "two-way")}  status = {status}");
                }

                report.Line("  Active = attached. FailedToConnectStart/End = that endpoint was further than 1 m");
                report.Line("  from a walkable node; move it onto the surface.");
            });

            report.Section("P0 — NavMeshCleaner (R5: the flood-fill that erases custom islands)");
            report.Try("NavMeshCleaner", () =>
            {
                var cleaners = Resources.FindObjectsOfTypeAll<NavMeshCleaner>();
                report.Value("NavMeshCleaner instances", cleaners.Length);

                foreach (var cleaner in cleaners)
                {
                    var points = cleaner.validNavMeshPoints;
                    report.Value($"  [{cleaner.name}] validNavMeshPoints", points == null ? "<null>" : points.Length.ToString());

                    if (points != null && points.Length > 0)
                    {
                        report.Value("  first point", points[0]);
                        report.Value("  last point", points[points.Length - 1]);
                    }
                }

                if (cleaners.Length == 0)
                    report.Line("  None found. Either no level is loaded, or the cleaner is created per generation.");
            });

            report.Section("P0 — rooms and nav anchors (where validNavMeshPoints come from)");
            report.Try("rooms", () =>
            {
                var rooms = UnityEngine.Object.FindObjectsByType<PerfectRandom.Sulfur.Core.LevelGeneration.Room>(
                    FindObjectsSortMode.None);

                report.Value("Room instances in scene", rooms.Length);
                report.Value("Room.NAVMESH_ANCHOR_TAG", PerfectRandom.Sulfur.Core.LevelGeneration.Room.NAVMESH_ANCHOR_TAG);

                var anchorTotal = 0;
                foreach (var room in rooms)
                {
                    var anchors = room.navMeshAnchors;
                    if (anchors != null)
                        anchorTotal += anchors.Count;
                }

                report.Value("total navMeshAnchors", anchorTotal);
                report.Line("  (BuildNavMeshNode builds validNavMeshPoints from connector positions + these anchors.");
                report.Line("   A custom arena contributes neither unless we add one — that is R5.)");
            });
        }

        private static string DescribeLayerMask(LayerMask mask)
        {
            var names = new List<string>();

            for (var layer = 0; layer < 32; layer++)
            {
                if ((mask.value & (1 << layer)) == 0)
                    continue;

                var name = LayerMask.LayerToName(layer);
                names.Add(string.IsNullOrEmpty(name) ? $"<unnamed {layer}>" : $"{layer}:{name}");
            }

            return names.Count == 0 ? "<none>" : string.Join(", ", names);
        }
    }
}
