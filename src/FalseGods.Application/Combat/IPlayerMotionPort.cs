using FalseGods.Core.Simulation;

namespace FalseGods.Application.Combat
{
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
    }
}
