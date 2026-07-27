using FalseGods.RuntimeContracts.Arena;

namespace FalseGods.Application.Combat
{
    /// <summary>
    /// Where one player is and where they are going — the two spots a volley threatens for that player.
    /// </summary>
    /// <remarks>
    /// <para>A volley carries one of these <b>per player</b>, and every crate of it picks one player and then one
    /// of that player's two spots. That is what makes a barrage everybody's problem: aiming through the local
    /// player — the game's own singleton — aims at whoever happens to be hosting, and everyone else walks through
    /// the crates untouched.</para>
    /// <para>Both spots travel rather than being recomputed by each peer, because a client cannot read another
    /// player's velocity to predict it: the aim is the host's to decide, like the seed.</para>
    /// </remarks>
    public readonly struct CrateVolleyAim
    {
        public CrateVolleyAim(ArenaWorldPoint current, ArenaWorldPoint lead)
        {
            Current = current;
            Lead = lead;
        }

        /// <summary>Where the player stands now.</summary>
        public ArenaWorldPoint Current { get; }

        /// <summary>Where they are predicted to be when the crates arrive. Equal to <see cref="Current"/> when
        /// this peer could not tell how fast they were moving — an unled aim rather than a wrong one.</summary>
        public ArenaWorldPoint Lead { get; }
    }
}
