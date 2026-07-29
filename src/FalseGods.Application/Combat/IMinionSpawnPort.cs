using System.Collections.Generic;
using FalseGods.Core.Bosses.Combat;
using FalseGods.RuntimeContracts.Arena;

namespace FalseGods.Application.Combat
{
    /// <summary>
    /// Puts the boss's bands into the world at the places the room authored for them, and owns the roster that
    /// says what a band is made of.
    /// </summary>
    /// <remarks>
    /// <para>The minions are the game's <b>own</b> units, not something of ours: they path on the level's
    /// navigation, take the level's jump links between terraces, take weapon fire, and drop the loot they always
    /// drop. That is the whole reason to spawn real ones — the room's shape only means something to an enemy that
    /// uses the game's own movement.</para>
    /// <para><b>The roster lives behind this port, and deliberately so.</b> The encounter names a band; which
    /// creatures that band consists of is the game's vocabulary, which only the adapter may speak. So the boss's
    /// design ("the third wave is the casters") and the game's content ("a caster is a GoblinWizardFrost") stay on
    /// opposite sides of one seam, and neither has to be rewritten to change the other.</para>
    /// <para><b>Host only.</b> The host owns enemies (SULFUR Together invariant 1), and the session layer
    /// replicates them the way it replicates every other enemy, so a client must never call this: it would double
    /// every minion and each peer would be fighting a different set.</para>
    /// <para>Spawning is asynchronous in the game (the unit's asset is loaded on demand), so a call is a request,
    /// not a placement — <see cref="Summon"/> returns once the requests are made, not once the minions exist.</para>
    /// </remarks>
    public interface IMinionSpawnPort
    {
        /// <summary>How many minions this port currently has alive in the world.</summary>
        int Alive { get; }

        /// <summary>
        /// How many arrive when <paramref name="band"/> is summoned, or 0 for a band this roster does not know.
        /// </summary>
        /// <remarks>
        /// Asked so the encounter can say what it is about to do before doing it. The number is the roster's, never
        /// the caller's — a caller that decided the headcount would be back to summoning "four of something".
        /// </remarks>
        int SizeOf(MinionBandId band);

        /// <summary>
        /// Put <paramref name="band"/> on the floor, distributed over the room's authored places in the order
        /// given. An empty place list summons nothing, and so does a band the roster does not know.
        /// </summary>
        /// <remarks>
        /// The caller supplies <i>every</i> place it is willing to use rather than one per member, because it does
        /// not know how many members there are; a band larger than the room's authored places reuses them from the
        /// start, the way the vanilla cave boss picks a spawner per henchman and lets them collide.
        /// </remarks>
        void Summon(MinionBandId band, IReadOnlyList<ArenaWorldPoint> places);

        /// <summary>
        /// Remove every minion this port spawned. The encounter's minions belong to the encounter: a fight that
        /// ends must not leave its summons wandering the level.
        /// </summary>
        void DespawnAll();
    }
}
