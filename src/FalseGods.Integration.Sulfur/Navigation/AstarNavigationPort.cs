// Heavy A*/Unity interop (none of those APIs carry nullable annotations), so this file opts out of the
// nullable-reference context like the other game-facing implementations.
#nullable disable

using System;
using FalseGods.Application.Arena;
using Pathfinding;
using Pathfinding.Graphs.Navmesh;
using UnityEngine;
using ILogger = FalseGods.RuntimeContracts.Diagnostics.ILogger;

namespace FalseGods.Integration.Sulfur.Navigation
{
    /// <summary>
    /// <see cref="INavigationPort"/> over the game's live A* recast graph — the productionised form of the
    /// measured P7 probe discipline (ADR-002, Docs/CollisionAndNavigationProposal.md §4.3 Option 1 / §4.6,
    /// RiskList R4/R5/R8/R30).
    /// </summary>
    /// <remarks>
    /// Everything here was measured in-game before it was written down:
    /// <list type="bullet">
    /// <item><b>Bake in clear space, never scan live.</b> A live rescan does not rasterize our bundle meshes
    /// (P5); instead a clone of the realized arena is instantiated +300 m up, a <see cref="NavmeshPrefab"/>
    /// scans it against the live graph's settings (touching nothing), and the clone is destroyed.</item>
    /// <item><b>Tile-align the bounds.</b> The bake and apply bounds are the same tile-aligned rect, or
    /// <c>SnapToGraph</c>'s rounded origin shifts the floor (the P6 fix).</item>
    /// <item><b>Snapshot before overwrite, restore exactly.</b> <c>ReplaceTiles</c> overwrites whole XZ tile
    /// columns, clobbering the level's own ground nav in the footprint; the level tiles are snapshotted
    /// (geometry <b>and</b> per-node walkability — restoring geometry alone over-restores, because the
    /// <c>NavMeshCleaner</c> flood-fill is not re-run) and put back verbatim on <see cref="Remove"/>. A bare
    /// <c>ClearTiles</c> would leave a hole players are standing in (P7).</item>
    /// <item><b>A level change owns its own cleanup.</b> The game rebuilds <c>AstarPath.active</c> per level
    /// (P0), so if the graph instance changed since <see cref="Apply"/>, the snapshot belongs to a dead graph
    /// and <see cref="Remove"/> deliberately restores nothing.</item>
    /// </list>
    /// The arena geometry arrives by composition-time injection (a Unity-level source the Composition Root
    /// wires), keeping this signature-compatible with the Unity-free port. Work items are flushed synchronously
    /// (<see cref="AstarPath.FlushWorkItems"/>), so <see cref="Apply"/>/<see cref="Remove"/> complete before
    /// they return; both run on the main thread.
    /// </remarks>
    public sealed class AstarNavigationPort : INavigationPort
    {
        private const float BakeClearHeight = 300f;
        private const float NavBoundsHeight = 4f;
        private const float FloorBandHalf = 1.5f;

        private readonly Func<GameObject> _arenaRoot;
        private readonly ILogger _logger;

        private AstarPath _appliedGraph;
        private TileMeshes _savedTiles;
        private bool[][] _savedWalkable;
        private IntRect _appliedRect;
        private bool _applied;

        /// <param name="arenaRoot">Returns the realized arena's root, or null when none is realized. Injected by
        /// the Composition Root from the UnityRuntime realization; this adapter never references that module.</param>
        /// <param name="logger">Optional diagnostics; never required for correct behaviour.</param>
        public AstarNavigationPort(Func<GameObject> arenaRoot, ILogger logger = null)
        {
            _arenaRoot = arenaRoot ?? throw new ArgumentNullException(nameof(arenaRoot));
            _logger = logger;
        }

