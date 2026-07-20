using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using PerfectRandom.Sulfur.Core.LevelGeneration;
using UnityEngine;
using UnityEngine.AddressableAssets;
using UnityEngine.ResourceManagement.AsyncOperations;

namespace FalseGods.Probe
{
    /// <summary>
    /// P1a — vanilla material-borrow SELECTOR reliability (boss #1 roadmap P1, direction B).
    ///
    /// Direction B authors our OWN large-cave meshes and borrows vanilla cave wall/floor MATERIALS onto them.
    /// Vanilla materials are NOT individually addressable (only whole Rooms are, by GUID — see
    /// ArenaResourceArchitecture §1.2), so the production donor selector must be a *carrier* selector:
    ///
    ///     (Room prefab GUID) + (hierarchy PATH from the room root) + (sub-material index)
    ///
    /// — never the F11 probe's "first material whose name contains 'floor'" guess, which is not stable across
    /// rooms or game updates. This step proves that carrier selector actually RESOLVES and is STABLE, so the
    /// production <c>IVanillaAssetProvider</c> material-borrow path can rely on it:
    ///
    ///   1. Enumerate the loaded cave Room prefabs (GUIDs on <c>LevelBlock.roomPrefabsAddressable</c>).
    ///   2. Load each read-only (NO instantiate, so no component lifecycle runs), scope to <c>Room.Structure</c>
    ///      (the wall/floor geometry root — not <c>Decoration</c>/props).
    ///   3. Group the structural renderers by distinct material; for each material emit ONE representative
    ///      selector (guid, path, sub-material index) with its shader + <c>isSupported</c> and how many
    ///      renderers wear it (popularity ≈ the big floor/wall surfaces).
    ///   4. Re-resolve each selector via <c>root.Find(path).sharedMaterials[index]</c> and classify it:
    ///      <b>RELIABLE</b> (path unique + resolves + index maps to the same material), <b>AMBIGUOUS</b>
    ///      (a duplicate sibling name on the path — <c>Find</c> is first-match, unsafe), <b>UNRESOLVED</b>, or
    ///      <b>INDEX-DRIFT</b>. Only RELIABLE triples are safe production selectors.
    ///
    /// Read-only: it loads prefabs, reads them, and releases every Addressables handle. It touches no nav graph
    /// and no game state. Run it standing in the CAVE level whose look you want to reuse (its LevelBlocks must be
    /// loaded). This does NOT re-judge pink/no-pink — F11 already proved a borrowed vanilla material renders
    /// correctly on our mesh; P1a proves the SELECTOR that names which material to borrow.
    /// </summary>
    internal sealed class VanillaMaterialProbe
    {
        // Survey caps: enough distinct rooms/materials to choose a donor, short enough to read.
        private const int MaxRooms = 6;
        private const int MaxMaterialsPerRoom = 24;

        public IEnumerator Run(ProbeReport report)
        {
            report.Section("P1a — vanilla material-borrow selector (carrier GUID + path + submat index)");

            var guids = CollectRoomGuids(report);
            if (guids.Count == 0)
            {
                report.Line("  Skipped: no Room GUIDs found. Stand in a CAVE level (its LevelBlocks must be");
                report.Line("  loaded) and press the key again.");
                yield break;
            }

            report.Value("distinct room GUIDs found", guids.Count);
            report.Value("surveying up to", MaxRooms);

            var surveyed = 0;
            foreach (var guid in guids)
            {
                if (surveyed >= MaxRooms)
                    break;
                yield return SurveyRoom(report, guid);
                surveyed++;
            }

            report.Line();
            report.Line("  >>> Pick a donor: copy a (guid, path, submat) line whose material is a wall/floor you");
            report.Line("      want AND whose verdict is RELIABLE (never AMBIGUOUS/UNRESOLVED). That triple is the");
            report.Line("      production material-borrow selector the exporter will emit and the runtime resolves.");
        }

        /// <summary>Distinct non-empty room-prefab GUIDs across every loaded <see cref="LevelBlock"/>. In a cave
        /// level these are the cave rooms; order is preserved (first-seen) so the survey is deterministic.</summary>
        private static List<string> CollectRoomGuids(ProbeReport report)
        {
            var guids = new List<string>();
            var seen = new HashSet<string>(StringComparer.Ordinal);

            report.Try("enumerate LevelBlock room GUIDs (the game's own addressable rooms)", () =>
            {
                var blocks = Resources.FindObjectsOfTypeAll<LevelBlock>();
                report.Value("loaded LevelBlocks", blocks.Length);
                foreach (var block in blocks)
                {
                    var references = block.roomPrefabsAddressable;
                    if (references == null)
                        continue;
                    foreach (var reference in references)
                    {
                        var guid = reference?.AssetGUID;
                        if (!string.IsNullOrEmpty(guid) && seen.Add(guid))
                            guids.Add(guid);
                    }
                }
            });

            return guids;
        }

        private IEnumerator SurveyRoom(ProbeReport report, string guid)
        {
            report.Line();
            report.Value("── room GUID", guid);

            var reference = new AssetReference(guid);
            AsyncOperationHandle<GameObject> load = default;
            var started = false;
            try
            {
                load = reference.LoadAssetAsync<GameObject>();
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
                report.Value("load", $"FAILED (status={load.Status})");
                SafeRelease(reference);
                yield break;
            }

            var prefab = load.Result;
            report.Try("survey structural materials", () => SurveyPrefab(report, prefab));
            SafeRelease(reference);
        }

