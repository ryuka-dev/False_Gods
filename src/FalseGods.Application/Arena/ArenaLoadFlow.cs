using System;
using System.Collections.Generic;
using FalseGods.Protocol.Arena;
using FalseGods.Protocol.Wire;
using FalseGods.RuntimeContracts.Arena;

namespace FalseGods.Application.Arena
{
    /// <summary>The marker kinds the load flow resolves from the authored parity map. The authored
    /// <c>Enemy</c> spawn doubles as the boss spawn for the current slice — renaming it is an authoring change
    /// (new bundle, new hash), deliberately deferred.</summary>
    public static class ArenaMarkerKinds
    {
        public const string Player = "Player";
        public const string Boss = "Enemy";
    }

    /// <summary>Where the local arena load stands. Failure at any step returns the flow to
    /// <see cref="NotLoaded"/> with everything it had acquired released.</summary>
    public enum ArenaLoadStage
    {
        NotLoaded = 0,
        Prepared = 1,
        Realized = 2,
    }

    /// <summary>The outcome of <see cref="ArenaLoadFlow.Prepare"/>: the parsed artifact, or the fail-closed
    /// reason (which becomes the <c>ArenaLoadFailed</c> wire text).</summary>
    public sealed record ArenaPrepareResult(bool Success, string? FailureReason, ArenaContentArtifact? Artifact)
    {
        public static ArenaPrepareResult Failed(string reason) => new ArenaPrepareResult(false, reason, null);
    }

    /// <summary>The realized arena's load-flow outputs: where it stands and the resolved spawn markers, in
    /// world space.</summary>
    public sealed record LoadedArena(
        ArenaWorldPoint Origin,
        ArenaWorldPoint PlayerSpawn,
        ArenaWorldPoint BossSpawn,
        int NavWalkableNodes);

    /// <summary>The outcome of <see cref="ArenaLoadFlow.Realize"/>: the peer's own validated
    /// <see cref="ArenaManifest"/> (the <c>ArenaReady</c> payload) and the realized arena, or the fail-closed
    /// reason.</summary>
    public sealed record ArenaRealizeResult(bool Success, string? FailureReason, ArenaManifest? Manifest, LoadedArena? Arena)
    {
        public static ArenaRealizeResult Failed(string reason) => new ArenaRealizeResult(false, reason, null, null);
    }

    /// <summary>
    /// The local half of the canonical arena loading sequence, identical on every peer
    /// (Docs/MultiplayerLoadingContract.md §5.3 steps 2–4, Docs/ArenaLoadingProposal.md §2.4): load the shipped
    /// content, realize the authored prefab at the given origin, verify realized-vs-authored parity (R14), apply
    /// navigation, and produce the manifest the peer reports in <c>ArenaReady</c>.
    /// </summary>
    /// <remarks>
    /// <para><b>Fail closed, clean up in reverse.</b> Any failing step releases everything acquired so far and
    /// returns the flow to <see cref="ArenaLoadStage.NotLoaded"/>; the caller reports <c>ArenaLoadFailed</c>
    /// with the returned reason. <see cref="Teardown"/> runs the full reverse order — navigation out of the
    /// live graph first, then the realized hierarchy, then the bundle — and is idempotent at any stage
    /// (Architecture §9).</para>
    /// <para><b>Two stages, one sequence.</b> <see cref="Prepare"/> loads and validates the content;
    /// <see cref="Realize"/> places it. They are split because the <i>host</i> derives its arena origin from the
    /// authored player-spawn offset (<see cref="ArenaPlacement"/>) — which needs the parsed artifact — while a
    /// <i>client</i> gets its origin from the host's <c>EnterArena</c>. Both run the same two calls in the same
    /// order; there is no second code path (§5.3).</para>
    /// <para><b>The manifest's ProtocolVersion is the runtime's.</b> The artifact carries the protocol version
    /// it was exported against, but what peers must agree on is the wire contract they are <i>running</i>, so
    /// the reported manifest stamps <see cref="ProtocolVersion.Current"/>. The content hash is recomputed
    /// locally from the authored inputs — a shipped hash is never trusted (R34).</para>
    /// </remarks>
    public sealed class ArenaLoadFlow
    {
        // Realized-vs-authored tolerances, as measured in-game by PoC P8: tight enough to catch a real
        // divergence, loose enough for float round-tripping through the AssetBundle pipeline.
        private const float PositionEpsilon = 1e-3f;
        private const float RotationEpsilonDegrees = 0.05f;
        private const float ScaleEpsilon = 1e-3f;

