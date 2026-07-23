using FalseGods.RuntimeContracts.Arena;

namespace FalseGods.Application.Combat
{
    /// <summary>
    /// What a crate does to a player it reaches — two ways it can catch one: a direct hit on a body in flight, and
    /// a splash where it lands. Kept behind this boundary because dealing player damage and driving the player's
    /// movement are game-specific; the crate port only reports where the crate is.
    /// </summary>
    /// <remarks>
    /// The two together leave no easy air-dodge: a crate that reaches a player in the air detonates on them, and one
    /// that comes down near a player splashes them, so only moving clear on the ground avoids it. The crate is
    /// broken by the caller whether or not it caught anyone, and neither a hit nor a landing pays out loot — so the
    /// reward for shooting a crate out of the air still comes only from shooting it.
    /// </remarks>
    public interface IThrownCrateImpact
    {
        /// <summary>
        /// A direct hit at <paramref name="at"/> while the crate is in flight: if it has reached a player's body — a
        /// sphere in three dimensions, so it catches a player who jumped into its path or hangs in the air — hurt
        /// and knock them back, and return true so the caller detonates the crate there.
        /// </summary>
        bool Contact(ArenaWorldPoint at);

        /// <summary>
        /// Splash at <paramref name="at"/> where the crate lands: hurt and knock back any player within the impact
        /// radius on the ground, and return true if at least one was caught. The radii are the implementation's own,
        /// fixed sizes.
        /// </summary>
        bool Splash(ArenaWorldPoint at);
    }
}