        private static void SurveyPrefab(ProbeReport report, GameObject prefab)
        {
            report.Value("prefab", prefab.name);

            var room = prefab.GetComponent<Room>();
            Transform scope;
            if (room == null)
            {
                report.Value("Room component", "<none on root> — scoping to whole prefab");
                scope = prefab.transform;
            }
            else if (room.Structure == null)
            {
                report.Value("Room.Structure", "<null> — scoping to whole prefab");
                scope = prefab.transform;
            }
            else
            {
                report.Value("Room.Structure", room.Structure.name);
                scope = room.Structure.transform;
            }

            // Group by distinct material so the author sees one selector per material, not one per renderer.
            // Representative = the first renderer/sub-material wearing that material; usage counts popularity.
            var byMaterial = new Dictionary<Material, MaterialUse>();
            var renderers = scope.GetComponentsInChildren<Renderer>(includeInactive: true);
            report.Value("structural renderers", renderers.Length);

            foreach (var renderer in renderers)
            {
                var mats = renderer.sharedMaterials;
                if (mats == null)
                    continue;
                for (var i = 0; i < mats.Length; i++)
                {
                    var mat = mats[i];
                    if (mat == null)
                        continue;
                    if (!byMaterial.TryGetValue(mat, out var use))
                    {
                        use = new MaterialUse
                        {
                            RepresentativeRenderer = renderer.transform,
                            SubMaterialIndex = i,
                            Path = PathFromRoot(prefab.transform, renderer.transform),
                        };
                        byMaterial[mat] = use;
                    }
                    use.RendererCount++;
                }
            }

            report.Value("distinct structural materials", byMaterial.Count);

            var ordered = byMaterial
                .OrderByDescending(kv => kv.Value.RendererCount)
                .Take(MaxMaterialsPerRoom)
                .ToList();

            foreach (var entry in ordered)
            {
                var mat = entry.Key;
                var use = entry.Value;
                var shader = mat.shader != null ? mat.shader.name : "<null shader>";
                var supported = mat.shader != null && mat.shader.isSupported;
                var verdict = SelectorVerdict(prefab.transform, use, mat);
                var hint = LooksLikeSurface(mat.name) ? "  <- wall/floor?" : "";

                report.Line(
                    $"    [{verdict,-11}] submat {use.SubMaterialIndex}  x{use.RendererCount,-4}  " +
                    $"'{mat.name}'  ({shader}, supported={supported}){hint}");
                report.Line($"                   path: {use.Path ?? "<not under root>"}");
            }

            if (byMaterial.Count > ordered.Count)
                report.Value("(materials omitted)", byMaterial.Count - ordered.Count);
        }

        /// <summary>Classify a selector's stability. Only RELIABLE is a safe production donor selector.</summary>
        private static string SelectorVerdict(Transform root, MaterialUse use, Material expected)
        {
            if (use.Path == null)
                return "NO-PATH";

            // A duplicate sibling name anywhere on the path makes Transform.Find (first-match) unsafe, even if
            // it happens to hit the right node today.
            if (!PathIsUnambiguous(root, use.Path))
                return "AMBIGUOUS";

            var found = root.Find(use.Path);
            if (found == null || found != use.RepresentativeRenderer)
                return "UNRESOLVED";

            var renderer = found.GetComponent<Renderer>();
            var mats = renderer != null ? renderer.sharedMaterials : null;
            if (mats == null || use.SubMaterialIndex >= mats.Length || mats[use.SubMaterialIndex] != expected)
                return "INDEX-DRIFT";

            return "RELIABLE";
        }

        /// <summary>True when every segment of <paramref name="path"/> names exactly one child of its parent —
        /// i.e. no duplicate sibling names — so the path is a stable selector.</summary>
        private static bool PathIsUnambiguous(Transform root, string path)
        {
            var parent = root;
            foreach (var segment in path.Split('/'))
            {
                var matches = 0;
                Transform next = null;
                foreach (Transform child in parent)
                {
                    if (child.name == segment)
                    {
                        matches++;
                        next = child;
                    }
                }

                if (matches != 1)
                    return false;
                parent = next;
            }

            return true;
        }

        /// <summary>The '/'-joined child-name path from <paramref name="root"/> down to <paramref name="target"/>,
        /// or null if the target is not under the root.</summary>
        private static string PathFromRoot(Transform root, Transform target)
        {
            var segments = new List<string>();
            var cursor = target;
            while (cursor != null && cursor != root)
            {
                segments.Add(cursor.name);
                cursor = cursor.parent;
            }

            if (cursor != root)
                return null;

            segments.Reverse();
            return string.Join("/", segments);
        }

        private static bool LooksLikeSurface(string name)
        {
            if (string.IsNullOrEmpty(name))
                return false;
            foreach (var token in new[] { "floor", "ground", "wall", "rock", "cliff", "cave", "stone", "dirt", "terrain" })
            {
                if (name.IndexOf(token, StringComparison.OrdinalIgnoreCase) >= 0)
                    return true;
            }

            return false;
        }

        private static void SafeRelease(AssetReference reference)
        {
            try { reference.ReleaseAsset(); }
            catch (Exception) { /* not loaded / already released */ }
        }

        private sealed class MaterialUse
        {
            public Transform RepresentativeRenderer;
            public int SubMaterialIndex;
            public string Path;
            public int RendererCount;
        }
    }
}
