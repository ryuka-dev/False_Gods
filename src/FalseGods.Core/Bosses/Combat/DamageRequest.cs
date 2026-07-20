using FalseGods.Core.Simulation;

namespace FalseGods.Core.Bosses.Combat
{
    /// <summary>
    /// The boss's authoritative decision to deal <see cref="Amount"/> damage to <see cref="Target"/> (a player),
    /// resolved when an attack commits. This is the outbound half of combat
    /// (Docs/Architecture.md §6: <c>BossSimulation</c> emits DamageRequested → <c>Application</c> →
    /// <c>Integration.Sulfur</c> executes <c>Unit.ReceiveDamage</c>).
    /// </summary>
    /// <remarks>
    /// It is drained separately from the boss's presentation/wire events (<see cref="BossSimulation.DrainDamageRequests"/>,
    /// not <see cref="BossSimulation.DrainEvents"/>): a request to damage a player is a <b>command to another
    /// system</b>, not a boss-state fact to render or replicate, so it never enters the presentation or wire mapper.
    /// Only single-player and the host produce these; a client never computes damage
    /// (Docs/MultiplayerLoadingContract.md §5.6, SULFUR Together invariants 1/2). <see cref="Attack"/> ties the
    /// request to the attack that caused it, so the same landing applies exactly once.
    /// </remarks>
    public sealed record DamageRequest(ParticipantId Target, int Amount, AttackInstanceId Attack);
}
