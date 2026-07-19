using System;
using System.Collections.Generic;
using FalseGods.Core.Bosses;
using FalseGods.Protocol.Wire;
using FalseGods.RuntimeContracts.Presentation;

namespace FalseGods.Application.Presentation
{
    /// <summary>
    /// The <b>client</b> half of the presentation mapper: <see cref="BossSnapshot"/> / <see cref="IBossWireEvent"/>
    /// into the same <see cref="PresentationState"/> / <see cref="IPresentationEvent"/> contracts the domain half
    /// produces (Docs/Architecture.md §7, Docs/OriginalBossNetworkingArchitecture.md §9.6).
    /// </summary>
    /// <remarks>
    /// This is the counterpart of <see cref="BossPresentationMapping"/>: a multiplayer client has no
    /// <c>BossSimulation</c>, so it maps the host's wire snapshots and events instead — and both paths converge on
    /// the identical <see cref="IEncounterPresentation"/>, so presentation cannot tell host from client (that
    /// convergence is asserted by the presentation-parity tests). It reverses the int-encoded wire enums that
    /// <see cref="BossWireMapping"/> produced.
    ///
    /// <para>
    /// A wire boss event carries no <c>BossInstanceId</c> (the stream is scoped to one boss); the caller supplies
    /// it from the current snapshot/baseline, which is why <see cref="ToEvent"/> takes a <see cref="BossInstanceId"/>.
    /// </para>
    /// </remarks>
    public static class WirePresentationMapping
    {
        public static PresentationState ToState(BossSnapshot snapshot)
        {
            if (snapshot is null)
            {
                throw new ArgumentNullException(nameof(snapshot));
            }

            var healthFraction = snapshot.MaxHealth > 0
                ? Math.Max(0f, Math.Min(1f, snapshot.Health / (float)snapshot.MaxHealth))
                : 0f;

            return new PresentationState(
                snapshot.Boss,
                snapshot.Position,
                snapshot.Facing,
                snapshot.PhaseId,
                ToVisualActivity(snapshot.StateId),
                snapshot.WeakPointExposed,
                healthFraction);
        }

        public static IPresentationEvent ToEvent(BossInstanceId boss, IBossWireEvent wireEvent)
        {
            switch (wireEvent)
            {
                case BossAppearedEvent e:
                    return new BossAppeared(boss, e.PhaseId);
                case BossAttackTelegraphedEvent e:
                    return new AttackTelegraphStarted(boss, e.Attack, ToVisualKind(e.AttackDefinitionId), e.AimPoint, e.TelegraphSeconds);
                case BossAttackCommittedEvent e:
                    return new AttackLanded(boss, e.Attack, ToVisualKind(e.AttackDefinitionId), e.AimPoint);
                case BossPhaseChangedEvent e:
                    return new PhaseTransition(boss, e.PhaseId);
                case BossWeakPointChangedEvent e:
                    return new WeakPointVisibilityChanged(boss, e.Exposed);
                case BossDamagedEvent e:
                    return new BossHit(boss, e.Amount, e.WeakPointHit);
                case BossDefeatedEvent _:
                    return new BossDefeated(boss);
                case null:
                    throw new ArgumentNullException(nameof(wireEvent));
                default:
                    throw new ArgumentOutOfRangeException(nameof(wireEvent), wireEvent.GetType().Name, "No presentation mapping for this boss wire event.");
            }
        }

        public static IPresentationEvent ToEvent(IArenaWireEvent wireEvent)
        {
            switch (wireEvent)
            {
                case ArenaMechanismGroupActivatedEvent e:
                    return new MechanismGroupEngaged(e.Group);
                case ArenaExitUnlockedEvent _:
                    return new ExitOpened();
                case null:
                    throw new ArgumentNullException(nameof(wireEvent));
                default:
                    throw new ArgumentOutOfRangeException(nameof(wireEvent), wireEvent.GetType().Name, "No presentation mapping for this arena wire event.");
            }
        }

        /// <summary>
        /// Reconstruct the arena's visual state from a snapshot as cues — the late-join path: a client that
        /// applied a baseline mid-fight replays one <see cref="MechanismGroupEngaged"/> per active group (and
        /// <see cref="ExitOpened"/> if the exit is already unlocked) instead of having missed the live events.
        /// The cues are idempotent visuals, so replaying them over an already-current presentation is safe.
        /// </summary>
        public static IReadOnlyList<IPresentationEvent> ToEvents(ArenaSnapshot snapshot)
        {
            if (snapshot is null)
            {
                throw new ArgumentNullException(nameof(snapshot));
            }

            var events = new List<IPresentationEvent>(snapshot.ActiveMechanismGroups.Count + 1);
            for (var i = 0; i < snapshot.ActiveMechanismGroups.Count; i++)
            {
                events.Add(new MechanismGroupEngaged(snapshot.ActiveMechanismGroups[i]));
            }

            if (snapshot.ExitUnlocked)
            {
                events.Add(new ExitOpened());
            }

            return events;
        }

        private static BossVisualActivity ToVisualActivity(int stateId)
        {
            switch (stateId)
            {
                case (int)BossActivity.Idle:
                    return BossVisualActivity.Idle;
                case (int)BossActivity.Telegraphing:
                    return BossVisualActivity.Telegraphing;
                case (int)BossActivity.Committing:
                    return BossVisualActivity.Committing;
                case (int)BossActivity.Recovering:
                    return BossVisualActivity.Recovering;
                case (int)BossActivity.Dead:
                    return BossVisualActivity.Dead;
                default:
                    throw new ArgumentOutOfRangeException(nameof(stateId), stateId, "Unknown wire boss state id.");
            }
        }

        private static AttackVisualKind ToVisualKind(int attackDefinitionId)
        {
            switch (attackDefinitionId)
            {
                case (int)BossAttackKind.AimedProjectile:
                    return AttackVisualKind.Projectile;
                case (int)BossAttackKind.AreaTelegraph:
                    return AttackVisualKind.Area;
                default:
                    throw new ArgumentOutOfRangeException(nameof(attackDefinitionId), attackDefinitionId, "Unknown wire attack definition id.");
            }
        }
    }
}
