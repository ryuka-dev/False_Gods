using System;
using System.Collections.Generic;
using FalseGods.Core.Bosses;
using FalseGods.Core.Bosses.Events;
using FalseGods.RuntimeContracts.Presentation;

namespace FalseGods.Application.Presentation
{
    /// <summary>
    /// Drives an <see cref="IEncounterPresentation"/> from the boss domain: it maps one tick's discrete domain
    /// events to presentation cues and the boss's continuous state to a <see cref="PresentationState"/>, then pushes
    /// both through the single presentation entry point.
    /// </summary>
    /// <remarks>
    /// This is the local (single-player and host) driver of the presentation seam. It takes the events the caller
    /// has already drained from the boss, because on a host the <em>same</em> drained events are also fanned out to
    /// replication (Docs/Architecture.md §4.3) — draining them here would starve that path. Cues are played first,
    /// then the latest continuous state is applied, so presentation settles on a state consistent with the cues it
    /// just saw (a death cue followed by the <see cref="BossVisualActivity.Dead"/> state).
    ///
    /// <para>
    /// It makes no authoritative decision and holds no domain state; it only translates and forwards. That is what
    /// lets single-player and multiplayer share one presentation path (Docs/ADRs/ADR-003).
    /// </para>
    /// </remarks>
    public sealed class BossPresenter
    {
        private readonly IEncounterPresentation _presentation;

        public BossPresenter(IEncounterPresentation presentation)
        {
            _presentation = presentation ?? throw new ArgumentNullException(nameof(presentation));
        }

        /// <summary>
        /// Present one tick: play a cue for each of <paramref name="domainEvents"/> (in order), then apply
        /// <paramref name="boss"/>'s current continuous state.
        /// </summary>
        public void Present(BossSimulation boss, IReadOnlyList<IBossDomainEvent> domainEvents)
        {
            if (boss is null)
            {
                throw new ArgumentNullException(nameof(boss));
            }

            if (domainEvents is null)
            {
                throw new ArgumentNullException(nameof(domainEvents));
            }

            for (var i = 0; i < domainEvents.Count; i++)
            {
                _presentation.Handle(BossPresentationMapping.ToEvent(domainEvents[i]));
            }

            _presentation.Apply(BossPresentationMapping.ToState(boss));
        }
    }
}
