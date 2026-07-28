using FalseGods.Core.Bosses;
using FalseGods.Core.Simulation;

namespace FalseGods.Protocol.Wire
{
    /// <summary>The boss became active — the reliable counterpart of Core's <c>BossSpawned</c>.</summary>
    public sealed record BossAppearedEvent(Sequence Sequence, SimulationTick Tick, int PhaseId) : IBossWireEvent;

    /// <summary>
    /// The host selected an attack and began telegraphing it. <see cref="Attack"/> ties the whole
    /// telegraph→commit chain together and is the anchor for attack-effect idempotence (§9.7/§9.9).
    /// </summary>
    public sealed record BossAttackTelegraphedEvent(
        Sequence Sequence,
        SimulationTick Tick,
        AttackInstanceId Attack,
        int AttackDefinitionId,
        SimVector2 AimPoint,
        float TelegraphSeconds) : IBossWireEvent;

    /// <summary>The attack landed. The authoritative "it happened" fact; clients render it and decide no damage.</summary>
    public sealed record BossAttackCommittedEvent(
        Sequence Sequence,
        SimulationTick Tick,
        AttackInstanceId Attack,
        int AttackDefinitionId,
        SimVector2 AimPoint) : IBossWireEvent;

    /// <summary>The host advanced the boss to a new phase.</summary>
    public sealed record BossPhaseChangedEvent(Sequence Sequence, SimulationTick Tick, int PhaseId) : IBossWireEvent;

    /// <summary>The boss's weak point opened or closed.</summary>
    public sealed record BossWeakPointChangedEvent(Sequence Sequence, SimulationTick Tick, bool Exposed) : IBossWireEvent;

    /// <summary>
    /// The host applied damage. <see cref="RemainingHealth"/> matches the authoritative health the snapshot also
    /// corrects; this discrete event is the "a hit happened" cue for the client (flash / number).
    /// </summary>
    public sealed record BossDamagedEvent(
        Sequence Sequence,
        SimulationTick Tick,
        int Amount,
        int RemainingHealth,
        bool WeakPointHit) : IBossWireEvent;

    /// <summary>
    /// The boss moved to the next station of its itinerary and is now standing at <see cref="AnchorIndex"/>.
    /// The position is where it <b>is</b>; the snapshot stream carries the same place continuously, but this is
    /// the discrete decision a client can play a cue on.
    /// </summary>
    public sealed record BossRelocatedEvent(
        Sequence Sequence,
        SimulationTick Tick,
        int StationIndex,
        int AnchorIndex,
        SimVector2 Position,
        float Height) : IBossWireEvent;

    /// <summary>The boss died. Terminal on the boss stream.</summary>
    /// <summary>
    /// The boss started, or stopped, being enraged — the reliable transition behind the continuous
    /// <c>BossSnapshot.Enraged</c>. The snapshot is what a peer that missed this corrects itself from; the event is
    /// what a peer plays the change with, so a client roars at the moment the host's boss did rather than the
    /// first frame a snapshot happened to arrive.
    /// </summary>
    public sealed record BossEnragedEvent(Sequence Sequence, SimulationTick Tick, bool Enraged) : IBossWireEvent;

    public sealed record BossDefeatedEvent(Sequence Sequence, SimulationTick Tick) : IBossWireEvent;
}
