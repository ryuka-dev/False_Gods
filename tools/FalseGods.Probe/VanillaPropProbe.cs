using System;
using System.Collections;
using System.Collections.Generic;
using System.Text;
using PerfectRandom.Sulfur.Core.LevelGeneration;
using UnityEngine;
using UnityEngine.AddressableAssets;
using UnityEngine.ResourceManagement.AsyncOperations;
using UnityEngine.ResourceManagement.ResourceLocations;

namespace FalseGods.Probe
{
    /// <summary>
    /// Vanilla PROP survey (arena decoration, boss #1) — the discovery step before cloning a piece of vanilla
    /// room scenery into our arena.
    ///
    /// Cloning a prop needs three things the reverse-engineered export cannot give us:
    ///   1. The donor room's <b>runtime</b> addressable key. The export's meta GUIDs are NOT the game's catalog
    ///      keys (measured: the export gives <c>CaveNormal3New</c> a different GUID from the one that actually
    ///      loads), so the key has to be read from the live catalog or from the level's own room lists.
    ///   2. The prop's exact child NAME and path inside that room — the clone selector.
    ///   3. Its layer, transform and component inventory — because a vanilla prop carries its own colliders,
    ///      triggers and gameplay components, and a prop left on a layer the navigation scan rasterizes will
    ///      silently reshape the arena's navmesh.
    ///
    /// Read-only. It loads room prefabs without instantiating them (so no component lifecycle runs), reads the
    /// hierarchy, and releases every Addressables handle. It touches no nav graph and no game state.
    ///
    /// Two independent ways to find the donor room, because either can come up empty:
    ///   A. <b>Catalog search</b> — scan the live catalog's locations for one whose asset path carries the room
    ///      name. This answers "is it addressable at all, and under which key" directly.
    ///   B. <b>Level room lists</b> — the GUIDs on every loaded <c>LevelBlock.roomPrefabsAddressable</c>, loaded
    ///      one by one to read each prefab's name. This is the path P1a used to pin the material carrier, and it
    ///      doubles as a GUID-to-name table for picking future donors.
    /// </summary>
    internal sealed class VanillaPropProbe
    {
        /// <summary>How deep below a matched prop to print the hierarchy. The sludge pool's own children (the
        /// teleport anchor, the damage trigger, the audio source, the sludge surfaces) sit one level down; its
        /// decoration bricks another. Two levels shows the parts that matter without ~130 brick lines.</summary>
        private const int PropDumpDepth = 2;

        /// <summary>Cap on rooms loaded for the name table, so a survey in a big environment stays a survey.</summary>
        private const int MaxRoomsToName = 96;

        private readonly string _roomName;
        private readonly string _propNameFragment;

        public VanillaPropProbe(string roomName, string propNameFragment)
        {
            _roomName = roomName;
            _propNameFragment = propNameFragment;
        }

