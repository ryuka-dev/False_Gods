using FalseGods.Core.Bosses;
using FalseGods.Core.Simulation;

namespace FalseGods.Protocol.Wire
{
    /// <summary>
    /// Continuous boss state for unreliable correction — position, health, phase, active attack, weak point
    /// (Docs/OriginalBossNetworkingArchitecture.md §9.6, Docs/MultiplayerLoadingContract.md §5.7).
    /// </summary>
    /// <remarks>
    /// Carried on the unreliable channel: loss is tolerated and a snapshot never <em>drives</em> a discrete
    /// transition — that is what the reliable <see cref="IBossWireEvent"/> stream is for. It carries <b>decision
    /// results, not RNG state</b>: the client does not re-simulate, so it needs the selected attack/target, not
    /// the host's generator (ADR-005).
    ///
    /// <para>
    /// Boss and arena are separate wire types (FG-ARCH-008): this type carries <b>no</b> arena mechanism, gate,
    /// or hazard field. Its shape is the subset the PoC test boss needs — a single weak-point flag rather than the
    /// general schema's <c>WeakPointStates[]</c> — and it grows only as a boss needs it (Docs/RiskList.md R31).
    /// </para>
    /// </remarks>
    public sealed record BossSnapshot(
        EncounterId Encounter,
        BossInstanceId Boss,
        DefinitionId Definition,
        ProtocolVersion ProtocolVersion,
        SimulationTick Tick,
        int PhaseId,
        int StateId,
        SimulationTick StateStartTick,
        AttackInstanceId? ActiveAttack,
        int? ActiveAttackDefinitionId,
        ParticipantId? Target,
        SimVector2 Position,
        SimVector2 Facing,
        int Health,
        int MaxHealth,
        bool WeakPointExposed,
        Sequence LastProcessedBossEventSequence);
}
