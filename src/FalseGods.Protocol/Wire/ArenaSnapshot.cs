using System.Collections.Generic;
using FalseGods.Core.Arena;
using FalseGods.Core.Simulation;

namespace FalseGods.Protocol.Wire
{
    /// <summary>
    /// Continuous arena state for unreliable correction — which mechanism groups are active, and whether the exit
    /// is unlocked (Docs/OriginalBossNetworkingArchitecture.md §9.6, Docs/MultiplayerLoadingContract.md §5.7).
    /// </summary>
    /// <remarks>
    /// The arena counterpart of <see cref="BossSnapshot"/>, and a <b>separate</b> wire type (FG-ARCH-008,
    /// ADR-005): it carries <b>no</b> boss phase, attack, or health field, so a boss reusable across arenas never
    /// carries one arena's mechanism vocabulary. Its shape is the subset the PoC test arena needs — active
    /// mechanism groups + an exit flag rather than the general schema's separate hazard/gate arrays (R31).
    /// </remarks>
    public sealed record ArenaSnapshot(
        EncounterId Encounter,
        string ArenaId,
        int ArenaVersion,
        ProtocolVersion ProtocolVersion,
        SimulationTick Tick,
        IReadOnlyList<MechanismGroupId> ActiveMechanismGroups,
        bool ExitUnlocked,
        Sequence LastProcessedArenaEventSequence);
}
