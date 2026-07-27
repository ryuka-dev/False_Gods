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

    /// <summary>
    /// The boss's authored content inside the arena prefab: the places it may stand, and how big it is. Like the
    /// décor and the navigation links, these are read from the realized hierarchy and carry no artifact rows — a
    /// hash-relevant fact is one two peers must agree on independently, and these are not: the boss's position
    /// and size reach a client through the host's replication, not through its own copy of the room.
    /// <para>Both are optional. Without <see cref="AnchorGroupPath"/> the boss simply has no authored places to
    /// stand; without <see cref="BodyPath"/> it keeps the presentation's own default size.</para>
    /// PoC-arena content constants, grouped here like <see cref="ArenaMarkerKinds"/>.
    /// </summary>
    public static class BossRoomContent
    {
        /// <summary>Every child is one place the boss may stand, in authored order; its position is the boss's
        /// feet. Which one it uses when is the boss's script, not the room's business.</summary>
        public const string AnchorGroupPath = "GameplayRoot/BossAnchors";

        /// <summary>A marker whose <b>scale</b> is the boss's authored size. Its position is not used — the boss
        /// stands at an anchor, not here.</summary>
        public const string BodyPath = "GameplayRoot/BossBody";

        /// <summary>Every child is a place the boss's minions can be summoned to. Put them where the room's shape
        /// is worth using — a terrace an enemy has to come down from is a different fight than the floor.</summary>
        public const string MinionSpawnGroupPath = "GameplayRoot/MinionSpawns";

        /// <summary>Every child is a place the room produces destructibles: where the boss's ammunition enters the
        /// world before anyone carries it anywhere. Put them somewhere a player can reach and interfere with — the
        /// supply line is only interesting if it can be cut.</summary>
        public const string CrateSourceGroupPath = "GameplayRoot/CrateSources";

        /// <summary>Every child is where carriers set destructibles down for the boss, <b>index-aligned with
        /// <see cref="AnchorGroupPath"/></b>: the boss standing at anchor <i>n</i> is supplied by pile <i>n</i>. A
        /// room that authors fewer piles than anchors reuses the last one, and a room that authors none has no
        /// supply line at all.</summary>
        public const string CratePileGroupPath = "GameplayRoot/CratePiles";
    }

    /// <summary>The hand-authored decoration rocks are excluded from the content hash (like the lighting), so they
    /// carry no artifact rows and their count/placement change freely without a rehash. At load they are painted by
    /// naming convention with the cave rock material, reusing the same donor carrier the surfaces borrow from —
    /// every <c>Rock_*</c> renderer anywhere under <see cref="ParentPath"/>, at any depth, so grouping rocks under
    /// empty holder objects in the prefab is free.
    /// PoC-arena content constants, grouped here like <see cref="ArenaMarkerKinds"/>.</summary>
    public static class RockDecoration
    {
        public const string ParentPath = "VisualRoot";
        public const string ChildNamePrefix = "Rock_";
        public const string MaterialName = "Rocks_Caves";
        public const int SubMaterialIndex = 0;
    }

    /// <summary>The hand-sculpted cave shell (VisualRoot/CaveShell) is hand-authored presentation excluded from the
    /// content hash; at load its sub-materials are painted with the vanilla cave materials, in sub-mesh order, from
    /// the same donor carrier the surfaces borrow from — so the sculpt gets the game's real MasterShader look
    /// (correct lighting/shading) over the mesh's own UVs + face assignment.
    /// <para>Each surface is identified by the PLACEHOLDER material the authored mesh wears, not by sub-mesh
    /// index — see <see cref="SubmeshBorrow"/> for why index binding is unsafe here. Listing a placeholder the
    /// mesh does not carry is harmless: only what is present gets painted.</para>
    /// PoC-arena content constants, grouped here like <see cref="ArenaMarkerKinds"/>.</summary>
    public static class WallShellDecoration
    {
        public const string Path = "VisualRoot/CaveShell";

        /// <summary>The sculpt's walkable faces, split off into their own object by the authoring pipeline so they
        /// can sit on the navigation layer while the rest of the shell stays invisible to the scan. Painted from
        /// the same rules — it wears the Floor placeholder — and optional: an arena whose sculpt has no Floor slot
        /// simply has no such child.</summary>
        public const string WalkablePath = "VisualRoot/CaveWalkable";

        // CaveCeilingOther, not CaveCeiling: the pinned donor carrier has no material by the latter name (measured).
        public static readonly IReadOnlyList<SubmeshMaterialRule> SurfaceRules = new[]
        {
            new SubmeshMaterialRule("FG_WallBot", "CaveWallBot"),
            new SubmeshMaterialRule("FG_WallMid", "CaveWallMid"),
            new SubmeshMaterialRule("FG_WallTop", "CaveWallTop"),
            new SubmeshMaterialRule("FG_Floor", "CaveFloor"),
            new SubmeshMaterialRule("FG_Ceiling", "CaveCeilingOther"),
        };
    }

    /// <summary>
    /// Vanilla scenery cloned into the arena at load. Like the rocks and the cave shell this is hand-authored
    /// presentation excluded from the content hash: the author places empty marker objects under
    /// <see cref="ParentPath"/> and each one receives a copy of the named prop, so scenery can be moved, turned,
    /// duplicated or removed without a rehash or a re-export.
    /// <para>The donor room is named by its <b>runtime</b> addressable key. Vanilla room prefabs answer to their
    /// asset path in the live catalog, and that path — not the reverse-engineered export's GUID, which is a
    /// different number entirely — is what loads. Measured in game before it was pinned here.</para>
    /// PoC-arena content constants, grouped here like <see cref="ArenaMarkerKinds"/>.</summary>
    public static class VanillaPropDecoration
    {
        public const string ParentPath = "VisualRoot/VanillaProps";

        /// <summary>The vanilla cave boss room — the donor for the cave scenery this arena reuses.</summary>
        public const string CaveBossRoomKey =
            "Assets/_Core/Prefabs/LevelGeneration/Chunks/Caves/CaveCousinNew.prefab";

        public const string MudPoolMarkerPrefix = "Prop_MudPool";

        public const string MudPoolPath = "Enemies/CousinSludgePool";

        /// <summary>
        /// <b>GeometryNoNavMesh, not the prop's own StaticDoodad.</b> The donor room carries the pool on a layer the
        /// recast scan rasterizes, so cloning it as-is would quietly turn a decorative basin into terrain and let
        /// it reshape — or split — the arena's navigation. On this layer it stays solid to physics and to the
        /// boss's thrown destructibles while the scan never sees it, which is exactly how the sculpted cave shell
        /// is handled.
        /// </summary>
        public const string LayerName = "GeometryNoNavMesh";

        /// <summary>Removed from the clone: the donor boss's teleport anchor. Our boss is anchored by the room's
        /// own authored points, and an unused game object has no business standing in our arena.</summary>
        public static readonly IReadOnlyList<string> MudPoolStripChildren = new[] { "CousinPosition" };

        /// <summary>
        /// Removed from the clone: the donor boss's pool controller, which is inert without that boss — it only
        /// caches the pool's damage volume and waits to be told to bubble or to switch the volume off.
        /// <para>The damage volume itself is <b>kept</b>. Standing in the sludge hurting the player is the pool's
        /// authored behaviour, and the authored version is better than one of ours: the room's own designers set
        /// the amount, the interval and the shape, and limited it to the player faction — so the boss's own
        /// minions wading through their master's pool are unharmed, which is exactly what was wanted.</para>
        /// </summary>
        public static readonly IReadOnlyList<string> MudPoolStripComponents = new[] { "CousinPool" };

        /// <summary>
        /// What a client strips on top of <see cref="MudPoolStripComponents"/>: the damage volume.
        /// <para>Hurting a player is a decision about the shared world, and this repository settles those on the
        /// host — that is how the boss's own hits reach a client today. A vanilla damage volume knows nothing of
        /// that: cloned onto every peer it would run everywhere at once, each peer damaging every player standing
        /// in its own copy, so one player wading in would be hurt once by their own machine and again by the
        /// host's. Building the volume only where the world is authoritative keeps one hazard, resolved once.</para>
        /// </summary>
        public const string MudPoolHazardComponent = "ApplyDamageInsideCollider";

        /// <summary>The pool's hazard volume keeps the layer the donor authored. Which layer a collider sits on
        /// decides which other layers it reports contact with at all, so sweeping this trigger onto the scenery
        /// layer with the rest of the prop would not just recategorise it — it would stop it firing, and the
        /// hazard would be silently dead while everything still looked right.</summary>
        public static readonly IReadOnlyList<string> MudPoolVolumeChildren = new[] { "PoolBlocker" };
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

    /// <summary>The realized arena's load-flow outputs: where it stands, the resolved spawn markers in world
    /// space, and the boss content the room authored (<see cref="BossRoomContent"/>).</summary>
    /// <param name="BossAnchors">The authored places the boss may stand, in authored order; empty when the room
    /// authored none.</param>
    /// <param name="BossSize">The authored boss size, or 0 when the room authored none — the presentation keeps
    /// its own default rather than shrinking the boss to nothing.</param>
    /// <param name="MinionSpawns">The authored places minions are summoned to, in authored order; empty when the
    /// room authored none, in which case a boss that summons simply has nowhere to put them.</param>
    /// <param name="CrateSources">The authored places destructibles are produced, in authored order; empty when the
    /// room authored none, in which case nothing is produced and the boss has no supply line.</param>
    /// <param name="CratePiles">The authored places carriers deliver to, index-aligned with
    /// <paramref name="BossAnchors"/>; empty when the room authored none.</param>
    public sealed record LoadedArena(
        ArenaWorldPoint Origin,
        ArenaWorldPoint PlayerSpawn,
        ArenaWorldPoint BossSpawn,
        int NavWalkableNodes,
        IReadOnlyList<ArenaWorldPoint> BossAnchors,
        float BossSize,
        IReadOnlyList<ArenaWorldPoint> MinionSpawns,
        IReadOnlyList<ArenaWorldPoint> CrateSources,
        IReadOnlyList<ArenaWorldPoint> CratePiles);

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
        private readonly IVanillaAssetProvider _vanillaAssets;
        private readonly Func<bool> _worldIsOurs;

        private ContentHash _contentHash;

        /// <param name="worldIsOurs">Whether this peer decides what happens in the shared world — true in single
        /// player and on the host, false on a client. Scenery that <i>acts</i> on players (the mud pool's hazard)
        /// is only built where the answer is yes, so a hazard is resolved once for everyone rather than once per
        /// peer. Null means yes, which is what single player means.</param>
        public ArenaLoadFlow(
            IArenaAssetProvider assets,
            IArenaRealization realization,
            INavigationPort navigation,
            IVanillaAssetProvider vanillaAssets,
            Func<bool>? worldIsOurs = null)
        {
            _assets = assets ?? throw new ArgumentNullException(nameof(assets));
            _realization = realization ?? throw new ArgumentNullException(nameof(realization));
            _navigation = navigation ?? throw new ArgumentNullException(nameof(navigation));
            _vanillaAssets = vanillaAssets ?? throw new ArgumentNullException(nameof(vanillaAssets));
            _worldIsOurs = worldIsOurs ?? (() => true);
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

            var realized = _realization.Realize(
                origin,
                parityPaths,
                new[] { playerPath, bossPath, BossRoomContent.BodyPath },
                new[]
                {
                    BossRoomContent.AnchorGroupPath,
                    BossRoomContent.MinionSpawnGroupPath,
                    BossRoomContent.CrateSourceGroupPath,
                    BossRoomContent.CratePileGroupPath,
                });
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

            var borrowRequests = BuildMaterialBorrowRequests(artifact, out var borrowError);
            if (borrowError != null)
            {
                return Fail(borrowError);
            }

            var borrow = _vanillaAssets.Resolve(borrowRequests);
            if (!borrow.Success)
            {
                return Fail($"arena material borrow failed: {borrow.Error ?? "unknown"}");
            }

            var rockPaint = PaintDecorationRocks(borrowRequests);
            if (!rockPaint.Success)
            {
                return Fail($"arena decoration paint failed: {rockPaint.Error ?? "unknown"}");
            }

            var wallPaint = PaintWallShell(borrowRequests);
            if (!wallPaint.Success)
            {
                return Fail($"arena wall-shell paint failed: {wallPaint.Error ?? "unknown"}");
            }

            var props = CloneVanillaProps();
            if (!props.Success)
            {
                return Fail($"arena vanilla scenery failed: {props.Error ?? "unknown"}");
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
            Arena = new LoadedArena(
                origin,
                player.WorldPosition,
                boss.WorldPosition,
                nav.WalkableNodesApplied,
                CollectGroup(realized.Markers, BossRoomContent.AnchorGroupPath),
                ReadBossSize(realized.Markers),
                CollectGroup(realized.Markers, BossRoomContent.MinionSpawnGroupPath),
                CollectGroup(realized.Markers, BossRoomContent.CrateSourceGroupPath),
                CollectGroup(realized.Markers, BossRoomContent.CratePileGroupPath));
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
            // Release the borrowed vanilla materials AFTER the realized hierarchy is destroyed, so no live renderer
            // still references a material whose carrier handle we are about to release; both happen before the
            // bundle unload, which strips our own meshes.
            _realization.Teardown();
            _vanillaAssets.Release();
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

        /// <summary>Pair each hashed material borrow (carrier + material name + sub-material index) with its
        /// non-hashed runtime target path (the placement), producing the requests the resolver acts on. A borrow
        /// with no matching placement is a fail-closed error rather than a silently-skipped paint.</summary>
        /// <summary>Paint the hand-authored decoration rocks (excluded from the artifact, so no per-rock rows) with
        /// the cave rock material, reusing the same donor carrier the surfaces borrow from. Skipped when the arena
        /// borrows nothing (no carrier to reuse); zero rocks placed is a success with zero applied.</summary>
        private MaterialBorrowResult PaintDecorationRocks(IReadOnlyList<MaterialBorrowRequest> borrowRequests)
        {
            if (borrowRequests.Count == 0)
            {
                return MaterialBorrowResult.Resolved(0);
            }

            var carrierGuid = borrowRequests[0].CarrierGuid;
            return _vanillaAssets.PaintByConvention(new MaterialConventionPaint(
                RockDecoration.ParentPath,
                RockDecoration.ChildNamePrefix,
                RockDecoration.SubMaterialIndex,
                carrierGuid,
                RockDecoration.MaterialName));
        }

        /// <summary>Place the vanilla scenery the room authored markers for. Runs before navigation so the clones
        /// are already standing — and already on their intended layer — when the level scans; an arena that
        /// authored no prop markers places nothing and succeeds.</summary>
        private VanillaPropResult CloneVanillaProps()
        {
            // A client keeps the scenery and loses the hazard: the host resolves what the pool does to whoever is
            // standing in it, exactly as it resolves the boss's own hits.
            var strip = new List<string>(VanillaPropDecoration.MudPoolStripComponents);
            if (!_worldIsOurs())
            {
                strip.Add(VanillaPropDecoration.MudPoolHazardComponent);
            }

            return _vanillaAssets.CloneProps(new VanillaPropClone(
                VanillaPropDecoration.ParentPath,
                VanillaPropDecoration.MudPoolMarkerPrefix,
                VanillaPropDecoration.CaveBossRoomKey,
                VanillaPropDecoration.MudPoolPath,
                VanillaPropDecoration.MudPoolStripChildren,
                strip,
                VanillaPropDecoration.LayerName,
                VanillaPropDecoration.MudPoolVolumeChildren));
        }

        /// <summary>Paint the sculpted cave shell's surfaces with the vanilla cave materials, reusing the same
        /// carrier the surfaces borrow from. The sculpt arrives in two objects — the solid shell and the walkable
        /// floor split off onto the navigation layer — so both are painted from the same rules. Skipped when the
        /// arena borrows nothing; an absent object is fail-open (the sculpt is optional décor).</summary>
        private MaterialBorrowResult PaintWallShell(IReadOnlyList<MaterialBorrowRequest> borrowRequests)
        {
            if (borrowRequests.Count == 0)
            {
                return MaterialBorrowResult.Resolved(0);
            }

            var carrierGuid = borrowRequests[0].CarrierGuid;
            var shell = _vanillaAssets.PaintSubmeshes(new SubmeshBorrow(
                WallShellDecoration.Path, carrierGuid, WallShellDecoration.SurfaceRules));
            if (!shell.Success)
            {
                return shell;
            }

            var walkable = _vanillaAssets.PaintSubmeshes(new SubmeshBorrow(
                WallShellDecoration.WalkablePath, carrierGuid, WallShellDecoration.SurfaceRules));
            if (!walkable.Success)
            {
                return walkable;
            }

            return MaterialBorrowResult.Resolved(shell.Applied + walkable.Applied);
        }

        private static IReadOnlyList<MaterialBorrowRequest> BuildMaterialBorrowRequests(
            ArenaContentArtifact artifact, out string? error)
        {
            var pathByBorrow = new Dictionary<StableMarkerId, string>();
            foreach (var placement in artifact.MaterialBorrowPlacements)
            {
                pathByBorrow[placement.BorrowMarkerId] = placement.TargetPath;
            }

            var requests = new List<MaterialBorrowRequest>(artifact.Definition.MaterialBorrows.Count);
            foreach (var borrow in artifact.Definition.MaterialBorrows)
            {
                if (!pathByBorrow.TryGetValue(borrow.MarkerId, out var targetPath))
                {
                    error = $"material borrow {borrow.MarkerId} has no target-path placement in the artifact";
                    return Array.Empty<MaterialBorrowRequest>();
                }

                requests.Add(new MaterialBorrowRequest(
                    targetPath, borrow.TargetSubMaterialIndex, borrow.CarrierGuid, borrow.MaterialName));
            }

            error = null;
            return requests;
        }

        /// <summary>The members of one authored marker group, in authored order — the realization reports them
        /// under paths prefixed by the group's own.</summary>
        private static IReadOnlyList<ArenaWorldPoint> CollectGroup(
            IReadOnlyList<RealizedMarker> markers, string groupPath)
        {
            var prefix = groupPath + "/";
            var points = new List<ArenaWorldPoint>();
            for (var i = 0; i < markers.Count; i++)
            {
                if (markers[i].Path.StartsWith(prefix, StringComparison.Ordinal))
                {
                    points.Add(markers[i].WorldPosition);
                }
            }

            return points;
        }

        /// <summary>The authored boss size: the uniform scale of the body marker. A non-uniform or non-positive
        /// authored scale is not a size, so it reads as "none authored" and the presentation keeps its default
        /// rather than rendering a boss squashed along one axis.</summary>
        private static float ReadBossSize(IReadOnlyList<RealizedMarker> markers)
        {
            var body = FindMarker(markers, BossRoomContent.BodyPath);
            if (body is null)
            {
                return 0f;
            }

            var scale = body.LocalScale;
            const float tolerance = 1e-3f;
            if (scale.X <= 0f
                || Math.Abs(scale.X - scale.Y) > tolerance
                || Math.Abs(scale.X - scale.Z) > tolerance)
            {
                return 0f;
            }

            return scale.X;
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
