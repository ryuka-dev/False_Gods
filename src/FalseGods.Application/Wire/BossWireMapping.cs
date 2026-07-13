using System;
using FalseGods.Core.Bosses;
using FalseGods.Core.Bosses.Events;
using FalseGods.Core.Simulation;
using FalseGods.Protocol.Wire;

namespace FalseGods.Application.Wire
{
    /// <summary>
    /// The host-side mapper from the boss <b>domain</b> to the <b>wire</b>: Core state and
    /// <see cref="IBossDomainEvent"/>s into <see cref="BossSnapshot"/> / <see cref="IBossWireEvent"/> for
    /// replication (Docs/OriginalBossNetworkingArchitecture.md §9.6).
    /// </summary>
    /// <remarks>
    /// The host runs the simulation and produces both presentation (via <c>BossPresentationMapping</c>) and wire
    /// output; this is the wire half. It is pure and static — no channel, no state. The receiving client maps the
    /// wire back to presentation with <c>WirePresentationMapping</c>, and the two presentation results agree, which
    /// is what makes single-player, host, and client share one presentation entry point (Architecture §7).
    ///
    /// <para>
    /// The wire enums are carried as ints: <c>StateId = (int)BossActivity</c>, <c>PhaseId = (int)BossPhase</c>,
    /// <c>AttackDefinitionId = (int)BossAttackKind</c>. <see cref="WirePresentationMapping"/> reverses them.
    /// </para>
    /// </remarks>
    public static class BossWireMapping
    {
        /// <summary>Project the boss's current continuous state into a <see cref="BossSnapshot"/>.</summary>
        /// <remarks>
        /// <c>StateStartTick</c> is set to <paramref name="tick"/> and <c>Target</c> to <c>null</c>: the test boss
        /// does not track an activity-start tick or expose its current target, and neither feeds
        /// <c>PresentationState</c> (so client presentation is unaffected). They are wire fields awaiting a boss
        /// that needs them (Docs/RiskList.md R31).
        /// </remarks>
        public static BossSnapshot ToSnapshot(
            BossSimulation boss,
            EncounterId encounter,
            DefinitionId definition,
            SimulationTick tick,
            Sequence lastProcessedBossEventSequence)
        {
            if (boss is null)
            {
                throw new ArgumentNullException(nameof(boss));
            }

            var activeAttack = boss.CurrentAttack;
            var activeAttackDefinitionId = activeAttack.HasValue ? (int?)(int)boss.CurrentAttackKind : null;

            return new BossSnapshot(
                encounter,
                boss.Id,
                definition,
                ProtocolVersion.Current,
                tick,
                (int)boss.Phase,
                (int)boss.Activity,
                tick,
                activeAttack,
                activeAttackDefinitionId,
                Target: null,
                boss.Position,
                boss.Facing,
                boss.Health,
                boss.MaxHealth,
                boss.IsWeakPointExposed,
                lastProcessedBossEventSequence);
        }

        /// <summary>Translate one boss domain event into its wire event, stamped with a sequence and tick.</summary>
        public static IBossWireEvent ToWireEvent(IBossDomainEvent domainEvent, Sequence sequence, SimulationTick tick)
        {
            switch (domainEvent)
            {
                case BossSpawned e:
                    return new BossAppearedEvent(sequence, tick, (int)e.Phase);
                case AttackTelegraphed e:
                    return new BossAttackTelegraphedEvent(sequence, tick, e.Attack, (int)e.Kind, e.AimPoint, e.TelegraphSeconds);
                case AttackCommitted e:
                    return new BossAttackCommittedEvent(sequence, tick, e.Attack, (int)e.Kind, e.AimPoint);
                case WeakPointExposed e:
                    return new BossWeakPointChangedEvent(sequence, tick, e.Exposed);
                case BossPhaseChanged e:
                    return new BossPhaseChangedEvent(sequence, tick, (int)e.Phase);
                case BossDamaged e:
                    return new BossDamagedEvent(sequence, tick, e.Amount, e.RemainingHealth, e.WeakPointHit);
                case BossDied _:
                    return new BossDefeatedEvent(sequence, tick);
                case null:
                    throw new ArgumentNullException(nameof(domainEvent));
                default:
                    throw new ArgumentOutOfRangeException(nameof(domainEvent), domainEvent.GetType().Name, "No wire mapping for this boss domain event.");
            }
        }
    }
}