        public NavigationApplyResult Apply()
        {
            if (_applied)
            {
                return NavigationApplyResult.Failed("arena navigation is already applied");
            }

            var astar = AstarPath.active;
            var recast = astar != null && astar.data != null ? astar.data.recastGraph : null;
            if (recast == null)
            {
                return NavigationApplyResult.Failed("no active recast graph (no loaded level)");
            }

            // The graph exists but its tiles may not be built yet (e.g. a hub/safe zone) — GetTile would then
            // dereference a null tile array. Fail closed with a clear reason rather than throw.
            if (!recast.isScanned)
            {
                return NavigationApplyResult.Failed("the level's navigation is not built here (no scanned recast tiles)");
            }

            var root = _arenaRoot();
            if (root == null)
            {
                return NavigationApplyResult.Failed("no realized arena to build navigation for");
            }

            var arena = WorldBounds(root);
            var layout = new TileLayout(recast);
            var tileRect = layout.GetTouchingTiles(arena, 0.5f);
            var alignedGraph = layout.GetTileBoundsInGraphSpace(tileRect.xmin, tileRect.ymin, tileRect.Width, tileRect.Height);
            var alignedCentre = layout.transform.Transform(alignedGraph.center);
            var alignedSize = new Vector3(
                tileRect.Width * layout.TileWorldSizeX, NavBoundsHeight, tileRect.Height * layout.TileWorldSizeZ);
            NavmeshPrefab.SnapToGraph(
                layout,
                new Vector3(alignedCentre.x, arena.center.y, alignedCentre.z),
                Quaternion.identity,
                new Bounds(Vector3.zero, alignedSize),
                out var graphTileRect,
                out var snappedRotation,
                out var yOffset);

            // SnapToGraph does NOT clamp to the graph, so an arena placed near a level edge yields a tile rect
            // that runs off the tile array — GetTile / ReplaceTiles / ClearTiles would then throw. The whole
            // footprint must sit inside the graph, or the apply is refused fail-closed (§5.3.1: never corrupt the
            // level's own graph). Moving toward the level centre resolves it.
            if (graphTileRect.xmin < 0 || graphTileRect.ymin < 0
                || graphTileRect.xmax >= recast.tileXCount || graphTileRect.ymax >= recast.tileZCount)
            {
                return NavigationApplyResult.Failed(
                    $"arena navigation footprint tiles [{graphTileRect.xmin},{graphTileRect.ymin}]..[{graphTileRect.xmax},"
                    + $"{graphTileRect.ymax}] extend past the level's navigable area ({recast.tileXCount}x{recast.tileZCount} "
                    + "tiles); move toward the level centre and try again");
            }

            byte[] baked;
            try
            {
                baked = BakeInClearSpace(recast, root, alignedCentre, alignedSize);
            }
            catch (Exception exception)
            {
                return NavigationApplyResult.Failed($"arena navmesh bake threw: {exception.Message}");
            }

            if (baked == null)
            {
                return NavigationApplyResult.Failed("arena navmesh bake produced no data");
            }

            var tiles = TileMeshes.Deserialize(baked);
            var bakedTriangles = 0;
            if (tiles.tileMeshes != null)
            {
                for (var i = 0; i < tiles.tileMeshes.Length; i++)
                {
                    bakedTriangles += (tiles.tileMeshes[i].triangles?.Length ?? 0) / 3;
                }
            }

            if (bakedTriangles == 0)
            {
                return NavigationApplyResult.Failed("arena navmesh baked 0 triangles (floor did not rasterize)");
            }

            tiles.Rotate(snappedRotation);
            if (tiles.tileRect.Width != graphTileRect.Width || tiles.tileRect.Height != graphTileRect.Height)
            {
                return NavigationApplyResult.Failed(
                    $"baked tile dims {tiles.tileRect.Width}x{tiles.tileRect.Height} do not match the graph rect "
                    + $"{graphTileRect.Width}x{graphTileRect.Height}");
            }

            tiles.tileRect = graphTileRect;

            TileMeshes saved;
            bool[][] savedWalkable;
            try
            {
                saved = SnapshotTiles(recast, graphTileRect, out savedWalkable);
            }
            catch (Exception exception)
            {
                return NavigationApplyResult.Failed($"level tile snapshot threw: {exception.Message}");
            }

            astar.AddWorkItem((Action)(() => recast.ReplaceTiles(tiles, yOffset)));
            astar.FlushWorkItems();

            var floorY = root.transform.position.y;
            var walkable = CountWalkable(recast, arena, floorY - FloorBandHalf, floorY + FloorBandHalf);
            if (walkable == 0)
            {
                // Fail closed AND leave the level as it was: put the snapshotted tiles straight back.
                RestoreSnapshot(astar, recast, saved, graphTileRect, savedWalkable);
                return NavigationApplyResult.Failed("applied arena navmesh added no walkable floor nodes; level tiles restored");
            }

            _appliedGraph = astar;
            _savedTiles = saved;
            _savedWalkable = savedWalkable;
            _appliedRect = graphTileRect;
            _applied = true;
            _logger?.Log($"[arena-nav] applied {bakedTriangles} baked triangles over tiles "
                + $"[{graphTileRect.xmin},{graphTileRect.ymin}] {graphTileRect.Width}x{graphTileRect.Height}; "
                + $"{walkable} walkable floor node(s)");
            return NavigationApplyResult.Applied(walkable);
        }

