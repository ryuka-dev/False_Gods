using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using BepInEx;
using PerfectRandom.Sulfur.Core.LevelGeneration;
using UnityEngine;
using UnityEngine.AddressableAssets;
using UnityEngine.ResourceManagement.AsyncOperations;

namespace FalseGods.Probe
{
    /// <summary>
    /// PoC step P3 — RiskList R6/R13, MaterialCompatibilityReport §3.4/§3.6: does a vanilla cave prefab
    /// render correctly (no pink) under OUR LightingRoot, and does one vanilla floor material behave when
    /// assigned to our own flat ground mesh? Neither question can be answered by reading fields — pink is a
    /// visual failure — so this section deliberately makes objects VISIBLE and leaves them up for the human
    /// to judge, then tears them down. It is the one place the probe departs from P0–P2's inactive-holder
    /// read-only discipline, and it does so as narrowly as possible:
    ///
    ///   - Our own room (the P2 bundle, now carrying a real LightingRoot) is instantiated active — it has no
    ///     MonoBehaviours, so nothing but its lights/renderers/colliders comes alive.
    ///   - The vanilla prefab is instantiated under an INACTIVE holder, has every MonoBehaviour stripped
    ///     while still inactive (so no Awake/OnEnable/Start ever runs), and only THEN is shown. What remains
    ///     is meshes + materials + shaders — exactly the render path P3 needs to judge, and nothing that
    ///     could register with a manager, spawn, or mutate world state.
    ///   - Global RenderSettings (ambient/fog — scene state a prefab cannot carry) are optionally applied and
    ///     always restored on teardown.
    ///
    /// PASS/FAIL is a human judgement made with your eyes while the stage is up; this probe only builds the
    /// stage, records what it built, and cleans it up. Press the visual hotkey once to raise it, again to
    /// drop it.
    /// </summary>
    internal sealed class VisualProbe
    {
        private const string BundleFileName = "falsegods-poc-room.bundle";
        private const string OurRoomPrefabName = "PocRoom";

        // Where the stage appears relative to the camera, so you see it the moment you raise it.
        private const float RoomForwardDistance = 18f;
        private const float VanillaSideOffset = 14f;
        private const float EyeToFootDrop = 1.6f;

        private AssetBundle _bundle;
        private AssetReference _vanillaRef;
        private GameObject _ourRoom;
        private GameObject _vanilla;

        private bool _envApplied;
        private EnvironmentState _savedEnv;

        public bool IsUp => _ourRoom != null || _vanilla != null || _bundle != null;

        /// <summary>Raises the visible P3 stage. Coroutine: it awaits the bundle and Addressables loads.</summary>
        public IEnumerator Raise(ProbeReport report, bool applyEnvironment)
        {
            report.Section("P3 — VISIBLE render check (judge with your eyes)");

            var camera = Camera.main;
            if (camera == null)
            {
                report.Line("  Skipped: no Camera.main. Enter a level and stand in it, then press the key again.");
                yield break;
            }

            var origin = StageOrigin(camera, out var right);

            // ── our own room: the P2 bundle, now with a LightingRoot ──────────────────────────────────
            var bundlePath = Path.Combine(Paths.BepInExRootPath, "FalseGods.Probe", BundleFileName);
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
                Drop(report);
                yield break;
            }

            report.Try("instantiate our room (active — its LightingRoot lights the stage)", () =>
            {
                _ourRoom = UnityEngine.Object.Instantiate(ourPrefab, origin, Quaternion.identity);
                _ourRoom.name = "FalseGodsP3_OurRoom";
                report.Value("our room at", origin);
                report.Value("our room lights", _ourRoom.GetComponentsInChildren<Light>(true).Length);
            });

            // ── the vanilla prefab, rendered next to our lighting, scripts stripped ────────────────────
            yield return RaiseVanilla(report, origin + right * VanillaSideOffset);

            // ── the "vanilla material on our own ground mesh" test (report §3.4) ───────────────────────
            report.Try("swap a vanilla floor material onto our ground mesh", () => SwapFloorMaterial(report));

            // ── ambient/fog: scene state the prefab cannot carry; apply and remember to restore ────────
            if (applyEnvironment)
                report.Try("apply basic ambient/fog (restored on teardown)", () => ApplyEnvironment(report));

            report.Line();
            report.Line("  >>> NOW LOOK, with the stage in front of you:");
            report.Line("      1. Is the vanilla prefab PINK/black, or correctly textured and lit?  (R6/R13)");
            report.Line("      2. Does the vanilla floor material sit right on our flat floor, or swim/mis-scale? (§3.4)");
            report.Line("      3. Is the arena lit by OUR LightingRoot (it should read as lit even where the");
            report.Line("         level's own lights don't reach)?");
            report.Line("  Press the visual hotkey again to tear the stage down and restore the environment.");
        }

