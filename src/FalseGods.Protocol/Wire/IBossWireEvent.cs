namespace FalseGods.Protocol.Wire
{
    /// <summary>
    /// A discrete boss transition on the reliable, sequenced boss event stream
    /// (Docs/OriginalBossNetworkingArchitecture.md §9.6, ADR-005).
    /// </summary>
    /// <remarks>
    /// Reliable and ordered within the boss stream, which has its own <see cref="Sequence"/> space independent of
    /// the arena stream. Idempotence is by (<c>EncounterId</c>, boss stream, <see cref="Sequence"/>); attack
    /// effects are additionally idempotent by <c>AttackInstanceId</c> (§9.9). These are the wire counterpart of
    /// Core's <c>IBossDomainEvent</c> — a distinct vocabulary carrying sequence/tick — and never a boss snapshot
    /// or any arena type (FG-ARCH-008).
    /// </remarks>
    public interface IBossWireEvent
    {
        /// <summary>The position of this event in the boss stream's sequence space.</summary>
        Sequence Sequence { get; }

        /// <summary>The host simulation tick at which the transition occurred.</summary>
        SimulationTick Tick { get; }
    }
}
