namespace FalseGods.Protocol.Wire
{
    /// <summary>
    /// A discrete arena transition on the reliable, sequenced arena event stream
    /// (Docs/OriginalBossNetworkingArchitecture.md §9.6, ADR-005).
    /// </summary>
    /// <remarks>
    /// A <b>separate</b> sequence space from the boss stream, so a dropped arena event never stalls boss events
    /// behind it (§9.9). The wire counterpart of Core's <c>IArenaDomainEvent</c>; it carries no boss state
    /// (FG-ARCH-008).
    /// </remarks>
    public interface IArenaWireEvent
    {
        /// <summary>The position of this event in the arena stream's sequence space.</summary>
        Sequence Sequence { get; }

        /// <summary>The host simulation tick at which the transition occurred.</summary>
        SimulationTick Tick { get; }
    }
}
