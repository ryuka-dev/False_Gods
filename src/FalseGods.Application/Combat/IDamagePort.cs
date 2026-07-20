using FalseGods.Core.Simulation;

namespace FalseGods.Application.Combat
{
    /// <summary>
    /// The outbound damage seam: apply the boss's resolved <see cref="Core.Bosses.Combat.DamageRequest"/> to a real
    /// player (Docs/Architecture.md §6: <c>BossSimulation</c> emits DamageRequested → <c>Application</c> →
    /// <c>Integration.Sulfur</c> executes <c>Unit.ReceiveDamage</c>). This is the counterpart of the inbound
    /// <see cref="IBossDamageSink"/>.
    /// </summary>
    /// <remarks>
    /// Single-player and the host resolve boss damage; a client never does (Docs/MultiplayerLoadingContract.md §5.6,
    /// SULFUR Together invariants 1/2). The implementation lives in <c>Integration.Sulfur</c> (which references
    /// <c>Application</c>): it maps the <see cref="ParticipantId"/> to the game <c>Player</c> and calls the game's own
    /// damage entry point, which applies the game's damage model (armour, resistances, safe-zone). The amount is
    /// the boss's decision; how much health is actually lost is the game's.
    /// </remarks>
    public interface IDamagePort
    {
        /// <summary>Deal <paramref name="amount"/> points of boss damage to the player identified by
        /// <paramref name="target"/>. An unknown target is ignored.</summary>
        void ApplyDamage(ParticipantId target, int amount);
    }
}