        public void Remove()
        {
            if (!_applied)
            {
                return;
            }

            try
            {
                var astar = AstarPath.active;
                if (astar == null || !ReferenceEquals(astar, _appliedGraph))
                {
                    // The game rebuilt the graph for a new level (P0); our tiles died with the old instance and
                    // the snapshot belongs to it — restoring into the new graph would corrupt it.
                    _logger?.Log("[arena-nav] graph was rebuilt since apply; nothing to restore");
                    return;
                }

                var recast = astar.data != null ? astar.data.recastGraph : null;
                if (recast != null)
                {
                    RestoreSnapshot(astar, recast, _savedTiles, _appliedRect, _savedWalkable);
                    _logger?.Log("[arena-nav] level tiles restored to baseline (geometry + walkability)");
                }
            }
            finally
            {
                _applied = false;
                _appliedGraph = null;
                _savedTiles = default;
                _savedWalkable = null;
            }
        }

        private static void RestoreSnapshot(
            AstarPath astar, RecastGraph recast, TileMeshes saved, IntRect rect, bool[][] savedWalkable)
        {
            astar.AddWorkItem((Action)(() =>
            {
                recast.ReplaceTiles(saved, 0f);
                RestoreWalkability(recast, rect, savedWalkable);
            }));
            astar.FlushWorkItems();
        }

        /// <summary>P7's bake: scan a clone of the realized arena +300 m up (clear of level geometry) with the
        /// same tile-aligned bounds the apply uses. <see cref="NavmeshPrefab.Scan(RecastGraph)"/> builds its own
        /// TileBuilder and never touches the live graph.</summary>
        private static byte[] BakeInClearSpace(RecastGraph recast, GameObject root, Vector3 alignedCentre, Vector3 alignedSize)
        {
            var bakeArena = UnityEngine.Object.Instantiate(
                root, root.transform.position + (Vector3.up * BakeClearHeight), root.transform.rotation);
            bakeArena.name = "FalseGodsArenaNavBake";
            try
            {
                var bakeBounds = WorldBounds(bakeArena);
                var holder = new GameObject("FalseGodsArenaNavBakeHolder");
                holder.transform.position = new Vector3(alignedCentre.x, bakeBounds.center.y, alignedCentre.z);
                try
                {
                    var navPrefab = holder.AddComponent<NavmeshPrefab>();
                    navPrefab.applyOnStart = false;
                    navPrefab.removeTilesWhenDisabled = false;
                    navPrefab.bounds = new Bounds(Vector3.zero, alignedSize);
                    return navPrefab.Scan(recast);
                }
                finally
                {
                    UnityEngine.Object.Destroy(holder);
                }
            }
            finally
            {
                UnityEngine.Object.Destroy(bakeArena);
            }
        }

