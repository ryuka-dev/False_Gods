using System;
using FalseGods.Core.Bosses;
using FalseGods.Core.Bosses.Events;
using FalseGods.RuntimeContracts.Presentation;

namespace FalseGods.Application.Presentation
{
    /// <summary>
    /// Translates the boss <b>domain</b> — Core state and <see cref="IBossDomainEvent"/>s — into the
    /// transport-agnostic presentation contracts a renderer consumes.
    /// </summary>
    /// <remarks>
    /// This is the single-player/host half of the mapper the architecture places in <c>FalseGods.Application</c>
    /// (Docs/Architecture.md §7, Docs/DefinitionOfDone.md §3 step 5): it is the one place that knows both the domain
    /// vocabulary and the presentation vocabulary. The client half — wire <c>BossSnapshot</c>/<c>BossEvent</c> →
    /// the same presentation contracts — is a separate mapper added with the Protocol DTOs (step 6); both converge
    /// on the identical <see cref="PresentationState"/> / <see cref="IPresentationEvent"/> so
    /// <see cref="IEncounterPresentation"/> cannot tell the modes apart.
    ///
    /// <para>
    /// It is pure and static: no Unity, no socket, no state of its own — which makes the boundary unit-testable
    /// (Docs/Architecture.md §7).
    /// </para>
    /// </remarks>
    public static class BossPresentationMapping
    {
        /// <summary>Project the boss's current continuous domain state into a <see cref="PresentationState"/>.</summary>
        public static PresentationState ToState(BossSimulation boss)
        {
            if (boss is null)
            {
                throw new ArgumentNullException(nameof(boss));
            }

            var healthFraction = boss.MaxHealth > 0
                ? Math.Max(0f, Math.Min(1f, boss.Health / (float)boss.MaxHealth))
                : 0f;

            return new PresentationState(
                boss.Id,
                boss.Position,
                boss.Facing,
                ToPhaseVisualId(boss.Phase),
                ToVisualActivity(boss.Activity),
                boss.IsWeakPointExposed,
                healthFraction);
        }

        /// <summary>
        /// Translate one discrete domain event into its presentation cue. Throws on an unrecognised event type so
        /// that adding a domain event forces a deliberate presentation-mapping decision rather than silently
        /// dropping a visual.
        /// </summary>
        public static IPresentationEvent ToEvent(IBossDomainEvent domainEvent)
        {
            switch (domainEvent)
            {
                case BossSpawned e:
                    return new BossAppeared(e.Boss, ToPhaseVisualId(e.Phase));
                case AttackTelegraphed e:
                    return new AttackTelegraphStarted(e.Boss, e.Attack, ToVisualKind(e.Kind), e.AimPoint, e.TelegraphSeconds);
                case AttackCommitted e:
                    return new AttackLanded(e.Boss, e.Attack, ToVisualKind(e.Kind), e.AimPoint);
                case WeakPointExposed e:
                    return new WeakPointVisibilityChanged(e.Boss, e.Exposed);
                case BossPhaseChanged e:
                    return new PhaseTransition(e.Boss, ToPhaseVisualId(e.Phase));
                case BossDamaged e:
                    return new BossHit(e.Boss, e.Amount, e.WeakPointHit);
                case BossDied e:
                    return new BossDefeated(e.Boss);
                case null:
                    throw new ArgumentNullException(nameof(domainEvent));
                default:
                    throw new ArgumentOutOfRangeException(
                        nameof(domainEvent),
                        domainEvent.GetType().Name,
                        "No presentation mapping is defined for this boss domain event.");
            }
        }

        private static int ToPhaseVisualId(BossPhase phase) => (int)phase;

        private static BossVisualActivity ToVisualActivity(BossActivity activity)
        {
            switch (activity)
            {
                case BossActivity.Idle:
                    return BossVisualActivity.Idle;
                case BossActivity.Telegraphing:
                    return BossVisualActivity.Telegraphing;
                case BossActivity.Committing:
                    return BossVisualActivity.Committing;
                case BossActivity.Recovering:
                    return BossVisualActivity.Recovering;
                case BossActivity.Dead:
                    return BossVisualActivity.Dead;
                default:
                    throw new ArgumentOutOfRangeException(nameof(activity), activity, "Unknown boss activity.");
            }
        }

        private static AttackVisualKind ToVisualKind(BossAttackKind kind)
        {
            switch (kind)
            {
                case BossAttackKind.AimedProjectile:
                    return AttackVisualKind.Projectile;
                case BossAttackKind.AreaTelegraph:
                    return AttackVisualKind.Area;
                default:
                    throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unknown boss attack kind.");
            }
        }
    }
}