        private IEnumerator RaiseVanilla(ProbeReport report, Vector3 position)
        {
            var guid = FirstRoomGuid(report);
            if (guid == null)
            {
                report.Line("  Vanilla prefab skipped: no room GUID (generate a level first).");
                yield break;
            }

            _vanillaRef = new AssetReference(guid);
            AsyncOperationHandle<GameObject> load = default;
            var started = false;
            try
            {
                load = _vanillaRef.LoadAssetAsync<GameObject>();
                started = true;
            }
            catch (Exception exception)
            {
                report.Failure($"LoadAssetAsync({guid})", exception);
            }

            if (!started)
                yield break;

            yield return load;
            if (load.Status != AsyncOperationStatus.Succeeded || load.Result == null)
            {
                report.Line($"  Vanilla prefab load FAILED: status={load.Status}");
                yield break;
            }

            var prefab = load.Result;
            report.Try("instantiate vanilla prefab (scripts stripped, then shown)", () =>
            {
                // Instantiate INACTIVE, strip every MonoBehaviour while nothing can Awake, THEN activate:
                // what shows is renderers + meshes + materials only — a pure render check, no gameplay.
                var holder = new GameObject("FalseGodsP3_VanillaHolder");
                holder.SetActive(false);
                var instance = UnityEngine.Object.Instantiate(prefab, holder.transform);

                var scripts = instance.GetComponentsInChildren<MonoBehaviour>(true);
                var stripped = 0;
                foreach (var script in scripts)
                {
                    if (script == null)
                        continue;
                    try { UnityEngine.Object.DestroyImmediate(script); stripped++; }
                    catch (Exception) { /* [RequireComponent] can block removal; count what's left below */ }
                }
                var remaining = instance.GetComponentsInChildren<MonoBehaviour>(true).Count(m => m != null);
                report.Value("vanilla prefab", prefab.name);
                report.Value("MonoBehaviours stripped / left", $"{stripped} / {remaining}");

                var renderers = instance.GetComponentsInChildren<Renderer>(true);
                var mats = renderers.SelectMany(r => r.sharedMaterials ?? Array.Empty<Material>()).ToList();
                report.Value("vanilla renderers", renderers.Length);
                report.Value("vanilla null materials", mats.Count(m => m == null));
                report.Value("vanilla shaders", mats.Where(m => m != null && m.shader != null)
                    .Select(m => $"{m.shader.name}: {(m.shader.isSupported ? "supported" : "*** NOT SUPPORTED ***")}")
                    .Distinct());

                // Reparent out of the holder into the world, at the stage position, then show it.
                instance.transform.SetParent(null, worldPositionStays: false);
                instance.transform.position = position;
                instance.name = "FalseGodsP3_Vanilla";
                instance.SetActive(true);
                UnityEngine.Object.Destroy(holder);

                _vanilla = instance;
                report.Value("vanilla shown at", position);
            });
        }

        /// <summary>
        /// Takes one material off the vanilla prefab and puts it on our own flat floor mesh — the report §3.4
        /// test. If the material is triplanar/world-projected it should look right on our mesh; if it needs
        /// authored UVs / vertex colours / lightmap UV2 it will look wrong. That verdict is yours, on screen.
        /// </summary>
        private void SwapFloorMaterial(ProbeReport report)
        {
            if (_vanilla == null || _ourRoom == null)
            {
                report.Line("  Skipped: need both the vanilla prefab and our room up.");
                return;
            }

            var floorRenderer = FindRenderer(_ourRoom, "Floor");
            if (floorRenderer == null)
            {
                report.Line("  Skipped: our room has no 'Floor' renderer.");
                return;
            }

            var vanillaMats = _vanilla.GetComponentsInChildren<Renderer>(true)
                .SelectMany(r => r.sharedMaterials ?? Array.Empty<Material>())
                .Where(m => m != null)
                .ToList();

            // Prefer a material that looks like a floor/ground; otherwise the first available.
            var chosen = vanillaMats.FirstOrDefault(m =>
                             m.name.IndexOf("floor", StringComparison.OrdinalIgnoreCase) >= 0 ||
                             m.name.IndexOf("ground", StringComparison.OrdinalIgnoreCase) >= 0)
                         ?? vanillaMats.FirstOrDefault();

            if (chosen == null)
            {
                report.Line("  Skipped: the vanilla prefab exposed no material to borrow.");
                return;
            }

            floorRenderer.sharedMaterial = chosen;
            report.Value("floor material now", chosen.name);
            report.Value("floor material shader", chosen.shader == null ? "<null>" : chosen.shader.name);
        }

