using FalseGods.RuntimeContracts.Arena;

namespace FalseGods.Application.Combat
{
    /// <summary>
    /// What a crate landing does to the players around its impact point: a splash circle centred on where it
    /// lands, hurting and knocking back anyone standing inside it. Kept behind this boundary because dealing player
    /// damage and driving the player's movement are game-specific; the crate port only reports where a crate
    /// landed.
    /// </summary>
    /// <remarks>
    /// The crate is broken by the caller whether or not it caught anyone, and a landing never pays out loot — so
    /// the reward for shooting a crate out of the air still comes only from shooting it, never from its landing.
    /// </remarks>
    public interface IThrownCrateImpact
    {
        /// <summary>
        /// Splash at <paramref name="at"/>: hurt and knock back any player within the impact radius, and return
        /// true if at least one was caught. The radius is the implementation's own, a fixed size.
        /// </summary>
        bool Splash(ArenaWorldPoint at);
    }
}
