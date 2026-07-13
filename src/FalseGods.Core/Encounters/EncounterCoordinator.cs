using System;
using System.Collections.Generic;
using FalseGods.Core.Arena;
using FalseGods.Core.Bosses;
using FalseGods.Core.Bosses.Events;
using FalseGods.Core.Simulation;

namespace FalseGods.Core.Encounters
{
    /// <summary>
    /// Bridges the boss and the arena via project-owned events, so neither reaches into the other's internals
    /// (Docs/Architecture.md §5, Docs/OriginalBossNetworkingArchitecture.md §9.2).
    /// </summary>
    /// <remarks>
    /// It owns the encounter scope — the <see cref="Simulation.EncounterId"/> and the <see cref="EncounterPhase"/> —
    /// and evaluates the encounter's rules, translating boss domain events into arena commands. For the PoC test
    /// encounter the rules are: the boss reaching phase two activates a configured mechanism group, and the boss's
    /// death unlocks the exit and marks the encounter defeated (Docs/MinimalProofOfConceptPlan.md B7/B9):
    ///
    /// <code>
    /// BossPhaseChanged(Two) → ActivateMechanismGroup(phaseTwoGroup)
    /// BossDied              → UnlockExit(); EncounterPhase = Defeated
    /// </code>
    ///
    /// <para>
    /// It consumes the boss's <b>already-drained</b> events (like the presenter and replication), because on a host
    /// the same drained events fan out to all three. The arena events its commands produce are drained from the
    /// <see cref="ArenaSimulation"/> by the caller for replication/presentation. A richer per-phase rule map is
    /// deliberately not built until a second boss needs it (Docs/DefinitionOfDone.md §3).
    /// </para>
    /// </remarks>
    public sealed class EncounterCoordinator
    {
        private readonly ArenaSimulation _arena;
        private readonly MechanismGroupId _phaseTwoGroup;

        public EncounterCoordinator(EncounterId id, ArenaSimulation arena, MechanismGroupId phaseTwoGroup)
        {
            Id = id;
            _arena = arena ?? throw new ArgumentNullException(nameof(arena));
            _phaseTwoGroup = phaseTwoGroup;
            Phase = EncounterPhase.PreFight;
        }

        /// <summary>The encounter scope id, shared by every replicated stream and the baseline.</summary>
        public EncounterId Id { get; }

        /// <summary>The encounter-level lifecycle stage.</summary>
        public EncounterPhase Phase { get; private set; }

        /// <summary>Move from <see cref="EncounterPhase.PreFight"/> to <see cref="EncounterPhase.Fighting"/> once the
        /// ready gate has passed and the boss is about to start. Idempotent from any non-terminal state.</summary>
        public void Begin()
        {
            if (Phase == EncounterPhase.PreFight)
            {
                Phase = EncounterPhase.Fighting;
            }
        }

        /// <summary>Mark the encounter as exiting for teardown. Terminal.</summary>
        public void BeginExit() => Phase = EncounterPhase.Exiting;

        /// <summary>
        /// Evaluate the encounter rules against one tick's boss domain events, driving the arena. Safe to call with
        /// an empty list.
        /// </summary>
        public void Process(IReadOnlyList<IBossDomainEvent> bossEvents)
        {
            if (bossEvents is null)
            {
                throw new ArgumentNullException(nameof(bossEvents));
            }

            for (var i = 0; i < bossEvents.Count; i++)
            {
                switch (bossEvents[i])
                {
                    case BossPhaseChanged phaseChanged when phaseChanged.Phase == BossPhase.Two:
                        _arena.ActivateMechanismGroup(_phaseTwoGroup);
                        break;
                    case BossDied _:
                        _arena.UnlockExit();
                        Phase = EncounterPhase.Defeated;
                        break;
                }
            }
        }
    }
}
