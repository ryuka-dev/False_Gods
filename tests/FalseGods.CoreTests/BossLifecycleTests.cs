using System.Linq;
using FalseGods.Core.Bosses;
using FalseGods.Core.Bosses.Events;
using FalseGods.Core.Simulation;
using Xunit;

namespace FalseGods.CoreTests
{
    public sealed class BossLifecycleTests
    {
        [Fact]
        public void Spawn_starts_at_full_health_in_phase_one_idle_and_emits_BossSpawned()
        {
            var h = new BossTestHarness();
            var boss = h.Build();

            boss.Spawn(new SimVector2(0f, 0f));

            Assert.True(boss.IsSpawned);
            Assert.Equal(BossTestHarness.StandardDefinition.MaxHealth, boss.Health);
            Assert.Equal(BossPhase.One, boss.Phase);
            Assert.Equal(BossActivity.Idle, boss.Activity);
            Assert.False(boss.IsDead);
            Assert.False(boss.IsWeakPointExposed);

            var spawned = BossTestHarness.Single<BossSpawned>(boss.DrainEvents());
            Assert.Equal(boss.Id, spawned.Boss);
            Assert.Equal(BossPhase.One, spawned.Phase);
            Assert.Equal(BossTestHarness.StandardDefinition.MaxHealth, spawned.Health);
        }

        [Fact]
        public void Spawn_is_idempotent()
        {
            var h = new BossTestHarness();
            var boss = h.Build();

            boss.Spawn(new SimVector2(1f, 2f));
            boss.DrainEvents();
            boss.Spawn(new SimVector2(9f, 9f));

            Assert.Empty(boss.DrainEvents());
            Assert.Equal(new SimVector2(1f, 2f), boss.Position);
        }

        [Fact]
        public void Advance_before_spawn_does_nothing()
        {
            var h = new BossTestHarness();
            var boss = h.Build();

            var events = h.Step(5f);

            Assert.Empty(events);
            Assert.False(boss.IsSpawned);
            Assert.Equal(BossActivity.Idle, boss.Activity);
        }

        [Fact]
        public void DrainEvents_clears_the_buffer()
        {
            var h = new BossTestHarness();
            var boss = h.Build();
            boss.Spawn(SimVector2.Zero);

            Assert.NotEmpty(boss.DrainEvents());
            Assert.Empty(boss.DrainEvents());
        }
    }
}
