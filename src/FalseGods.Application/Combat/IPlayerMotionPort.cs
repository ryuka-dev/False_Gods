using System.Collections.Generic;
using FalseGods.Core.Simulation;

namespace FalseGods.Application.Combat
{
    /// <summary>One player worth throwing at: who they are, where they are, and — when this peer can tell — how
    /// fast they are moving.</summary>
    /// <remarks>
    /// <para><b>Why velocity is optional here.</b> Only the local player has a live movement controller to ask; the
    /// others are figures the session layer keeps up to date from elsewhere. Their speed has to be worked out from
    /// how their position changes, which is the caller's business over time, not something a single read can
    /// answer.</para>
    /// <para><see cref="Index"/> is the game's own player index, which is what identifies a player everywhere else
    /// in the encounter — so a caller can remember one across frames without inventing an identity for it.</para>
    /// </remarks>
    public readonly struct PlayerAim
    {
        public PlayerAim(int index, SimVector2 position, SimVector2 velocity, bool velocityKnown)
        {
            Index = index;
            Position = position;
            Velocity = velocity;
            VelocityKnown = velocityKnown;
        }

        public int Index { get; }

        public SimVector2 Position { get; }

        /// <summary>Meaningful only when <see cref="VelocityKnown"/>.</summary>
        public SimVector2 Velocity { get; }

        public bool VelocityKnown { get; }
    }

    /// <summary>Where a player is and how fast they are moving, on the arena's ground plane, at the moment it is
    /// read.</summary>
    /// <remarks>Just enough to lead a throw at a moving player, in the project's own 2D terms rather than a game
    /// vector — the height a crate lands at is the thrower's business, not the target's.</remarks>
    public readonly struct PlayerMotion
    {
        public PlayerMotion(SimVector2 position, SimVector2 velocity)
        {
            Position = position;
            Velocity = velocity;
            Known = true;
        }

        /// <summary>The player's position on the ground plane.</summary>
        public SimVector2 Position { get; }

        /// <summary>The player's velocity on the ground plane, in units per second.</summary>
        public SimVector2 Velocity { get; }

        /// <summary>False when there is no player to read — no level loaded — in which case the other fields are
        /// meaningless and a caller should not lead a throw.</summary>
        public bool Known { get; }
    }

    /// <summary>
    /// Reads the local player's motion so a throw can be aimed where the player will be, not where they are. Kept
    /// behind this boundary because reading a player's position and velocity is a game-specific concern; the
    /// simulation that leads the throw works only in <see cref="PlayerMotion"/>.
    /// </summary>
    public interface IPlayerMotionPort
    {
        /// <summary>The local player's current motion, or a <see cref="PlayerMotion"/> with
        /// <see cref="PlayerMotion.Known"/> false when there is no player to read.</summary>
        PlayerMotion TryReadLocalPlayer();

        /// <summary>
        /// Every player the boss should be throwing at, in the game's own roster order, appended to
        /// <paramref name="into"/> (cleared first). Players who are down are left out — nobody is aimed at while
        /// lying on the floor.
        /// </summary>
        /// <remarks>
        /// Reading the whole room rather than only the local player is what makes a barrage threaten everybody: a
        /// boss aiming through the local player singleton aims at whoever happens to be hosting, and everyone else
        /// walks through the crates untouched.
        /// </remarks>
        void ReadPlayersToThrowAt(IList<PlayerAim> into);
    }
}
