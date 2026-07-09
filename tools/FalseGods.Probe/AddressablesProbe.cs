using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using PerfectRandom.Sulfur.Core.LevelGeneration;
using UnityEngine;
using UnityEngine.AddressableAssets;
using UnityEngine.ResourceManagement.AsyncOperations;
using UnityEngine.ResourceManagement.ResourceLocations;

namespace FalseGods.Probe
{
    /// <summary>
    /// PoC step P1 — RiskList R1: can a mod resolve and load vanilla room prefabs from the player's own
    /// install, at runtime, by Addressables key/GUID?
    ///
    /// The whole "reuse vanilla assets, redistribute nothing" strategy rests on yes.
    ///
    /// The GUIDs are NOT hardcoded from a wiki or from the decompile. They are read out of the game's own
    /// LevelBlock.roomPrefabsAddressable at runtime, so this answers the real question — "are the keys the
    /// game itself holds resolvable from our code" — rather than the easier question "do these particular
    /// strings still work".
    ///
    /// Read-only: locations are queried, one prefab is loaded and released, and nothing is instantiated.
    /// </summary>
    internal static class AddressablesProbe
    {
        /// <summary>How many distinct room GUIDs to resolve. Enough to be convincing, few enough to be quick.</summary>
        private const int GuidsToResolve = 5;

        public static IEnumerator Run(ProbeReport report)
        {
            report.Section("P1 — Addressables catalog");

            var keySamples = new List<string>();

            report.Try("resource locators", () =>
            {
                var locators = Addressables.ResourceLocators?.ToList();
                report.Value("locator count", locators?.Count ?? 0);

                if (locators == null)
                    return;

                foreach (var locator in locators)
                {
                    var keys = locator.Keys?.ToList();
                    report.Value($"[{locator.LocatorId}] key count", keys?.Count ?? 0);

                    if (keys == null)
                        continue;

                    keySamples.AddRange(keys.Take(5).Select(k => k?.ToString() ?? "<null>"));
                }
            });

            if (keySamples.Count > 0)
            {
                report.Line("  sample keys:");
                foreach (var key in keySamples.Take(10))
                    report.Line($"    {key}");
            }

            // ── the real question: the GUIDs the game itself uses for rooms ───────────────────────────
            report.Section("P1 — room GUIDs, read from the game's own LevelBlock assets");

            var guids = new List<string>();

            report.Try("LevelBlock discovery", () =>
            {
                var levelBlocks = Resources.FindObjectsOfTypeAll<LevelBlock>();
                report.Value("LevelBlock assets loaded", levelBlocks.Length);

                foreach (var block in levelBlocks)
                {
                    var references = block.roomPrefabsAddressable;
                    if (references == null)
                        continue;

                    foreach (var reference in references)
                    {
                        if (reference == null || string.IsNullOrEmpty(reference.AssetGUID))
                            continue;

                        if (!guids.Contains(reference.AssetGUID))
                            guids.Add(reference.AssetGUID);
                    }
                }

                report.Value("distinct room GUIDs found", guids.Count);

                if (guids.Count == 0)
                {
                    report.Line("  None. LevelBlock ScriptableObjects are probably not loaded yet —");
                    report.Line("  run this again once a level has generated.");
                }
            });

            // ── does AssetReference.RuntimeKeyIsValid agree that these are resolvable? ────────────────
            if (guids.Count > 0)
            {
                report.Section("P1 — AssetReference.RuntimeKeyIsValid()");
                report.Try("RuntimeKeyIsValid", () =>
                {
                    foreach (var guid in guids.Take(GuidsToResolve))
                    {
                        var reference = new AssetReference(guid);
                        report.Value(guid, reference.RuntimeKeyIsValid() ? "valid" : "*** INVALID ***");
                    }
                });
            }

            // ── LoadResourceLocationsAsync: the cheap "does this key exist" probe from RiskList R1 ────
            report.Section("P1 — LoadResourceLocationsAsync (hits / misses)");

            var resolved = 0;
            var attempted = 0;

            foreach (var guid in guids.Take(GuidsToResolve))
            {
                attempted++;

                AsyncOperationHandle<IList<IResourceLocation>> handle = default;
                var started = false;

                try
                {
                    handle = Addressables.LoadResourceLocationsAsync(guid, typeof(GameObject));
                    started = true;
                }
                catch (Exception exception)
                {
                    report.Failure($"LoadResourceLocationsAsync({guid})", exception);
                }

                if (!started)
                    continue;

                yield return handle;

                if (handle.Status == AsyncOperationStatus.Succeeded && handle.Result != null && handle.Result.Count > 0)
                {
                    resolved++;
                    var location = handle.Result[0];
                    report.Line($"  HIT  {guid}");
                    report.Line($"        PrimaryKey   {location.PrimaryKey}");
                    report.Line($"        InternalId   {location.InternalId}");
                    report.Line($"        ResourceType {location.ResourceType?.Name}");
                    report.Line($"        ProviderId   {location.ProviderId}");
                }
                else
                {
                    report.Line($"  MISS {guid}  status={handle.Status}");
                }

                Addressables.Release(handle);
            }

            report.Value("resolved / attempted", $"{resolved} / {attempted}");
            report.Line(resolved > 0 && resolved == attempted
                ? "  R1 LOOKS GOOD: the game's own room GUIDs resolve from mod code."
                : "  R1 NEEDS ATTENTION: see misses above.");

            // ── actually load one prefab, the way ArenaLoadingProposal §2.3 intends to ────────────────
            report.Section("P1 — load one vanilla room prefab (no instantiation)");

            var loadGuid = guids.FirstOrDefault();
            if (loadGuid == null)
            {
                report.Line("  Skipped: no GUID available.");
                yield break;
            }

            // Uses the game's own API shape: new AssetReference(guid).LoadAssetAsync<GameObject>(),
            // exactly as LevelGenGraphUtilities does.
            var reference2 = new AssetReference(loadGuid);
            AsyncOperationHandle<GameObject> load = default;
            var loadStarted = false;

            try
            {
                load = reference2.LoadAssetAsync<GameObject>();
                loadStarted = true;
            }
            catch (Exception exception)
            {
                report.Failure($"LoadAssetAsync({loadGuid})", exception);
            }

            if (!loadStarted)
                yield break;

            yield return load;

            if (load.Status != AsyncOperationStatus.Succeeded || load.Result == null)
            {
                report.Line($"  FAILED to load {loadGuid}: status={load.Status}");
                report.Value("exception", load.OperationException?.Message);
            }
            else
            {
                var prefab = load.Result;
                report.Value("prefab name", prefab.name);
                report.Value("child count", prefab.transform.childCount);

                report.Try("renderer/shader survey (feeds R6)", () =>
                {
                    var renderers = prefab.GetComponentsInChildren<Renderer>(includeInactive: true);
                    report.Value("renderers", renderers.Length);

                    var shaders = renderers
                        .SelectMany(r => r.sharedMaterials ?? Array.Empty<Material>())
                        .Where(m => m != null && m.shader != null)
                        .Select(m => m.shader.name)
                        .Distinct()
                        .Take(10)
                        .ToList();

                    report.Value("distinct shaders (max 10)", shaders);

                    var colliders = prefab.GetComponentsInChildren<Collider>(includeInactive: true);
                    report.Value("colliders", colliders.Length);
                    report.Value("collider layers", colliders.Select(c => c.gameObject.layer.ToString()).Distinct());
                });

                report.Line("  Loaded and inspected without instantiating. Releasing.");
            }

            reference2.ReleaseAsset();
        }
    }
}
