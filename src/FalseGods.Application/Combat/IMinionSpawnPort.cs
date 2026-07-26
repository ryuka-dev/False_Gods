using System.Collections.Generic;
using FalseGods.RuntimeContracts.Arena;

namespace FalseGods.Application.Combat
{
    /// <summary>
    /// Puts the boss's minions into the world at the places the room authored for them.
    /// </summary>
    /// <remarks>
    /// <para>The minions are the game's <b>own</b> units, not something of ours: they path on the level's
    /// navigation, take the level's jump links between terraces, take weapon fire, and drop the loot they always
    /// drop. That is the whole reason to spawn real ones — the room's shape only means something to an enemy that
    /// uses the game's own movement.</para>
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

        /// <summary>Ask for one minion at each of <paramref name="at"/>. An empty list summons nothing.</summary>
        void Summon(IReadOnlyList<ArenaWorldPoint> at);

        /// <summary>
        /// Remove every minion this port spawned. The encounter's minions belong to the encounter: a fight that
        /// ends must not leave its summons wandering the level.
        /// </summary>
        void DespawnAll();
    }
}
