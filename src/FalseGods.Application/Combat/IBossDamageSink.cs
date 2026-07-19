namespace FalseGods.Application.Combat
{
    /// <summary>
    /// The inbound weapon-damage seam: the game-side adapter delivers real weapon hits on the boss's physical
    /// body here, and the composition applies them to the authoritative <c>BossSimulation</c>
    /// (Docs/Architecture.md §6; single-player and host only — a client never applies damage, invariant 4).
    /// </summary>
    /// <remarks>
    /// This is the <b>receiving</b> half of the damage story and deliberately not the documented
    /// <c>IDamagePort</c>, which is the outbound half (the boss dealing damage to game units via
    /// <c>Unit.ReceiveDamage</c>) and does not exist yet — it arrives when a boss attack actually deals damage.
    /// The amount is the game's final computed per-hit damage (weapon stats, ammo, falloff already applied);
    /// whether the hit is amplified is decided by the simulation's own weak-point rules, not by the caller.
    /// </remarks>
    public interface IBossDamageSink
    {
        /// <summary>Deliver one weapon hit's final damage amount. Non-positive or non-finite amounts are ignored.</summary>
        void ApplyWeaponDamage(float amount);
    }
}
