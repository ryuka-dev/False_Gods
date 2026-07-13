using FalseGods.Core.Simulation;
using FalseGods.Protocol.Arena;

namespace FalseGods.Protocol.Wire
{
    /// <summary>
    /// The reliable, once-per-join composition of boss + arena + encounter state — the sole carrier for
    /// join-in-progress and full recovery (Docs/OriginalBossNetworkingArchitecture.md §9.8,
    /// Docs/MultiplayerLoadingContract.md §5.7, ADR-005).
    /// </summary>
    /// <remarks>
    /// A late or recovering client applies exactly one baseline, then resumes per-stream event processing from
    /// the <see cref="LastProcessedBossEventSequence"/> / <see cref="LastProcessedArenaEventSequence"/> it carries
    /// (invariant 6). It is the <b>only</b> type permitted to hold both a <see cref="BossSnapshot"/> and an
    /// <see cref="ArenaSnapshot"/> (FG-ARCH-008); whenever either half gains state, the baseline must gain it too,
    /// or late join silently loses it.
    /// </remarks>
    public sealed record EncounterBaseline(
        EncounterId Encounter,
        ProtocolVersion ProtocolVersion,
        string ArenaId,
        int ArenaVersion,
        ContentHash ContentHash,
        SimulationTick Tick,
        int EncounterPhaseId,
        BossSnapshot Boss,
        ArenaSnapshot Arena,
        Sequence LastProcessedBossEventSequence,
        Sequence LastProcessedArenaEventSequence);
}
