// Unity / game-type interop, like the other game-facing implementations in this assembly.
#nullable disable

using System;
using PerfectRandom.Sulfur.Core;
using PerfectRandom.Sulfur.Core.Effects;
using UnityEngine;
using ILogger = FalseGods.RuntimeContracts.Diagnostics.ILogger;

namespace FalseGods.Integration.Sulfur.Arena
{
    /// <summary>
    /// Pushes the level's fog out far enough to see across a boss arena, through the game's own fog seam.
    /// </summary>
    /// <remarks>
    /// <para>SULFUR's fog is Unity linear fog driven by <c>EffectsManager.RequestFogValues</c>, and a level's
    /// values come from its <c>WorldEnvironment</c> by way of <c>GameManager.SetLevelFog</c>. That happens in the
    /// loading-setup step, which runs <i>before</i> the start-area step our arena is placed in, so applying fog
    /// alongside the arena lands after the level has set its own and is not overwritten afterwards: no later
    /// generation step touches fog, and the only thing that otherwise would — a <c>FogChangeTrigger</c> volume —
    /// lives in authored vanilla rooms, none of which a hijacked level contains.</para>
    /// <para><b>The colour is the level's, only the distance is ours.</b> A cave's fog colour is its mood; what
    /// makes a 60-unit arena unreadable is a cutoff tuned for corridor-sized rooms, not the colour. So the
    /// environment's own <c>fogColor</c> is passed straight back through and only the near/far distances change.</para>
    /// </remarks>
    public static class SulfurLevelFog
    {
        /// <summary>
        /// Set the current level's fog distances, keeping its own fog colour. Returns false (and changes nothing)
        /// when the game's managers are not up, so a fog tweak can never be the reason a level fails to load.
        /// </summary>
        public static bool TryApply(float startDistance, float endDistance, ILogger logger = null)
        {
            if (endDistance <= 0f || endDistance <= startDistance)
            {
                logger?.LogWarning(
                    $"[fog] ignoring fog range {startDistance:0.#}..{endDistance:0.#} — the end must be beyond the start.");
                return false;
            }

            try
            {
                var effects = StaticInstance<EffectsManager>.Instance;
                var gameManager = StaticInstance<GameManager>.Instance;
                if (effects == null || gameManager == null || gameManager.currentEnvironment == null)
                {
                    logger?.LogWarning("[fog] no effects manager or level environment yet; fog left as it is.");
                    return false;
                }

                var wasStart = RenderSettings.fogStartDistance;
                var wasEnd = RenderSettings.fogEndDistance;

                effects.RequestFogValues(gameManager.currentEnvironment.fogColor, startDistance, endDistance);

                logger?.Log($"[fog] level fog range {wasStart:0.#}..{wasEnd:0.#} -> {startDistance:0.#}..{endDistance:0.#} "
                    + "(the level's own fog colour is kept).");
                return true;
            }
            catch (Exception exception)
            {
                logger?.LogWarning($"[fog] could not set the level fog: {exception}");
                return false;
            }
        }

        /// <summary>Hand the level's fog back to whatever its own environment asks for. Used when nothing of ours
        /// should be dictating the look any more.</summary>
        public static bool TryRestoreLevelDefault(ILogger logger = null)
        {
            try
            {
                var gameManager = StaticInstance<GameManager>.Instance;
                if (gameManager == null)
                {
                    return false;
                }

                gameManager.SetLevelFog();
                logger?.Log("[fog] level fog restored to the environment's own values.");
                return true;
            }
            catch (Exception exception)
            {
                logger?.LogWarning($"[fog] could not restore the level fog: {exception}");
                return false;
            }
        }
    }
}
