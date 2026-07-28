using System;
using FalseGods.Application.Combat;
using FalseGods.RuntimeContracts.Arena;
using PerfectRandom.Sulfur.Core;
using PerfectRandom.Sulfur.Core.Effects;
using PerfectRandom.Sulfur.Core.Units;
using UnityEngine;
using ILogger = FalseGods.RuntimeContracts.Diagnostics.ILogger;

namespace FalseGods.Integration.Sulfur.Combat
{
    /// <summary>
    /// The SULFUR implementation of <see cref="IBattlefieldCleanupPort"/>: the game's own record of what has died,
    /// and the game's own ways of taking a body and a spray of gore away.
    /// </summary>
    /// <remarks>
    /// <para><b>Where the bodies are.</b> <c>UnitManager.KilledUnits</c> is a public list the game appends every
    /// dead unit to (everything but breakables) from <c>Unit.Die()</c>. It is the only record of the kind there is
    /// — and, measured on v0.18.5, <b>nothing in the game ever reads it</b>: the only other thing that touches it
    /// is the wholesale clear between levels. So it is a register kept for someone, and this is that someone.</para>
    /// <para><b>How they are taken.</b> <c>Npc.DestroySelf()</c>, which is exactly what the game's own endless
    /// mode does to its tracked units when it changes arena — it walks its list and destroys each, alive or dead.
    /// The loose gore is a separate system with a separate call, <c>GibSystemNEW.DeactivateAllGibs()</c>, which is
    /// what endless mode uses between waves. Two systems, two calls; clearing one leaves the other's mess behind,
    /// which is what "the bodies are gone but the floor is still red" would look like.</para>
    /// <para><b>What is deliberately never swept.</b> A player — alive, down, or dead — is a body the fight is
    /// still about, and removing one would take a rescuable team-mate out of the world. Anything still alive is
    /// not a corpse. Anything outside the caller's radius is not in this room.</para>
    /// </remarks>
    public sealed class SulfurBattlefieldCleanup : IBattlefieldCleanupPort
    {
        private readonly ILogger? _logger;

        public SulfurBattlefieldCleanup(ILogger? logger = null)
        {
            _logger = logger;
        }

        public int SweepCorpses(ArenaWorldPoint around, float radius)
        {
            var swept = SweepBodies(new Vector3(around.X, around.Y, around.Z), radius);
            SweepGore();
            return swept;
        }

        /// <summary>
        /// Take the bodies, and drop them from the game's register as they go.
        /// </summary>
        /// <remarks>
        /// <b>Pruning the register is part of the job, not tidiness.</b> Nothing else in the game removes from it,
        /// so entries only ever accumulate — left alone, every later sweep would walk a list that keeps growing
        /// with the destroyed objects it already dealt with. Walked backwards so removing an entry cannot skip the
        /// next one.
        /// </remarks>
        private int SweepBodies(Vector3 around, float radius)
        {
            var manager = StaticInstance<UnitManager>.Instance;
            var killed = manager != null ? manager.KilledUnits : null;
            if (killed == null)
            {
                return 0;
            }

            var reach = radius * radius;
            var swept = 0;
            for (var i = killed.Count - 1; i >= 0; i--)
            {
                var unit = killed[i];
                if (unit == null)
                {
                    killed.RemoveAt(i); // already gone: the level took it, or an earlier sweep did
                    continue;
                }

                if (unit.isPlayer || unit.IsAlive)
                {
                    continue;
                }

                if ((unit.transform.position - around).sqrMagnitude > reach)
                {
                    continue; // lying somewhere this fight has no business reaching into
                }

                try
                {
                    if (unit is Npc npc)
                    {
                        npc.DestroySelf();
                    }
                    else
                    {
                        UnityEngine.Object.Destroy(unit.gameObject);
                    }

                    killed.RemoveAt(i);
                    swept++;
                }
                catch (Exception exception)
                {
                    // A body that will not go is a worse-looking floor, not a broken fight.
                    _logger?.LogWarning($"[cleanup] a body could not be cleared ({exception.Message}); leaving it.");
                }
            }

            return swept;
        }

        /// <summary>Send the loose gore back to its pool. Global by nature — the system keeps one set of live
        /// pieces for the whole level and offers no way to ask about a part of it.</summary>
        private void SweepGore()
        {
            try
            {
                StaticInstance<GibSystemNEW>.Instance?.DeactivateAllGibs();
            }
            catch (Exception exception)
            {
                _logger?.LogWarning($"[cleanup] the gore could not be cleared ({exception.Message}).");
            }
        }
    }
}
