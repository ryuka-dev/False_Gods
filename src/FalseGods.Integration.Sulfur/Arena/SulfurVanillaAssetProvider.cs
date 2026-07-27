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

        // Donors held for the encounter's lifetime, one AssetReference per key; released in Release(). The key is
        // whatever the catalog answers to — a room GUID for the material carriers, an asset path for the prop
        // donors — so both kinds of borrow share one cache and one lifetime.
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
            // Any depth below the parent, not just its direct children: the author groups décor under empty holder
            // objects in the prefab (a "Rock" folder per batch), which is ordinary Unity authoring and must not
            // silently cost those pieces their paint. Holders are unaffected — they carry no Renderer, and their
            // own names do not match the prefix.
            foreach (var renderer in parent.GetComponentsInChildren<Renderer>(includeInactive: true))
            {
                if (!renderer.gameObject.name.StartsWith(paint.ChildNamePrefix, StringComparison.Ordinal))
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

        public MaterialBorrowResult PaintSubmeshes(SubmeshBorrow borrow)
        {
            if (borrow == null || borrow.Rules == null || borrow.Rules.Count == 0)
                return MaterialBorrowResult.Resolved(0);

            var root = _realizedRoot();
            if (root == null)
                return MaterialBorrowResult.Failed("no realized arena root to paint decoration");

            var target = root.transform.Find(borrow.TargetPath);
            if (target == null)
                return MaterialBorrowResult.Resolved(0); // optional décor absent — not a failure

            var renderer = target.GetComponent<Renderer>();
            if (renderer == null)
                return MaterialBorrowResult.Failed($"decoration '{borrow.TargetPath}' has no Renderer to paint");

            var carrier = LoadCarrier(borrow.CarrierGuid, out var carrierError);
            if (carrier == null)
                return MaterialBorrowResult.Failed($"decoration carrier '{borrow.CarrierGuid}' did not load: {carrierError}");

            // Each sub-mesh is matched by the placeholder material it already wears, so the paint survives Unity
            // reordering sub-meshes on re-import. A slot whose placeholder no name in the rules is counted and left
            // alone: an unpainted surface is visible in-game and does not deserve failing the whole arena load.
            var materials = renderer.sharedMaterials;
            var applied = 0;
            var unmatched = new List<string>();
            for (var i = 0; i < materials.Length; i++)
            {
                var placeholder = materials[i] == null ? null : materials[i].name;
                var rule = FindRule(borrow.Rules, placeholder);
                if (rule == null)
                {
                    unmatched.Add($"{i}:'{placeholder ?? "<none>"}'");
                    continue;
                }

                var material = FindMaterial(carrier, rule.VanillaMaterialName, out var materialError);
                if (material == null)
                {
                    return MaterialBorrowResult.Failed(
                        $"decoration submaterial '{rule.VanillaMaterialName}' in carrier '{borrow.CarrierGuid}': {materialError}");
                }

                materials[i] = material;
                applied++;
            }

            renderer.sharedMaterials = materials; // reassign: the array getter returns a copy
            _logger?.Log($"[vanilla-material] {applied} submesh paint(s) on '{borrow.TargetPath}'"
                + (unmatched.Count > 0 ? $"; {unmatched.Count} slot(s) with no rule: {string.Join(", ", unmatched)}" : ""));
            return MaterialBorrowResult.Resolved(applied);
        }

        public VanillaPropResult CloneProps(VanillaPropClone request)
        {
            if (request == null)
                return VanillaPropResult.Placed(0);

            var root = _realizedRoot();
            if (root == null)
                return VanillaPropResult.Failed("no realized arena root to place props in");

            var parent = string.IsNullOrEmpty(request.ParentPath) ? root.transform : root.transform.Find(request.ParentPath);
            if (parent == null)
                return VanillaPropResult.Placed(0); // the arena authored no props at all — optional décor

            var markers = new List<Transform>();
            foreach (var candidate in parent.GetComponentsInChildren<Transform>(includeInactive: true))
            {
                if (candidate != parent && candidate.name.StartsWith(request.MarkerNamePrefix, StringComparison.Ordinal))
                    markers.Add(candidate);
            }

            if (markers.Count == 0)
                return VanillaPropResult.Placed(0);

            var layer = LayerMask.NameToLayer(request.LayerName);
            if (layer < 0)
                return VanillaPropResult.Failed($"prop layer '{request.LayerName}' does not exist in this build");

            var donor = LoadCarrier(request.RoomKey, out var donorError);
            if (donor == null)
                return VanillaPropResult.Failed($"prop donor room '{request.RoomKey}' did not load: {donorError}");

            var source = donor.transform.Find(request.PropPath);
            if (source == null)
                return VanillaPropResult.Failed($"prop '{request.PropPath}' not found in donor room '{request.RoomKey}'");

            // Clones are assembled inside an INACTIVE staging object. A vanilla prop brings the donor room's own
            // gameplay components along, and those must never run: Awake and Start do not fire while an object is
            // inactive in the hierarchy, so stripping and re-layering here means a removed component has no
            // lifecycle at all. Destruction is immediate for the same reason — a deferred Destroy would leave the
            // component alive until the end of the frame, by which point the clone is parented and active.
            var staging = new GameObject("FalseGodsPropStaging");
            staging.SetActive(false);

            var cloned = 0;
            try
            {
                foreach (var marker in markers)
                {
                    var clone = UnityEngine.Object.Instantiate(source.gameObject, staging.transform);
                    clone.name = source.name;

                    StripChildren(clone, request.StripChildNames);
                    StripComponents(clone, request.StripComponentNames);
                    SetLayerRecursively(clone.transform, layer);

                    // The marker owns placement; the clone keeps the source's own scale as its base, so a marker
                    // left at scale 1 reproduces the prop at its vanilla proportions.
                    clone.transform.SetParent(marker, worldPositionStays: false);
                    clone.transform.localPosition = Vector3.zero;
                    clone.transform.localRotation = Quaternion.identity;
                    clone.transform.localScale = source.localScale;
                    cloned++;
                }
            }
            finally
            {
                UnityEngine.Object.Destroy(staging);
            }

            _logger?.Log($"[vanilla-prop] {cloned} '{source.name}' clone(s) placed on '{request.MarkerNamePrefix}*'"
                + $", layer '{request.LayerName}'");
            return VanillaPropResult.Placed(cloned);
        }

        /// <summary>Remove whole child objects from a staged clone by name, at any depth. Used for the parts of a
        /// vanilla prop that belong to the donor room's own encounter rather than to the scenery.</summary>
        private static void StripChildren(GameObject clone, IReadOnlyList<string> names)
        {
            if (names == null || names.Count == 0)
                return;

            foreach (var transform in clone.GetComponentsInChildren<Transform>(includeInactive: true))
            {
                // A child removed earlier in this loop takes its own children with it, so the array can hold
                // already-destroyed entries by the time we reach them.
                if (transform == null || transform == clone.transform)
                    continue;

                if (Contains(names, transform.name))
                    UnityEngine.Object.DestroyImmediate(transform.gameObject);
            }
        }

        /// <summary>Remove components from a staged clone by type name, at any depth. Names rather than types:
        /// the recipe lives in Application, which has no reference to the game's assemblies.</summary>
        private static void StripComponents(GameObject clone, IReadOnlyList<string> names)
        {
            if (names == null || names.Count == 0)
                return;

            foreach (var component in clone.GetComponentsInChildren<Component>(includeInactive: true))
            {
                if (component == null || component is Transform)
                    continue;

                if (Contains(names, component.GetType().Name))
                    UnityEngine.Object.DestroyImmediate(component);
            }
        }

        private static bool Contains(IReadOnlyList<string> names, string name)
        {
            for (var i = 0; i < names.Count; i++)
            {
                if (string.Equals(names[i], name, StringComparison.Ordinal))
                    return true;
            }

            return false;
        }

        private static void SetLayerRecursively(Transform node, int layer)
        {
            node.gameObject.layer = layer;
            for (var i = 0; i < node.childCount; i++)
                SetLayerRecursively(node.GetChild(i), layer);
        }

        /// <summary>The rule whose placeholder name the slot's current material carries, or null. Exact, ordinal
        /// match: these are asset names on both sides, not user text.</summary>
        private static SubmeshMaterialRule FindRule(IReadOnlyList<SubmeshMaterialRule> rules, string placeholderName)
        {
            if (string.IsNullOrEmpty(placeholderName))
                return null;

            for (var i = 0; i < rules.Count; i++)
            {
                if (string.Equals(rules[i].PlaceholderName, placeholderName, StringComparison.Ordinal))
                    return rules[i];
            }

            return null;
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
