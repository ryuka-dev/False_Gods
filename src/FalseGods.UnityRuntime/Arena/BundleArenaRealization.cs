// Unity-interop file (AssetBundle/Transform APIs carry no nullable annotations) — opted out of the
// nullable-reference context like the other UnityRuntime implementations.
#nullable disable

using System;
using System.Collections.Generic;
using System.IO;
using FalseGods.RuntimeContracts.Arena;
using UnityEngine;
using ILogger = FalseGods.RuntimeContracts.Diagnostics.ILogger;

namespace FalseGods.UnityRuntime.Arena
{
    /// <summary>
    /// The UnityRuntime implementation of both halves of the arena content seam: the AssetBundle + artifact
    /// lifecycle (<see cref="IArenaAssetProvider"/>) and the authored-prefab realization
    /// (<see cref="IArenaRealization"/>) — one class, because the realization instantiates the very prefab the
    /// load acquired (ADR-001; Docs/ArenaLoadingProposal.md §2.3).
    /// </summary>
    /// <remarks>
    /// The artifact crosses the seam as raw text — parsing and hashing are <c>FalseGods.Application</c>'s job;
    /// this module may not reference <c>FalseGods.Protocol</c>. The bundle is loaded synchronously
    /// (<see cref="AssetBundle.LoadFromFile(string)"/>): the PoC bundle is small and the load happens once at
    /// encounter entry, on the main thread. The realized root is exposed via <see cref="CurrentRoot"/> so the
    /// Composition Root can hand it (as a plain Unity-level source) to the navigation adapter, which cannot
    /// reference this assembly.
    /// </remarks>
    public sealed class BundleArenaRealization : IArenaAssetProvider, IArenaRealization
    {
        private readonly string _bundlePath;
        private readonly string _artifactPath;
        private readonly string _prefabName;
        private readonly ILogger _logger;

        private AssetBundle _bundle;
        private GameObject _prefab;
        private GameObject _root;

        public BundleArenaRealization(string bundlePath, string artifactPath, string prefabName, ILogger logger = null)
        {
            _bundlePath = bundlePath ?? throw new ArgumentNullException(nameof(bundlePath));
            _artifactPath = artifactPath ?? throw new ArgumentNullException(nameof(artifactPath));
            _prefabName = prefabName ?? throw new ArgumentNullException(nameof(prefabName));
            _logger = logger;
        }

        /// <summary>The realized arena's root, or null when none is realized. For the Composition Root to wire
        /// into the navigation adapter; nothing else should reach in.</summary>
        public GameObject CurrentRoot => _root;

        public ArenaAssetLoadResult Load()
        {
            if (_bundle != null)
            {
                throw new InvalidOperationException("Arena assets are already loaded; the flow loads once per encounter.");
            }

            if (!File.Exists(_bundlePath))
            {
                return ArenaAssetLoadResult.Failed($"arena bundle not found at {_bundlePath}");
            }

            if (!File.Exists(_artifactPath))
            {
                return ArenaAssetLoadResult.Failed($"arena content artifact not found at {_artifactPath}");
            }

            _bundle = AssetBundle.LoadFromFile(_bundlePath);
            if (_bundle == null)
            {
                return ArenaAssetLoadResult.Failed($"arena bundle failed to load from {_bundlePath}");
            }

            _prefab = _bundle.LoadAsset<GameObject>(_prefabName);
            if (_prefab == null)
            {
                return ArenaAssetLoadResult.Failed($"prefab '{_prefabName}' not found in the arena bundle");
            }

            string artifactText;
            try
            {
                artifactText = File.ReadAllText(_artifactPath);
            }
            catch (Exception exception)
            {
                return ArenaAssetLoadResult.Failed($"arena artifact unreadable: {exception.Message}");
            }

            _logger?.Log($"[arena-assets] bundle + artifact loaded ({_prefabName})");
            return ArenaAssetLoadResult.Loaded(artifactText);
        }

        public void Release()
        {
            if (_root != null)
            {
                // Defensive ordering: the flow tears the realization down before releasing, but a bundle unload
                // with a live instance would strip its meshes — never leave that possible.
                Teardown();
            }

            if (_bundle != null)
            {
                _bundle.Unload(unloadAllLoadedObjects: true);
                _bundle = null;
                _prefab = null;
                _logger?.Log("[arena-assets] bundle released");
            }
        }

        public ArenaRealizationResult Realize(
            ArenaWorldPoint origin, IReadOnlyList<string> parityPaths, IReadOnlyList<string> markerPaths)
        {
            if (_prefab == null)
            {
                return ArenaRealizationResult.Failed("arena assets are not loaded");
            }

            if (_root != null)
            {
                return ArenaRealizationResult.Failed("the arena is already realized");
            }

            _root = UnityEngine.Object.Instantiate(
                _prefab, new Vector3(origin.X, origin.Y, origin.Z), Quaternion.identity);
            _root.name = "FalseGodsArena";

            var parityNodes = new List<RealizedParityNode>(parityPaths.Count);
            foreach (var path in parityPaths)
            {
                var node = _root.transform.Find(path);
                if (node == null)
                {
                    continue; // absence is reported by omission; the flow fails closed on it
                }

                parityNodes.Add(new RealizedParityNode(
                    path,
                    ToPoint(node.localPosition),
                    new ArenaRotation(node.localRotation.x, node.localRotation.y, node.localRotation.z, node.localRotation.w),
                    ToPoint(node.localScale)));
            }

            var markers = new List<RealizedMarker>(markerPaths.Count);
            foreach (var path in markerPaths)
            {
                var marker = _root.transform.Find(path);
                if (marker != null)
                {
                    markers.Add(new RealizedMarker(path, ToPoint(marker.position)));
                }
            }

            _logger?.Log($"[arena] realized at ({origin.X:0.0}, {origin.Y:0.0}, {origin.Z:0.0}); "
                + $"{parityNodes.Count}/{parityPaths.Count} parity nodes, {markers.Count}/{markerPaths.Count} markers");
            return new ArenaRealizationResult(true, null, parityNodes, markers);
        }

        public void Teardown()
        {
            if (_root != null)
            {
                UnityEngine.Object.Destroy(_root);
                _root = null;
                _logger?.Log("[arena] realized hierarchy destroyed");
            }
        }

        private static ArenaWorldPoint ToPoint(Vector3 v) => new ArenaWorldPoint(v.x, v.y, v.z);
    }
}