        private void ApplyEnvironment(ProbeReport report)
        {
            _savedEnv = EnvironmentState.Capture();

            RenderSettings.ambientMode = UnityEngine.Rendering.AmbientMode.Flat;
            RenderSettings.ambientLight = new Color(0.10f, 0.11f, 0.13f);
            RenderSettings.fog = true;
            RenderSettings.fogMode = FogMode.Linear;
            RenderSettings.fogColor = new Color(0.05f, 0.05f, 0.06f);
            RenderSettings.fogStartDistance = 12f;
            RenderSettings.fogEndDistance = 70f;

            _envApplied = true;
            report.Line("  ambient=flat dark, fog=linear 12..70 (global RenderSettings — will be restored).");
        }

        /// <summary>Drops the stage and restores everything the raise touched.</summary>
        public void Drop(ProbeReport report)
        {
            report.Section("P3 — teardown");

            if (_envApplied)
            {
                _savedEnv.Restore();
                _envApplied = false;
                report.Line("  RenderSettings restored.");
            }

            if (_ourRoom != null) { UnityEngine.Object.Destroy(_ourRoom); _ourRoom = null; }
            if (_vanilla != null) { UnityEngine.Object.Destroy(_vanilla); _vanilla = null; }

            if (_vanillaRef != null)
            {
                try { _vanillaRef.ReleaseAsset(); } catch (Exception) { /* not loaded */ }
                _vanillaRef = null;
                report.Line("  Vanilla Addressables handle released.");
            }

            if (_bundle != null)
            {
                _bundle.Unload(unloadAllLoadedObjects: true);
                _bundle = null;
                report.Line("  Bundle unloaded (unloadAllLoadedObjects: true).");
            }

            report.Line("  Stage down. Nothing from the probe remains in the level.");
        }

        private static Vector3 StageOrigin(Camera camera, out Vector3 right)
        {
            var forward = camera.transform.forward;
            forward.y = 0f;
            forward = forward.sqrMagnitude > 1e-4f ? forward.normalized : Vector3.forward;
            right = Vector3.Cross(Vector3.up, forward); // forward's right-hand side, on the ground plane

            var foot = camera.transform.position;
            foot.y -= EyeToFootDrop;
            return foot + forward * RoomForwardDistance;
        }

        private static string FirstRoomGuid(ProbeReport report)
        {
            string found = null;
            report.Try("find a vanilla room GUID (game's own LevelBlock assets)", () =>
            {
                foreach (var block in Resources.FindObjectsOfTypeAll<LevelBlock>())
                {
                    var references = block.roomPrefabsAddressable;
                    if (references == null)
                        continue;
                    foreach (var reference in references)
                    {
                        if (reference != null && !string.IsNullOrEmpty(reference.AssetGUID))
                        {
                            found = reference.AssetGUID;
                            return;
                        }
                    }
                }
            });
            return found;
        }

        private static Renderer FindRenderer(GameObject root, string childName)
        {
            return root.GetComponentsInChildren<Renderer>(true)
                .FirstOrDefault(r => string.Equals(r.gameObject.name, childName, StringComparison.Ordinal));
        }

        /// <summary>Snapshot of the global lighting environment so a raise can put it back exactly.</summary>
        private readonly struct EnvironmentState
        {
            private readonly UnityEngine.Rendering.AmbientMode _ambientMode;
            private readonly Color _ambientLight;
            private readonly float _ambientIntensity;
            private readonly bool _fog;
            private readonly Color _fogColor;
            private readonly FogMode _fogMode;
            private readonly float _fogStart;
            private readonly float _fogEnd;

            private EnvironmentState(UnityEngine.Rendering.AmbientMode ambientMode, Color ambientLight,
                float ambientIntensity, bool fog, Color fogColor, FogMode fogMode, float fogStart, float fogEnd)
            {
                _ambientMode = ambientMode;
                _ambientLight = ambientLight;
                _ambientIntensity = ambientIntensity;
                _fog = fog;
                _fogColor = fogColor;
                _fogMode = fogMode;
                _fogStart = fogStart;
                _fogEnd = fogEnd;
            }

            public static EnvironmentState Capture() => new EnvironmentState(
                RenderSettings.ambientMode, RenderSettings.ambientLight, RenderSettings.ambientIntensity,
                RenderSettings.fog, RenderSettings.fogColor, RenderSettings.fogMode,
                RenderSettings.fogStartDistance, RenderSettings.fogEndDistance);

            public void Restore()
            {
                RenderSettings.ambientMode = _ambientMode;
                RenderSettings.ambientLight = _ambientLight;
                RenderSettings.ambientIntensity = _ambientIntensity;
                RenderSettings.fog = _fog;
                RenderSettings.fogColor = _fogColor;
                RenderSettings.fogMode = _fogMode;
                RenderSettings.fogStartDistance = _fogStart;
                RenderSettings.fogEndDistance = _fogEnd;
            }
        }
    }
}