        public IEnumerator Run(ProbeReport report)
        {
            report.Section($"Vanilla prop survey — room '{_roomName}', prop '*{_propNameFragment}*'");

            var catalogKeys = new List<string>();
            report.Try("search the live catalog for the donor room", () => SearchCatalog(report, catalogKeys));

            var roomGuids = CollectRoomGuids(report);

            // Prefer a key the catalog itself gave us; fall back to the level's room lists.
            var candidates = new List<string>(catalogKeys);
            foreach (var guid in roomGuids)
            {
                if (!candidates.Contains(guid))
                    candidates.Add(guid);
            }

            if (candidates.Count == 0)
            {
                report.Line("  Nothing to survey: the catalog search found no location and no LevelBlock room");
                report.Line("  lists are loaded. Stand in a CAVE level and press the key again.");
                yield break;
            }

            report.Section("Room keys, and which prefab each one loads");
            var surveyed = 0;
            var matched = false;
            foreach (var key in candidates)
            {
                if (surveyed >= MaxRoomsToName)
                {
                    report.Line($"  ... stopped after {MaxRoomsToName} rooms.");
                    break;
                }

                surveyed++;

                var reference = new AssetReference(key);
                AsyncOperationHandle<GameObject> load = default;
                var started = false;
                report.Try($"LoadAssetAsync({key})", () =>
                {
                    load = reference.LoadAssetAsync<GameObject>();
                    started = true;
                });

                if (!started)
                    continue;

                yield return load;

                if (load.Status != AsyncOperationStatus.Succeeded || load.Result == null)
                {
                    report.Value(key, $"FAILED (status={load.Status})");
                    SafeRelease(reference);
                    continue;
                }

                var prefab = load.Result;
                var isTarget = string.Equals(prefab.name, _roomName, StringComparison.OrdinalIgnoreCase);
                report.Value(key, prefab.name + (isTarget ? "   <<< the donor room" : string.Empty));

                if (isTarget)
                {
                    matched = true;
                    report.Try("survey the donor room's props", () => SurveyRoom(report, key, prefab));
                }

                SafeRelease(reference);
            }

            report.Line();
            if (matched)
            {
                report.Line($"  >>> The key printed as '<<< the donor room' is the RUNTIME key for {_roomName}.");
                report.Line("      Together with a prop's PATH below it, that is the clone selector: load the room");
                report.Line("      by that key, Find(path), Instantiate, strip, re-layer, hold the handle.");
            }
            else
            {
                report.Line($"  >>> No loaded room is named '{_roomName}'. Either it is not reachable from this");
                report.Line("      level's room lists (try standing in the level that uses it), or it is not");
                report.Line("      addressable at all — in which case cloning from it is off the table and the");
                report.Line("      prop has to be authored instead.");
            }
        }

        /// <summary>Locations in the live catalog whose asset path carries the room name. A hit gives the key
        /// directly, without loading anything.</summary>
        private void SearchCatalog(ProbeReport report, List<string> keys)
        {
            report.Section("A. Live catalog search");

            var seen = new HashSet<string>(StringComparer.Ordinal);
            var locators = 0;
            var enumerable = 0;
            var scanned = 0;

            foreach (var locator in Addressables.ResourceLocators)
            {
                locators++;
                IEnumerable<IResourceLocation> locations;
                try
                {
                    locations = locator.AllLocations;
                }
                catch (Exception)
                {
                    continue; // some locators do not enumerate; skip them
                }

                if (locations == null)
                    continue;

                enumerable++;
                foreach (var location in locations)
                {
                    if (location?.InternalId == null || location.ResourceType != typeof(GameObject))
                        continue;

                    scanned++;
                    if (location.InternalId.IndexOf(_roomName, StringComparison.OrdinalIgnoreCase) < 0)
                        continue;

                    report.Value("location", location.InternalId);
                    report.Value("  primary key", location.PrimaryKey);
                    if (location.PrimaryKey != null && seen.Add(location.PrimaryKey))
                        keys.Add(location.PrimaryKey);
                }
            }

            report.Value("resource locators", locators);
            report.Value("of which enumerable", enumerable);
            report.Value("GameObject locations scanned", scanned);
            report.Value("keys matching the room name", keys.Count);
            if (keys.Count == 0)
            {
                report.Line("  (No catalog hit. That does not mean the room is unreachable — room prefabs are");
                report.Line("   referenced by GUID from the level's own data and need not be enumerable. B follows.)");
            }
        }