        private readonly IArenaAssetProvider _assets;
        private readonly IArenaRealization _realization;
        private readonly INavigationPort _navigation;

        private ContentHash _contentHash;

        public ArenaLoadFlow(IArenaAssetProvider assets, IArenaRealization realization, INavigationPort navigation)
        {
            _assets = assets ?? throw new ArgumentNullException(nameof(assets));
            _realization = realization ?? throw new ArgumentNullException(nameof(realization));
            _navigation = navigation ?? throw new ArgumentNullException(nameof(navigation));
        }

        public ArenaLoadStage Stage { get; private set; }

        /// <summary>The parsed artifact after a successful <see cref="Prepare"/>, else null.</summary>
        public ArenaContentArtifact? Artifact { get; private set; }

        /// <summary>This peer's validated manifest after a successful <see cref="Realize"/>, else null.</summary>
        public ArenaManifest? Manifest { get; private set; }

        /// <summary>The realized arena after a successful <see cref="Realize"/>, else null.</summary>
        public LoadedArena? Arena { get; private set; }

        /// <summary>
        /// Load the shipped bundle + artifact, parse it, and recompute the canonical content hash. On failure
        /// everything is released and the flow stays <see cref="ArenaLoadStage.NotLoaded"/>.
        /// </summary>
        public ArenaPrepareResult Prepare()
        {
            if (Stage != ArenaLoadStage.NotLoaded)
            {
                throw new InvalidOperationException($"Prepare called at stage {Stage}; the flow loads once per encounter.");
            }

            var asset = _assets.Load();
            if (!asset.Success || asset.ArtifactText is null)
            {
                _assets.Release();
                return ArenaPrepareResult.Failed($"arena content unavailable: {asset.Error ?? "no artifact text"}");
            }

            ArenaContentArtifact artifact;
            ContentHash hash;
            try
            {
                artifact = ArenaContentArtifact.Parse(asset.ArtifactText);
                hash = artifact.ComputeContentHash();
            }
            catch (Exception exception)
            {
                _assets.Release();
                return ArenaPrepareResult.Failed($"arena artifact invalid: {exception.Message}");
            }

            Artifact = artifact;
            _contentHash = hash;
            Stage = ArenaLoadStage.Prepared;
            return new ArenaPrepareResult(true, null, artifact);
        }

        /// <summary>
        /// Realize the arena at <paramref name="origin"/>, verify parity, resolve the spawn markers, and apply
        /// navigation. On failure everything acquired so far is torn down and the flow returns to
        /// <see cref="ArenaLoadStage.NotLoaded"/>.
        /// </summary>
        public ArenaRealizeResult Realize(ArenaWorldPoint origin)
        {
            if (Stage != ArenaLoadStage.Prepared)
            {
                throw new InvalidOperationException($"Realize called at stage {Stage}; call Prepare first, once.");
            }

            var artifact = Artifact!;
            var playerPath = FindMarkerPath(artifact, ArenaMarkerKinds.Player);
            var bossPath = FindMarkerPath(artifact, ArenaMarkerKinds.Boss);
            if (playerPath is null || bossPath is null)
            {
                return Fail($"authored parity map has no '{(playerPath is null ? ArenaMarkerKinds.Player : ArenaMarkerKinds.Boss)}' marker");
            }

            var parityPaths = new List<string>(artifact.Parity.Count);
            foreach (var node in artifact.Parity)
            {
                parityPaths.Add(node.Path);
            }

            var realized = _realization.Realize(origin, parityPaths, new[] { playerPath, bossPath });
            if (!realized.Success)
            {
                return Fail($"arena realization failed: {realized.Error ?? "unknown"}");
            }

            var parityError = CompareParity(artifact.Parity, realized.ParityNodes);
            if (parityError != null)
            {
                return Fail($"realized arena diverges from authored content: {parityError}");
            }

            var player = FindMarker(realized.Markers, playerPath);
            var boss = FindMarker(realized.Markers, bossPath);
            if (player is null || boss is null)
            {
                return Fail($"realized arena is missing marker '{(player is null ? playerPath : bossPath)}'");
            }

            var nav = _navigation.Apply();
            if (!nav.Success)
            {
                return Fail($"arena navigation failed: {nav.Error ?? "unknown"}");
            }

            Manifest = new ArenaManifest(
                artifact.Definition.ArenaId,
                artifact.Definition.ArenaVersion,
                artifact.SchemaVersion,
                _contentHash,
                ProtocolVersion.Current.Value,
                artifact.BundleVersion);
            Arena = new LoadedArena(origin, player.WorldPosition, boss.WorldPosition, nav.WalkableNodesApplied);
            Stage = ArenaLoadStage.Realized;
            return new ArenaRealizeResult(true, null, Manifest, Arena);
        }

