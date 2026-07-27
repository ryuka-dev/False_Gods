using System;
using FalseGods.RuntimeContracts.Multiplayer;
using SULFURTogether.Api;
using UnityEngine;
using ILogger = FalseGods.RuntimeContracts.Diagnostics.ILogger;

namespace FalseGods.Integration.SulfurTogether
{
    /// <summary>
    /// The ST implementation of <see cref="IDestructibleOwnership"/>: claims one of our destructibles on SULFUR
    /// Together's public API so its own in-scene destructible sync leaves it alone.
    /// </summary>
    /// <remarks>
    /// <para>ST mirrors destructible breaks by matching the position an object was spawned at, which works for the
    /// level's own breakables and not for ours: a boss's ammunition is made at runtime and heaped in one place, so
    /// the key matched the wrong crate and a break on one peer destroyed an unrelated one on the other — measured,
    /// as crates bursting in mid-air and loot appearing with no shot fired.</para>
    /// <para>An ST build without the API is not an error: the claim fails, the crates are also handled by ST's
    /// channel as they were before, and the encounter still runs. Reported once rather than per crate.</para>
    /// </remarks>
    internal sealed class StDestructibleOwnership : IDestructibleOwnership
    {
        private readonly ILogger? _logger;
        private bool _unavailable;

        public StDestructibleOwnership(ILogger? logger = null)
        {
            _logger = logger;
        }

        public void ClaimAsOurs(object destructible)
        {
            if (_unavailable)
            {
                return;
            }

            var go = destructible as GameObject ?? (destructible as Component)?.gameObject;
            if (go == null)
            {
                return;
            }

            try
            {
                NetExternalDestructibles.Exclude(go);
            }
            catch (Exception exception)
            {
                _unavailable = true;
                _logger?.LogWarning($"[st-destructibles] the session layer would not take the claim "
                    + $"({exception.Message}); it will keep mirroring our crates, which can break the wrong one.");
            }
        }
    }
}
