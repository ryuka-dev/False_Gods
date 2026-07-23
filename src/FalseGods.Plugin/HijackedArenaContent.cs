using System;
using System.IO;
using FalseGods.Application.Arena;
using FalseGods.Integration.Sulfur.Arena;
using FalseGods.Integration.Sulfur.Navigation;
using FalseGods.RuntimeContracts.Arena;
using FalseGods.UnityRuntime.Arena;
using ILogger = FalseGods.RuntimeContracts.Diagnostics.ILogger;

namespace FalseGods.Plugin
{
    /// <summary>
    /// The Composition Root's half of a hijacked (Strategy A) arena load: it runs the same canonical arena load
    /// sequence the additive path runs, and hands the result to the adapter that dresses it as a level room.
    /// </summary>
    /// <remarks>
    /// <para>This exists because the two halves live in assemblies that cannot see each other. Loading the arena
    /// is <c>FalseGods.UnityRuntime</c>'s AssetBundle work driven by <c>FalseGods.Application</c>'s load flow;
    /// dressing it as a <c>Room</c> is game-type work that only <c>FalseGods.Integration.Sulfur</c> may do. Only
    /// the Composition Root references both, so it supplies the load and release as callbacks.</para>
    /// <para><b>The one difference from the additive path is navigation.</b> The arena arrives as the level, so
    /// the level's own navigation scan covers it and the flow uses <see cref="NativeLevelNavigationPort"/> instead
    /// of the additive A* port — which would fail closed here, since a level being generated has no scanned
    /// navigation to apply anything to. Everything else — content hash, realized-vs-authored parity, borrowed
    /// vanilla materials — is the same sequence, so the arena is validated exactly as strictly.</para>
    /// <para><b>The origin is the level's own.</b> The additive path places the arena around the standing player;
    /// here the arena defines where the player ends up, so it is realized at the origin and the authored
    /// player-spawn marker becomes the level's spawn point.</para>
    /// </remarks>
    internal sealed class HijackedArenaContent
    {
        private static readonly ArenaWorldPoint LevelOrigin = new ArenaWorldPoint(0f, 0f, 0f);

        private readonly string _contentDirectory;
        private readonly ILogger _logger;

        private BundleArenaRealization? _realization;
        private ArenaLoadFlow? _flow;
        private ArenaRealizeResult? _realized;

        public HijackedArenaContent(string contentDirectory, ILogger logger)
        {
            _contentDirectory = contentDirectory ?? throw new ArgumentNullException(nameof(contentDirectory));
            _logger = logger;
        }

        /// <summary>
        /// Whether a hijacked arena is standing in the world right now. Asked of the realized hierarchy itself, so
        /// a level the player left — which destroyed the arena along with the rest of the level — reads as gone
        /// without us having to observe the level change.
        /// </summary>
        public bool IsLive => _realization != null && _realization.CurrentRoot != null;

        /// <summary>The live arena's validated load result — the manifest and spawn points an encounter needs —
        /// or null when no hijacked arena is standing.</summary>
        public ArenaRealizeResult? Realized => IsLive ? _realized : null;

        /// <summary>The live arena's realization, for presentation that needs to reach its hierarchy.</summary>
        public BundleArenaRealization? Realization => IsLive ? _realization : null;

        /// <summary>The origin a hijacked arena is realized at — the level's own.</summary>
        public static ArenaWorldPoint Origin => LevelOrigin;

        /// <summary>The source the generation hooks pull the arena room from.</summary>
        public HijackedArenaRoomSource CreateRoomSource() =>
            new HijackedArenaRoomSource(Load, () => _realization?.CurrentRoot!, Release, _logger);

        /// <summary>Load, realize, and validate the arena at the level origin.</summary>
        private HijackedArenaLoad Load()
        {
            Release();

            var realization = new BundleArenaRealization(
                Path.Combine(_contentDirectory, LocalEncounterController.BundleFileName),
                Path.Combine(_contentDirectory, LocalEncounterController.ArtifactFileName),
                LocalEncounterController.ArenaPrefabName,
                _logger);
            _realization = realization;
            _flow = new ArenaLoadFlow(
                realization,
                realization,
                new NativeLevelNavigationPort(_logger),
                new SulfurVanillaAssetProvider(() => realization.CurrentRoot, _logger));

            var prepared = _flow.Prepare();
            if (!prepared.Success)
            {
                return HijackedArenaLoad.Failed($"arena content did not prepare: {prepared.FailureReason}");
            }

            var realized = _flow.Realize(LevelOrigin);
            if (!realized.Success || realized.Arena is null)
            {
                return HijackedArenaLoad.Failed($"arena load failed: {realized.FailureReason}");
            }

            // Kept so an encounter raised in this level can fight in the arena already standing in it, rather
            // than loading a second copy of the same content.
            _realized = realized;
            return HijackedArenaLoad.Loaded(realized.Arena.PlayerSpawn);
        }

        /// <summary>
        /// Release the bundle and the borrowed vanilla materials. The arena hierarchy itself belongs to the level
        /// that placed it — the flow's teardown finds it already destroyed, which its null checks handle.
        /// </summary>
        private void Release()
        {
            _flow?.Teardown();
            _flow = null;
            _realization = null;
            _realized = null;
        }
    }
}
