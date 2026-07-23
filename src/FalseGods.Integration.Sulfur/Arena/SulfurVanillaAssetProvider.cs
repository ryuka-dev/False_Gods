// Addressables / Unity interop (none of those APIs carry nullable annotations), so this file opts out of the
// nullable-reference context like the other game-facing implementations.
#nullable disable

using System;
using System.Collections.Generic;
using FalseGods.Application.Arena;
using UnityEngine;
using UnityEngine.AddressableAssets;
using UnityEngine.ResourceManagement.AsyncOperations;
using ILogger = FalseGods.RuntimeContracts.Diagnostics.ILogger;

namespace FalseGods.Integration.Sulfur.Arena
{
    /// <summary>
    /// <see cref="IVanillaAssetProvider"/> over the game's own Addressables — the productionised form of the
    /// F11/P1a probe path (boss #1 roadmap P1, direction B; Docs/MaterialCompatibilityReport.md §3.1).
    /// </summary>
    /// <remarks>
    /// Each borrow names a donor <em>carrier</em> Room prefab by GUID plus a material NAME (vanilla materials are
    /// not individually addressable — P1a). The carrier is loaded <b>synchronously</b> from the player's own
    /// install (<see cref="AsyncOperationHandle{TObject}.WaitForCompletion"/>): arena entry is a loading beat, the
    /// call is on the main thread, and it keeps the load flow's synchronous contract (Strategy B). Carriers are
    /// cached per GUID so several borrows from one carrier load it once, and every handle is released on
    /// <see cref="Release"/>.
    /// <para>
    /// Pure presentation: only <c>sharedMaterials</c> is touched on our own realized renderer. No collision,
    /// navigation, spawn, or other authoritative state is ever taken from the carrier (host authority, single
    /// ownership). Fail-closed: a carrier that will not load, a material name that resolves to zero or more than
    /// one distinct material, an absent target path, a target with no renderer, or a sub-material index out of
    /// range aborts the whole resolve — never a partial paint. The realized arena root arrives by composition-time
    /// injection (a <see cref="Func{GameObject}"/>), keeping this signature-compatible with the Unity-free port.
    /// </para>
    /// </remarks>
    public sealed class SulfurVanillaAssetProvider : IVanillaAssetProvider
    {
        private readonly Func<GameObject> _realizedRoot;
        private readonly ILogger _logger;

        // Carriers held for the encounter's lifetime, one AssetReference per GUID; released in Release().
        private readonly Dictionary<string, LoadedCarrier> _carriers = new Dictionary<string, LoadedCarrier>(StringComparer.Ordinal);

        public SulfurVanillaAssetProvider(Func<GameObject> realizedRoot, ILogger logger = null)
        {
            _realizedRoot = realizedRoot ?? throw new ArgumentNullException(nameof(realizedRoot));
            _logger = logger;
        }

        public MaterialBorrowResult Resolve(IReadOnlyList<MaterialBorrowRequest> requests)
        {
            if (requests == null || requests.Count == 0)
                return MaterialBorrowResult.Resolved(0);

            var root = _realizedRoot();
            if (root == null)
                return MaterialBorrowResult.Failed("no realized arena root to paint");

            var applied = 0;
            foreach (var request in requests)
            {
                var carrier = LoadCarrier(request.CarrierGuid, out var carrierError);
                if (carrier == null)
                    return MaterialBorrowResult.Failed($"carrier '{request.CarrierGuid}' did not load: {carrierError}");

                var material = FindMaterial(carrier, request.MaterialName, out var materialError);
                if (material == null)
                {
                    return MaterialBorrowResult.Failed(
                        $"material '{request.MaterialName}' in carrier '{request.CarrierGuid}': {materialError}");
                }

                var target = root.transform.Find(request.TargetPath);
                if (target == null)
                    return MaterialBorrowResult.Failed($"target path '{request.TargetPath}' not found in the realized arena");

                var renderer = target.GetComponent<Renderer>();
                if (renderer == null)
                    return MaterialBorrowResult.Failed($"node at '{request.TargetPath}' has no Renderer to paint");

                var materials = renderer.sharedMaterials;
                if (request.TargetSubMaterialIndex < 0 || request.TargetSubMaterialIndex >= materials.Length)
                {
                    return MaterialBorrowResult.Failed(
                        $"sub-material index {request.TargetSubMaterialIndex} out of range (renderer at " +
                        $"'{request.TargetPath}' has {materials.Length}) ");
                }

                materials[request.TargetSubMaterialIndex] = material;
                renderer.sharedMaterials = materials; // reassign: the array getter returns a copy
                applied++;
            }

            _logger?.Log($"[vanilla-material] {applied} borrow(s) applied from {_carriers.Count} carrier(s)");
            return MaterialBorrowResult.Resolved(applied);
        }