        /// <summary>Distinct non-empty room-prefab GUIDs across every loaded <see cref="LevelBlock"/> — the same
        /// source P1a used to pin the material carrier. Order is first-seen, so the survey is deterministic.</summary>
        private static List<string> CollectRoomGuids(ProbeReport report)
        {
            report.Section("B. Room GUIDs on the loaded LevelBlocks");

            var guids = new List<string>();
            var seen = new HashSet<string>(StringComparer.Ordinal);

            report.Try("enumerate LevelBlock room GUIDs", () =>
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

            report.Value("distinct room GUIDs", guids.Count);
            return guids;
        }

        /// <summary>Print every object in the room whose name carries the prop fragment: its path from the room
        /// root (the clone selector), its local transform, its layer, its components, and its subtree down to
        /// <see cref="PropDumpDepth"/>.</summary>
        private void SurveyRoom(ProbeReport report, string key, GameObject prefab)
        {
            report.Section($"C. '{_propNameFragment}' props inside {prefab.name}");
            report.Value("room key", key);

            var matches = new List<Transform>();
            foreach (var transform in prefab.GetComponentsInChildren<Transform>(includeInactive: true))
            {
                if (transform.name.IndexOf(_propNameFragment, StringComparison.OrdinalIgnoreCase) >= 0)
                    matches.Add(transform);
            }

            report.Value("matching objects", matches.Count);
            if (matches.Count == 0)
            {
                report.Line("  (No object carries that name fragment. Widen PropNameFragment and run again — the");
                report.Line("   room's own child names are authoritative, the export's are only a guide.)");
                return;
            }

            foreach (var match in matches)
            {
                report.Line();
                report.Value("path", PathFrom(prefab.transform, match));
                report.Value("  active", match.gameObject.activeSelf);
                report.Value("  layer", $"{match.gameObject.layer} \"{LayerMask.LayerToName(match.gameObject.layer)}\"");
                report.Value("  localPosition", Format(match.localPosition));
                report.Value("  localRotation(euler)", Format(match.localEulerAngles));
                report.Value("  localScale", Format(match.localScale));
                report.Value("  components", ComponentNames(match.gameObject));
                DumpSubtree(report, prefab.transform, match, 1);
                ReportLayerSpread(report, match);
            }
        }

        private static void DumpSubtree(ProbeReport report, Transform root, Transform node, int depth)
        {
            if (depth > PropDumpDepth)
                return;

            for (var i = 0; i < node.childCount; i++)
            {
                var child = node.GetChild(i);
                var indent = new string(' ', depth * 2);
                report.Line($"  {indent}{child.name}  [layer {child.gameObject.layer} " +
                            $"\"{LayerMask.LayerToName(child.gameObject.layer)}\"]  " +
                            $"pos {Format(child.localPosition)}  scale {Format(child.localScale)}");
                report.Line($"  {indent}  components: {ComponentNames(child.gameObject)}");
                DumpSubtree(report, root, child, depth + 1);
            }
        }

        /// <summary>Which layers the whole prop subtree occupies, and how many objects sit on each. This is the
        /// nav-relevant number: a prop left on a layer the recast graph rasterizes joins the navmesh.</summary>
        private static void ReportLayerSpread(ProbeReport report, Transform prop)
        {
            var counts = new Dictionary<int, int>();
            var renderers = 0;
            var colliders = 0;

            foreach (var transform in prop.GetComponentsInChildren<Transform>(includeInactive: true))
            {
                var layer = transform.gameObject.layer;
                counts[layer] = counts.TryGetValue(layer, out var count) ? count + 1 : 1;
                if (transform.GetComponent<Renderer>() != null)
                    renderers++;
                if (transform.GetComponent<Collider>() != null)
                    colliders++;
            }

            var text = new StringBuilder();
            foreach (var pair in counts)
            {
                if (text.Length > 0)
                    text.Append("; ");
                text.Append($"{pair.Key} \"{LayerMask.LayerToName(pair.Key)}\" x{pair.Value}");
            }

            report.Value("  whole subtree: layers", text.ToString());
            report.Value("  whole subtree: renderers", renderers);
            report.Value("  whole subtree: colliders", colliders);
        }

        private static string ComponentNames(GameObject target)
        {
            var names = new List<string>();
            foreach (var component in target.GetComponents<Component>())
            {
                // A missing script reads as a null component; say so rather than hiding it.
                names.Add(component == null ? "<missing script>" : component.GetType().Name);
            }

            return names.Count == 0 ? "<none>" : string.Join(", ", names);
        }

        private static string PathFrom(Transform root, Transform node)
        {
            var parts = new List<string>();
            for (var current = node; current != null && current != root; current = current.parent)
            {
                parts.Insert(0, current.name);
            }

            return string.Join("/", parts);
        }

        private static string Format(Vector3 value) =>
            $"({value.x:0.###}, {value.y:0.###}, {value.z:0.###})";

        private static void SafeRelease(AssetReference reference)
        {
            if (reference == null)
                return;
            try { reference.ReleaseAsset(); }
            catch (Exception) { /* not loaded / already released */ }
        }
    }
}
