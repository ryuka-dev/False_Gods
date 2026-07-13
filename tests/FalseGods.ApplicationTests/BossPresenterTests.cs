using System;
using System.Collections.Generic;
using FalseGods.Application.Presentation;
using FalseGods.Core.Bosses.Events;
using FalseGods.Core.Simulation;
using FalseGods.RuntimeContracts.Presentation;
using Xunit;

namespace FalseGods.ApplicationTests
{
    public sealed class BossPresenterTests
    {
        [Fact]
        public void Present_plays_a_cue_for_each_event_then_applies_the_state_once()
        {
            var f = new BossFixture();
            f.Boss.Spawn(SimVector2.Zero);
            f.Boss.DrainEvents(); // discard the spawn event
            // One command that emits two ordered events: damage that also crosses into phase two.
            f.Boss.ApplyDamage(50);
            var events = f.Boss.DrainEvents();

            var sink = new RecordingPresentation();
            new BossPresenter(sink).Present(f.Boss, events);

            Assert.Equal(new[] { "event:BossHit", "event:PhaseTransition", "state" }, sink.Calls);
            Assert.Single(sink.States);
            Assert.Equal(2, sink.States[0].PhaseVisualId);
        }

        [Fact]
        public void Present_with_no_events_still_applies_the_current_state()
        {
            var f = new BossFixture();
            f.Boss.Spawn(SimVector2.Zero);
            f.Boss.DrainEvents();

            var sink = new RecordingPresentation();
            new BossPresenter(sink).Present(f.Boss, Array.Empty<IBossDomainEvent>());

            Assert.Empty(sink.Events);
            Assert.Single(sink.States);
        }

        [Fact]
        public void Present_maps_a_spawn_into_a_BossAppeared_cue()
        {
            var f = new BossFixture();
            f.Boss.Spawn(SimVector2.Zero);
            var events = f.Boss.DrainEvents();

            var sink = new RecordingPresentation();
            new BossPresenter(sink).Present(f.Boss, events);

            Assert.IsType<BossAppeared>(Assert.Single(sink.Events));
        }

        [Fact]
        public void Present_does_not_advance_the_boss_it_only_observes()
        {
            var f = new BossFixture();
            f.Boss.Spawn(SimVector2.Zero);
            var events = f.Boss.DrainEvents();
            var healthBefore = f.Boss.Health;
            var activityBefore = f.Boss.Activity;

            new BossPresenter(new RecordingPresentation()).Present(f.Boss, events);

            Assert.Equal(healthBefore, f.Boss.Health);
            Assert.Equal(activityBefore, f.Boss.Activity);
        }

        [Fact]
        public void Constructor_rejects_a_null_presentation()
        {
            Assert.Throws<ArgumentNullException>(() => new BossPresenter(null!));
        }

        [Fact]
        public void Present_rejects_null_arguments()
        {
            var presenter = new BossPresenter(new RecordingPresentation());
            var f = new BossFixture();
            f.Boss.Spawn(SimVector2.Zero);

            Assert.Throws<ArgumentNullException>(() => presenter.Present(null!, Array.Empty<IBossDomainEvent>()));
            Assert.Throws<ArgumentNullException>(() => presenter.Present(f.Boss, null!));
        }
    }
}
