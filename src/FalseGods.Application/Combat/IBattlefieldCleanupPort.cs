using FalseGods.RuntimeContracts.Arena;

namespace FalseGods.Application.Combat
{
    /// <summary>
    /// Clears the leavings of a fight — the bodies and the gore — from the room it is being fought in.
    /// </summary>
    /// <remarks>
    /// <para><b>Why a boss fight needs this and an ordinary room does not.</b> The game's own answer to bodies
    /// piling up is a budget: a room with a trigger volume in it keeps the last handful of corpses and destroys the
    /// oldest as new ones arrive. That is sized for a room you pass through. A boss fight is one room for a long
    /// time with waves summoned into it on purpose, so the floor fills with everything the players have killed and
    /// the thing they are supposed to be reading — where the boss is, where the next wave came from, what is still
    /// standing — is buried in it.</para>
    /// <para><b>Cleared at a moment, not continuously.</b> A body that vanishes while someone is looking at it
    /// reads as a bug; a floor that is clear again after the boss has gone under and come back up reads as the
    /// boss having done it. So this is asked for at the seam the fight already has, and never on a timer.</para>
    /// <para><b>Every peer clears its own floor.</b> This is the one piece of the fight that is deliberately not
    /// host-only, because it is not a decision: the bodies are already dead everywhere, and each peer is only
    /// tidying its own copies of them. Asking the host to do it for everybody would mean replicating the removal
    /// of things nobody is fighting any more, and a session layer that mirrors spawns need not mirror sweeping.
    /// </para>
    /// </remarks>
    public interface IBattlefieldCleanupPort
    {
        /// <summary>
        /// Remove every corpse lying within <paramref name="radius"/> of <paramref name="around"/>, and the loose
        /// gore with them. Returns how many bodies were taken, for the log.
        /// </summary>
        /// <remarks>
        /// The bound exists so a sweep can never reach out of the room the fight is in — not to trace its outline.
        /// Living units are never touched, and neither is a player, alive or down.
        /// </remarks>
        int SweepCorpses(ArenaWorldPoint around, float radius);
    }

    /// <summary>Shared numbers for clearing a fight's leavings, so a host and a client sweep the same ground.
    /// </summary>
    public static class BattlefieldSweep
    {
        /// <summary>
        /// How far from the arena's origin a body has to be lying before a sweep leaves it alone.
        /// </summary>
        /// <remarks>
        /// <b>A fence, not an outline.</b> The room is about eighty metres across and the origin sits inside it,
        /// so this reaches past every corner — which is the point. It is not trying to trace where the arena ends;
        /// it is there so a sweep can never reach into an ordinary level, which is possible whenever the boss is
        /// raised somewhere other than its own arena.
        /// </remarks>
        public const float ArenaReach = 120f;
    }
}
