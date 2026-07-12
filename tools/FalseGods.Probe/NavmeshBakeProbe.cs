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
    /// PoC step P5c — bake our arena navmesh in-game AND diagnose why it does or does not rasterize (RiskList
    /// R4). P5c's first run (clear space, +300 m, isolated) baked our floor to <b>0 triangles</b> — so our floor
    /// mesh does not rasterize in A* recast at all, which also invalidated P5b's "works" (that had baked LEVEL
    /// geometry, not ours). The leading cause: A* recast reads mesh <i>positions + indices</i> and computes each
    /// triangle's normal from the winding (<c>cross(v1-v0, v2-v0)</c>), not the supplied vertex normals; our
    /// generated box's top face winds so that geometric normal points DOWN, which recast treats as a ceiling
    /// (not walkable) even though it renders fine.
    ///
    /// This step measures that directly: it bakes our arena floor as-authored and a runtime winding-REVERSED
    /// copy of the same mesh, side by side in clear space, and reports each one's baked triangle count and its
    /// top-face geometric normal. If the reversed copy bakes and the original does not, the fix is to correct the
    /// winding in <c>PocRoomGenerator.BuildBoxMesh</c> (then rebuild the bundle). When the arena does bake, it
    /// also writes the serialized bytes — the shippable Option-1 artifact.
    ///
    /// Read-only w.r.t. <c>AstarPath.active</c>: <c>NavmeshPrefab.Scan()</c> builds a separate TileBuilder and
    /// never touches the live graph.
    /// </summary>
    internal sealed class NavmeshBakeProbe
    {
        private const string BundleFileName = "falsegods-poc-room.bundle";
        private const string OurRoomPrefabName = "PocRoom";
        private const string FloorChildName = "Floor";

        private const float ClearHeight = 300f;    // m above the camera — clear of all level geometry
        private const float ReversedOffsetX = 60f; // place the reversed-winding copy well clear of the arena

        private AssetBundle _bundle;
        private GameObject _room;
        private GameObject _reversed;
        private GameObject _prefabHolder;

        public IEnumerator Run(ProbeReport report)
        {
            report.Section("P5c — bake & diagnose the arena navmesh (R4, Option 1)");

            var recast = AstarPath.active?.data?.recastGraph;
            var camera = Camera.main;
            if (recast == null || camera == null)
            {
                report.Line("  Skipped: need AstarPath.active + recastGraph + Camera.main (stand in a loaded level).");
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
            var ourLoad = _bundle == null ? null : _bundle.LoadAssetAsync<GameObject>(OurRoomPrefabName);
            if (ourLoad != null) yield return ourLoad;
            if (!(ourLoad?.asset is GameObject ourPrefab))
            {
                report.Line("  *** FAILED: bundle / PocRoom did not load. ***");
                Cleanup();
                yield break;
            }

            var origin = camera.transform.position + Vector3.up * ClearHeight;
            _room = UnityEngine.Object.Instantiate(ourPrefab, origin, Quaternion.identity);
            _room.name = "FalseGodsP5c_BakeIsland";

            report.Value("bake origin (clear space)", origin);
            report.Value("graph cellSize / editorTileSize", $"{recast.cellSize} / {recast.editorTileSize}");
            report.Value("agent radius / height / climb / slope",
                $"{recast.characterRadius} / {recast.walkableHeight} / {recast.walkableClimb} / {recast.maxSlope}");

            var floorFilter = _room.GetComponentsInChildren<MeshFilter>(true)
                .FirstOrDefault(f => f.name == FloorChildName);
            if (floorFilter == null || floorFilter.sharedMesh == null)
            {
                report.Line("  *** FAILED: no readable Floor mesh found. ***");
                Cleanup();
                yield break;
            }
            var floorMesh = floorFilter.sharedMesh;
            report.Value($"floor mesh '{floorMesh.name}'",
                $"isReadable={floorMesh.isReadable}, verts={floorMesh.vertexCount}, tris={floorMesh.triangles.Length / 3}");
            report.Value("floor TOP-face geometric normal (as-authored)", TopFaceNormal(floorMesh).ToString("F2"));

            // ── Bake 1: the arena as authored, tight bounds around its floor.
            var arenaBounds = WorldBounds(_room);
            var arenaData = BakeOver(recast, _room.transform.position + Vector3.up * (arenaBounds.center.y - _room.transform.position.y),
                new Vector3(arenaBounds.size.x + 2f, 4f, arenaBounds.size.z + 2f), report, "arena as-authored", out var arenaTris);

            // ── Bake 2: a winding-reversed copy of the floor, well clear of the arena.
            var reversedMesh = ReverseWinding(floorMesh);
            report.Value("floor TOP-face geometric normal (reversed)", TopFaceNormal(reversedMesh).ToString("F2"));
            _reversed = new GameObject("FalseGodsP5c_ReversedFloor") { layer = floorFilter.gameObject.layer };
            _reversed.transform.position = new Vector3(origin.x + ReversedOffsetX, origin.y, origin.z);
            _reversed.AddComponent<MeshFilter>().sharedMesh = reversedMesh;
            _reversed.AddComponent<MeshRenderer>().sharedMaterial = floorFilter.GetComponent<MeshRenderer>()?.sharedMaterial;
            BakeOver(recast, _reversed.transform.position, new Vector3(24f, 4f, 24f), report, "floor winding-reversed", out var reversedTris);

            report.Line();
            report.Value("DIAGNOSIS", Diagnose(arenaTris, reversedTris));

            // If the arena baked as authored, write the shippable artifact.
            if (arenaTris > 0 && arenaData != null)
            {
                var fileName = $"arena-nav-{OurRoomPrefabName}-cell{recast.cellSize:0.00}.bytes";
                var outPath = System.IO.Path.Combine(Paths.BepInExRootPath, "FalseGods.Probe", fileName);
                try
                {
                    File.WriteAllBytes(outPath, arenaData);
                    report.Value("ARTIFACT WRITTEN", $"{outPath} ({arenaData.Length} bytes, cellSize {recast.cellSize:0.00})");
                }
                catch (Exception exception)
                {
                    report.Value("write FAILED", exception.ToString());
                }
            }

            Cleanup();
            report.Section("P5c — teardown");
            report.Line("  Islands destroyed, bundle unloaded. The live graph was never touched.");
        }

        /// <summary>Creates a NavmeshPrefab over the given world centre/size, scans it, and reports + returns the
        /// baked triangle count (and the serialized bytes via <paramref name="outData"/> for the caller).</summary>
        private byte[] BakeOver(RecastGraph recast, Vector3 worldCentre, Vector3 size, ProbeReport report, string label, out int triangles)
        {
            triangles = 0;
            _prefabHolder = new GameObject($"FalseGodsP5c_Bake_{label}");
            _prefabHolder.transform.position = worldCentre;
            var navPrefab = _prefabHolder.AddComponent<NavmeshPrefab>();
            navPrefab.applyOnStart = false;
            navPrefab.removeTilesWhenDisabled = false;
            navPrefab.bounds = new Bounds(Vector3.zero, size);

            byte[] data = null;
            try
            {
                data = navPrefab.Scan(recast);
                var tiles = TileMeshes.Deserialize(data);
                triangles = tiles.tileMeshes == null ? 0 : tiles.tileMeshes.Sum(t => (t.triangles?.Length ?? 0) / 3);
                report.Value($"[{label}] baked triangles / bytes", $"{triangles} / {data.Length}");
            }
            catch (Exception exception)
            {
                report.Value($"[{label}] bake THREW", exception.ToString());
            }
            finally
            {
                UnityEngine.Object.Destroy(_prefabHolder);
                _prefabHolder = null;
            }
            return data;
        }

        private static string Diagnose(int arenaTris, int reversedTris)
        {
            if (arenaTris > 0)
                return "arena floor bakes as-authored — winding is fine; the earlier 0-triangle result was placement/pollution.";
            if (reversedTris > 0)
                return "WINDING IS THE BUG — the as-authored floor bakes 0 triangles but the winding-reversed copy "
                       + "bakes fine. Fix PocRoomGenerator.BuildBoxMesh to wind the top face so its geometric "
                       + "normal points UP, then rebuild the bundle.";
            return "NEITHER bakes — winding is not the (only) cause. Investigate the mesh further (Position "
                   + "attribute, scale, degenerate triangles) or the bake setup.";
        }

        /// <summary>The geometric normal (recast's convention, <c>cross(v1-v0, v2-v0)</c>) of the triangle whose
        /// vertices sit highest — i.e. the top face. This is what recast uses to decide walkability, regardless
        /// of the mesh's supplied vertex normals.</summary>
        private static Vector3 TopFaceNormal(Mesh mesh)
        {
            var verts = mesh.vertices;
            var tris = mesh.triangles;
            var bestY = float.NegativeInfinity;
            var normal = Vector3.zero;
            for (var i = 0; i < tris.Length; i += 3)
            {
                var a = verts[tris[i]];
                var b = verts[tris[i + 1]];
                var c = verts[tris[i + 2]];
                var y = (a.y + b.y + c.y) / 3f;
                if (y > bestY)
                {
                    bestY = y;
                    normal = Vector3.Cross(b - a, c - a).normalized;
                }
            }
            return normal;
        }

        private static Mesh ReverseWinding(Mesh src)
        {
            var tris = (int[])src.triangles.Clone();
            for (var i = 0; i < tris.Length; i += 3)
                (tris[i + 1], tris[i + 2]) = (tris[i + 2], tris[i + 1]);
            var mesh = new Mesh { vertices = src.vertices, normals = src.normals, uv = src.uv, triangles = tris };
            mesh.RecalculateBounds();
            return mesh;
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
            if (_reversed != null) { UnityEngine.Object.Destroy(_reversed); _reversed = null; }
            if (_room != null) { UnityEngine.Object.Destroy(_room); _room = null; }
            if (_bundle != null) { _bundle.Unload(unloadAllLoadedObjects: true); _bundle = null; }
        }
    }
}
