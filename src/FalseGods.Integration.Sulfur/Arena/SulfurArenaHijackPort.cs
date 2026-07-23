using System;
using FalseGods.Application.Arena;
using PerfectRandom.Sulfur.Core;
using ILogger = FalseGods.RuntimeContracts.Diagnostics.ILogger;

namespace FalseGods.Integration.Sulfur.Arena
{
    /// <summary>
    /// The SULFUR implementation of <see cref="IArenaHijackPort"/>: it asks the game to load the first cave
    /// environment through <c>GameManager.GoToLevel</c> — the same entry point the game's own developer level-select
    /// uses. That routine destroys and rebuilds the A* recast graph, runs the level-generation node graph, scans
    /// navigation, and spawns the player, so going through it gives us native navigation instead of the additive
    /// overlay's manual rescan (Docs: Strategy A / arena loading proposal §2.1).
    /// </summary>
    /// <remarks>
    /// This step only triggers the load of the real cave level; substituting our arena for the generated content is
    /// a separate generation hook added on top. <c>WorldEnvironmentIds</c> is a global-namespace enum in the game
    /// assembly; <see cref="WorldEnvironmentIds.Act_01_Caves"/> is the first cave. The call is wrapped defensively —
    /// the base-game load path is external — and a missing <c>GameManager</c> is a logged no-op rather than a throw.
    /// </remarks>
    public sealed class SulfurArenaHijackPort : IArenaHijackPort
    {
        private const WorldEnvironmentIds CaveEnvironment = WorldEnvironmentIds.Act_01_Caves;
        private const int FirstLevelIndex = 0;

        private readonly ILogger? _logger;

        public SulfurArenaHijackPort(ILogger? logger)
        {
            _logger = logger;
        }

        public void LoadHijackedArena()
        {
            var gameManager = StaticInstance<GameManager>.Instance;
            if (gameManager == null)
            {
                _logger?.LogWarning("[hijack] no GameManager; cannot load the cave level.");
                return;
            }

            // Arm the generation hooks for exactly this load. They release themselves when the generation run
            // ends (LevelGenerationHijackPatches); the catch below covers the load never starting at all.
            LevelGenerationHijack.Arm();
            try
            {
                _logger?.Log($"[hijack] loading {CaveEnvironment} level {FirstLevelIndex} (native level load).");
                gameManager.GoToLevel(CaveEnvironment, FirstLevelIndex, LoadingMode.Normal, string.Empty);
            }
            catch (Exception exception)
            {
                LevelGenerationHijack.Disarm();
                _logger?.LogWarning($"[hijack] GoToLevel threw: {exception}");
            }
        }
    }
}
