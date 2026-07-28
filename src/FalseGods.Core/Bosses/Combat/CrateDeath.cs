namespace FalseGods.Core.Bosses.Combat
{
    /// <summary>
    /// How a thrown destructible stopped existing — and, because the two are the same question, whether it paid.
    /// </summary>
    /// <remarks>
    /// <para>Only the deaths a <i>player</i> caused are worth telling other peers about. A crate reaching its
    /// landing spot, bursting against a wall, or being lifted into a volley follows the same arc from the same
    /// seed on every machine, so it has already happened identically everywhere; saying so would repeat what both
    /// ends already know. Where a player is standing and what they are shooting at is the one thing no peer can
    /// work out from the commands it was sent.</para>
    /// <para>The two that do travel are told apart because they pay differently, which is the point of the whole
    /// mechanic: shooting a crate out of the air is rewarded, and letting one arrive is not.</para>
    /// </remarks>
    public enum CrateDeath
    {
        /// <summary>A player broke it — off a pile, or out of the air. Goes through the game's own break, so its
        /// loot obeys whatever rules the session has for sharing loot.</summary>
        Shot = 0,

        /// <summary>It reached a player in flight and burst on them. Breaks without paying, as a landing does.</summary>
        Struck = 1,
    }
}
