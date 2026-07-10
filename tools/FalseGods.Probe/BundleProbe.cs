using System;
using System.Collections;
using System.IO;
using System.Linq;
using BepInEx;
using UnityEngine;

namespace FalseGods.Probe
{
    /// <summary>
    /// PoC step P2 — RiskList R2: does an AssetBundle built in the game's exact Unity version (6000.3.6f1,
    /// FalseGods.Unity project) load under BepInEx, and does its prefab instantiate with meshes, materials
    /// and collider layers intact?
    ///
    /// The bundle is NOT shipped with the probe. Build it first
    /// (FalseGods.Unity — "False Gods/Build PoC AssetBundle", or the batch entry point), then place
    /// falsegods-poc-room.bundle in BepInEx/FalseGods.Probe/ next to the probe reports
    /// (`dotnet build … -p:DeployProbe=true` copies it automatically when it exists). If the file is
    /// missing, this probe reports that and skips — the P0/P1 sections still run.
    ///
    /// Read-only discipline matches AddressablesProbe: the prefab is instantiated under an INACTIVE holder
    /// (no Awake/OnEnable/Start runs), inspected, destroyed, and the bundle is unloaded with
    /// unloadAllLoadedObjects: true so nothing survives the probe.
    /// </summary>
    internal static class BundleProbe
    {
        /// <summary>Must match FalseGods.Unity/Assets/FalseGods/Editor/PocBundleBuilder.BundleFileName.</summary>
        private const string BundleFileName = "falsegods-poc-room.bundle";

        private const string PrefabName = "PocRoom";

        // Layer indices measured in-game by P0 — Docs/CollisionAndNavigationProposal.md §4.2.
        private const int GeometryLayer = 3;
        private const int GeometryNoNavMeshLayer = 22;

        public static IEnumerator Run(ProbeReport report)
        {
            report.Section("P2 — load our own AssetBundle (FalseGods.Unity, 6000.3.6f1)");

            var bundlePath = Path.Combine(Paths.BepInExRootPath, "FalseGods.Probe", BundleFileName);
            report.Value("bundle path", bundlePath);

            if (!File.Exists(bundlePath))
            {
                report.Line("  Skipped: bundle not found. Build it in FalseGods.Unity and copy it here");
                report.Line("  (or run: dotnet build tools/FalseGods.Probe -p:DeployProbe=true).");
                yield break;
            }

            report.Value("bundle size (bytes)", new FileInfo(bundlePath).Length);

            // Async load, same as the mod proper would use — the probe should exercise the realistic path.
            var request = AssetBundle.LoadFromFileAsync(bundlePath);
            yield return request;

            var bundle = request.assetBundle;
            if (bundle == null)
            {
                report.Line("  *** FAILED: AssetBundle.LoadFromFileAsync returned null. ***");
                report.Line("  R2 NEEDS ATTENTION: Unity version / build target mismatch is the usual cause.");
                yield break;
            }

            try
            {
                report.Value("bundle name", bundle.name);
                report.Value("asset names", bundle.GetAllAssetNames());

                var loadAsset = bundle.LoadAssetAsync<GameObject>(PrefabName);
                yield return loadAsset;

                var prefab = loadAsset.asset as GameObject;
                if (prefab == null)
                {
                    report.Line($"  *** FAILED: prefab '{PrefabName}' not found in the bundle. ***");
                    yield break;
                }

                report.Value("prefab name", prefab.name);
                report.Try("instantiate + inspect (isolated, no lifecycle)", () => Inspect(report, prefab));
            }
            finally
            {
                // Everything the bundle produced is destroyed with the holder inside Inspect; unload the
                // rest so a probe run leaves no trace (the mod proper will manage bundle lifetime itself).
                bundle.Unload(unloadAllLoadedObjects: true);
                report.Line("  Bundle unloaded (unloadAllLoadedObjects: true).");
            }
        }

        private static void Inspect(ProbeReport report, GameObject prefab)
        {
            GameObject holder = null;
            try
            {
                holder = new GameObject("FalseGodsProbe_BundleHolder");
                holder.SetActive(false); // inactive parent ⇒ no Awake/OnEnable/Start on the instance

                var instance = UnityEngine.Object.Instantiate(prefab, holder.transform);
                report.Value("instantiated", instance != null ? "yes" : "no");
                report.Value("instance activeInHierarchy", instance.activeInHierarchy); // expected: false

                var meshFilters = instance.GetComponentsInChildren<MeshFilter>(includeInactive: true);
                report.Value("mesh filters", meshFilters.Length);
                foreach (var filter in meshFilters)
                {
                    var mesh = filter.sharedMesh;
                    report.Value($"  {filter.gameObject.name} mesh",
                        mesh == null ? "<null>" : $"{mesh.name} ({mesh.vertexCount} verts, {mesh.triangles.Length / 3} tris)");
                }

                var renderers = instance.GetComponentsInChildren<Renderer>(includeInactive: true);
                var materials = renderers
                    .SelectMany(r => r.sharedMaterials ?? Array.Empty<Material>())
                    .ToList();
                report.Value("renderers", renderers.Length);
                report.Value("null materials", materials.Count(m => m == null));
                report.Value("shaders", materials
                    .Where(m => m != null)
                    .Select(m => m.shader == null ? "<null shader>" : m.shader.name)
                    .Distinct());
                report.Value("shaders supported", materials
                    .Where(m => m != null && m.shader != null)
                    .Select(m => $"{m.shader.name}: {(m.shader.isSupported ? "yes" : "*** NO ***")}")
                    .Distinct());

                // Layer survival: the bundle serializes layer indices; confirm they arrived intact.
                var colliders = instance.GetComponentsInChildren<Collider>(includeInactive: true);
                report.Value("colliders", colliders.Length);
                foreach (var collider in colliders)
                    report.Value($"  {collider.gameObject.name} layer",
                        $"{collider.gameObject.layer} ({LayerMask.LayerToName(collider.gameObject.layer)})");

                var floorOk = HasColliderOnLayer(colliders, "FloorCollider", GeometryLayer);
                var wallsOk = colliders.Count(c => c.gameObject.name.StartsWith("Wall", StringComparison.Ordinal)
                                                   && c.gameObject.layer == GeometryNoNavMeshLayer) == 4;
                var pillarOk = HasColliderOnLayer(colliders, "PillarCollider", GeometryLayer);

                report.Value("floor on Geometry(3)", floorOk);
                report.Value("4 walls on GeometryNoNavMesh(22)", wallsOk);
                report.Value("pillar on Geometry(3)", pillarOk);

                report.Line(floorOk && wallsOk && pillarOk && materials.All(m => m != null)
                    ? "  R2 LOOKS GOOD: our bundle loads, instantiates, and keeps meshes/materials/layers."
                    : "  R2 NEEDS ATTENTION: see the mismatches above.");
                report.Line("  (Rendering correctness — no pink — is P3, judged with the instance visible.)");
            }
            finally
            {
                if (holder != null)
                    UnityEngine.Object.Destroy(holder); // takes the instance with it
            }
        }

        private static bool HasColliderOnLayer(Collider[] colliders, string name, int layer)
        {
            return colliders.Any(c => c.gameObject.name == name && c.gameObject.layer == layer);
        }
    }
}