        public MaterialBorrowResult PaintByConvention(MaterialConventionPaint paint)
        {
            if (paint == null)
                return MaterialBorrowResult.Resolved(0);

            var root = _realizedRoot();
            if (root == null)
                return MaterialBorrowResult.Failed("no realized arena root to paint decoration");

            var carrier = LoadCarrier(paint.CarrierGuid, out var carrierError);
            if (carrier == null)
                return MaterialBorrowResult.Failed($"decoration carrier '{paint.CarrierGuid}' did not load: {carrierError}");

            var material = FindMaterial(carrier, paint.MaterialName, out var materialError);
            if (material == null)
            {
                return MaterialBorrowResult.Failed(
                    $"decoration material '{paint.MaterialName}' in carrier '{paint.CarrierGuid}': {materialError}");
            }

            var parent = string.IsNullOrEmpty(paint.ParentPath) ? root.transform : root.transform.Find(paint.ParentPath);
            if (parent == null)
                return MaterialBorrowResult.Failed($"decoration parent path '{paint.ParentPath}' not found in the realized arena");

            var applied = 0;
            foreach (Transform child in parent)
            {
                if (!child.name.StartsWith(paint.ChildNamePrefix, StringComparison.Ordinal))
                    continue;

                var renderer = child.GetComponent<Renderer>();
                if (renderer == null)
                    continue;

                var materials = renderer.sharedMaterials;
                if (paint.SubMaterialIndex < 0 || paint.SubMaterialIndex >= materials.Length)
                    continue; // a decoration renderer without that slot is skipped, not fatal

                materials[paint.SubMaterialIndex] = material;
                renderer.sharedMaterials = materials; // reassign: the array getter returns a copy
                applied++;
            }

            _logger?.Log($"[vanilla-material] {applied} decoration paint(s) of '{paint.MaterialName}' on '{paint.ChildNamePrefix}*'");
            return MaterialBorrowResult.Resolved(applied);
        }

        public void Release()
        {
            if (_carriers.Count == 0)
                return;

            foreach (var carrier in _carriers.Values)
            {
                try { carrier.Reference.ReleaseAsset(); }
                catch (Exception) { /* not loaded / already released */ }
            }

            _logger?.Log($"[vanilla-material] {_carriers.Count} carrier(s) released");
            _carriers.Clear();
        }

        private GameObject LoadCarrier(string guid, out string error)
        {
            error = null;
            if (_carriers.TryGetValue(guid, out var cached))
                return cached.Prefab;

            AssetReference reference = null;
            try
            {
                reference = new AssetReference(guid);
                var handle = reference.LoadAssetAsync<GameObject>();
                var prefab = handle.WaitForCompletion();
                if (handle.Status != AsyncOperationStatus.Succeeded || prefab == null)
                {
                    error = $"status={handle.Status}";
                    SafeRelease(reference);
                    return null;
                }

                _carriers[guid] = new LoadedCarrier(reference, prefab);
                return prefab;
            }
            catch (Exception exception)
            {
                error = exception.Message;
                SafeRelease(reference);
                return null;
            }
        }

        /// <summary>The single material named <paramref name="name"/> on the carrier, or null with a reason when
        /// none or more than one distinct material carries that name (fail-closed — an ambiguous name is not a
        /// safe selector).</summary>
        private static Material FindMaterial(GameObject carrier, string name, out string error)
        {
            error = null;
            var found = new List<Material>();
            foreach (var renderer in carrier.GetComponentsInChildren<Renderer>(includeInactive: true))
            {
                var materials = renderer.sharedMaterials;
                if (materials == null)
                    continue;
                foreach (var material in materials)
                {
                    if (material != null && material.name == name && !found.Contains(material))
                        found.Add(material);
                }
            }

            if (found.Count == 0)
            {
                error = "no material with that name on the carrier";
                return null;
            }

            if (found.Count > 1)
            {
                error = $"ambiguous — {found.Count} distinct materials share that name";
                return null;
            }

            return found[0];
        }

        private static void SafeRelease(AssetReference reference)
        {
            if (reference == null)
                return;
            try { reference.ReleaseAsset(); }
            catch (Exception) { /* not loaded / already released */ }
        }

        private readonly struct LoadedCarrier
        {
            public LoadedCarrier(AssetReference reference, GameObject prefab)
            {
                Reference = reference;
                Prefab = prefab;
            }

            public AssetReference Reference { get; }

            public GameObject Prefab { get; }
        }
    }
}