        /// <summary>Snapshot the live level tiles in <paramref name="rect"/> so <c>ReplaceTiles</c> can put them
        /// back verbatim: per-tile vertices converted back to tile space, triangles, tags, and — separately —
        /// each node's <c>Walkable</c> flag (nodes are rebuilt in triangle order, so index i round-trips).</summary>
        private static TileMeshes SnapshotTiles(RecastGraph recast, IntRect rect, out bool[][] walkablePerTile)
        {
            var width = rect.Width;
            var height = rect.Height;
            var meshes = new TileMesh[width * height];
            walkablePerTile = new bool[width * height][];
            for (var z = 0; z < height; z++)
            {
                for (var x = 0; x < width; x++)
                {
                    var slot = x + (z * width);
                    var tile = recast.GetTile(rect.xmin + x, rect.ymin + z);
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

                    var offset = (Int3)new Vector3(
                        (rect.xmin + x) * recast.TileWorldSizeX, 0f, (rect.ymin + z) * recast.TileWorldSizeZ);
                    var verts = new Int3[tile.vertsInGraphSpace.Length];
                    for (var i = 0; i < verts.Length; i++)
                    {
                        verts[i] = tile.vertsInGraphSpace[i] - offset;
                    }

                    var tris = new int[tile.tris.Length];
                    for (var i = 0; i < tris.Length; i++)
                    {
                        tris[i] = tile.tris[i];
                    }

                    var triCount = tris.Length / 3;
                    var tags = new uint[triCount];
                    var walkable = new bool[triCount];
                    var nodes = tile.nodes;
                    if (nodes != null)
                    {
                        for (var i = 0; i < triCount && i < nodes.Length; i++)
                        {
                            if (nodes[i] != null)
                            {
                                tags[i] = nodes[i].Tag;
                                walkable[i] = nodes[i].Walkable;
                            }
                        }
                    }

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

        /// <summary>Reapply the saved per-node walkability after restoring geometry — inside a work item (the
        /// setter dirties the hierarchical graph).</summary>
        private static void RestoreWalkability(RecastGraph recast, IntRect rect, bool[][] walkablePerTile)
        {
            if (walkablePerTile == null)
            {
                return;
            }

            var width = rect.Width;
            var height = rect.Height;
            for (var z = 0; z < height; z++)
            {
                for (var x = 0; x < width; x++)
                {
                    var walkable = walkablePerTile[x + (z * width)];
                    if (walkable == null || walkable.Length == 0)
                    {
                        continue;
                    }

                    var tile = recast.GetTile(rect.xmin + x, rect.ymin + z);
                    var nodes = tile?.nodes;
                    if (nodes == null)
                    {
                        continue;
                    }

                    for (var i = 0; i < nodes.Length && i < walkable.Length; i++)
                    {
                        if (nodes[i] != null)
                        {
                            nodes[i].Walkable = walkable[i];
                        }
                    }
                }
            }
        }

        private static int CountWalkable(RecastGraph recast, Bounds xzBounds, float yMin, float yMax)
        {
            var minX = xzBounds.center.x - xzBounds.extents.x;
            var maxX = xzBounds.center.x + xzBounds.extents.x;
            var minZ = xzBounds.center.z - xzBounds.extents.z;
            var maxZ = xzBounds.center.z + xzBounds.extents.z;
            var count = 0;
            recast.GetNodes(node =>
            {
                if (!node.Walkable)
                {
                    return;
                }

                var p = (Vector3)node.position;
                if (p.y < yMin || p.y > yMax || p.x < minX || p.x > maxX || p.z < minZ || p.z > maxZ)
                {
                    return;
                }

                count++;
            });
            return count;
        }

        private static Bounds WorldBounds(GameObject go)
        {
            var renderers = go.GetComponentsInChildren<Renderer>(true);
            if (renderers.Length == 0)
            {
                return new Bounds(go.transform.position, new Vector3(24f, 8f, 24f));
            }

            var bounds = renderers[0].bounds;
            for (var i = 1; i < renderers.Length; i++)
            {
                bounds.Encapsulate(renderers[i].bounds);
            }

            return bounds;
        }
    }
}