        /// <summary>
        /// Full local teardown, in reverse acquisition order: navigation restored, hierarchy destroyed, bundle
        /// released. Idempotent, and safe at any stage — the ports' Remove/Teardown/Release are no-ops for
        /// what was never acquired.
        /// </summary>
        public void Teardown()
        {
            _navigation.Remove();
            _realization.Teardown();
            _assets.Release();
            Artifact = null;
            Manifest = null;
            Arena = null;
            _contentHash = default;
            Stage = ArenaLoadStage.NotLoaded;
        }

        /// <summary>A mid-realize failure tears down everything acquired so far (realization may hold a partial
        /// hierarchy even after reporting failure-adjacent states; its Teardown is idempotent) and resets.</summary>
        private ArenaRealizeResult Fail(string reason)
        {
            Teardown();
            return ArenaRealizeResult.Failed(reason);
        }

        private static string? FindMarkerPath(ArenaContentArtifact artifact, string kind)
        {
            foreach (var node in artifact.Parity)
            {
                if (string.Equals(node.Kind, kind, StringComparison.Ordinal))
                {
                    return node.Path;
                }
            }

            return null;
        }

        private static RealizedMarker? FindMarker(IReadOnlyList<RealizedMarker> markers, string path)
        {
            foreach (var marker in markers)
            {
                if (string.Equals(marker.Path, path, StringComparison.Ordinal))
                {
                    return marker;
                }
            }

            return null;
        }

        /// <summary>R14: every authored parity node must exist at runtime with the authored local transform,
        /// within the measured tolerances. Returns the first mismatch, or null when all match.</summary>
        private static string? CompareParity(
            IReadOnlyList<ArenaParityNode> authored,
            IReadOnlyList<RealizedParityNode> realized)
        {
            var byPath = new Dictionary<string, RealizedParityNode>(StringComparer.Ordinal);
            foreach (var node in realized)
            {
                byPath[node.Path] = node;
            }

            foreach (var node in authored)
            {
                if (!byPath.TryGetValue(node.Path, out var actual))
                {
                    return $"'{node.Path}' missing at runtime";
                }

                var positionGap = Distance(node.LocalTransform.Position, actual.LocalPosition);
                var rotationGap = AngleDegrees(node.LocalTransform.Rotation, actual.LocalRotation);
                var scaleGap = Distance(node.LocalTransform.Scale, actual.LocalScale);
                if (positionGap > PositionEpsilon || rotationGap > RotationEpsilonDegrees || scaleGap > ScaleEpsilon)
                {
                    return $"'{node.Path}' off by pos {positionGap:0.####} rot {rotationGap:0.####}deg scale {scaleGap:0.####}";
                }
            }

            return null;
        }

        private static float Distance(Protocol.Arena.Vector3 authored, ArenaWorldPoint actual)
        {
            var dx = authored.X - actual.X;
            var dy = authored.Y - actual.Y;
            var dz = authored.Z - actual.Z;
            return (float)Math.Sqrt((dx * dx) + (dy * dy) + (dz * dz));
        }

        /// <summary>The angle between two unit rotations, sign-robust (q and -q are the same rotation) — the
        /// non-Unity equivalent of <c>Quaternion.Angle</c>.</summary>
        private static float AngleDegrees(Protocol.Arena.Quaternion authored, ArenaRotation actual)
        {
            var dot = (authored.X * actual.X) + (authored.Y * actual.Y) + (authored.Z * actual.Z) + (authored.W * actual.W);
            var clamped = Math.Min(1.0, Math.Abs((double)dot));
            return (float)(2.0 * Math.Acos(clamped) * (180.0 / Math.PI));
        }
    }
}
