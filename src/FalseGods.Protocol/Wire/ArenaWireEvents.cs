using FalseGods.Core.Arena;

namespace FalseGods.Protocol.Wire
{
    /// <summary>A mechanism group switched on — the reliable counterpart of Core's <c>MechanismGroupActivated</c>.</summary>
    public sealed record ArenaMechanismGroupActivatedEvent(
        Sequence Sequence,
        SimulationTick Tick,
        MechanismGroupId Group) : IArenaWireEvent;

    /// <summary>The arena exit unlocked — the boss is defeated and players may leave.</summary>
    public sealed record ArenaExitUnlockedEvent(Sequence Sequence, SimulationTick Tick) : IArenaWireEvent;
}
