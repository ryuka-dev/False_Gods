using System.Linq;
using FalseGods.Core.Bosses;
using FalseGods.Core.Bosses.Events;
using FalseGods.Core.Simulation;
using Xunit;

namespace FalseGods.CoreTests
{
    public sealed class BossCombatTests
    {
        private static BossTestHarness SpawnedWithTarget()
        {
            var h = new BossTestHarness().WithRandom(0).WithParticipantAt(1, 50f, 0f);
            h.Build().Spawn(SimVector2.Zero);
            h.Boss.DrainEvents();
            return h;
        }

        [Fact]
        public void A_normal_hit_reduces_health_by_its_raw_amount()
        {
            var h = SpawnedWithTarget();

            h.Boss.ApplyDamage(30);

            Assert.Equal(70, h.Boss.Health);
            var damaged = BossTestHarness.Single<BossDamaged>(h.Boss.DrainEvents());
            Assert.Equal(30, damaged.Amount);
            Assert.Equal(70, damaged.RemainingHealth);
            Assert.False(damaged.WeakPointHit);
        }

        [Fact]
        public void A_hit_on_the_exposed_weak_point_is_amplified_by_the_multiplier()
        {
            var def = BossTestHarness.StandardDefinition;
            var h = SpawnedWithTarget();

            // Drive the cycle to the recovery window where the weak point is open.
            h.Step(def.IdleSeconds);
            h.Step(def.TelegraphSeconds);
            h.Step(def.CommitSeconds);
            Assert.True(h.Boss.IsWeakPointExposed);
            h.Boss.DrainEvents();

            h.Boss.ApplyDamage(10);

            var expected = 10 * def.WeakPointDamageMultiplier; // 30
            Assert.Equal(100 - expected, h.Boss.Health);
            var damaged = BossTestHarness.Single<BossDamaged>(h.Boss.DrainEvents());
            Assert.Equal(expected, damaged.Amount);
            Assert.True(damaged.WeakPointHit);
        }

        [Fact]
        public void Crossing_the_threshold_enters_phase_two_exactly_once()
        {
            var h = SpawnedWithTarget();

            h.Boss.ApplyDamage(50); // 100 -> 50, at the threshold
            Assert.Equal(BossPhase.Two, h.Boss.Phase);
            var first = h.Boss.DrainEvents();
            Assert.Single(first.OfType<BossPhaseChanged>());
            Assert.Equal(BossPhase.Two, first.OfType<BossPhaseChanged>().Single().Phase);

            h.Boss.ApplyDamage(10); // 50 -> 40, still phase two, no second transition
            Assert.Empty(h.Boss.DrainEvents().OfType<BossPhaseChanged>());
        }

        [Fact]
        public void A_lethal_hit_kills_the_boss_and_does_not_also_report_a_phase_change()
        {
            var h = SpawnedWithTarget();

            h.Boss.ApplyDamage(100);

            Assert.Equal(0, h.Boss.Health);
            Assert.True(h.Boss.IsDead);
            Assert.Equal(BossActivity.Dead, h.Boss.Activity);
            var events = h.Boss.DrainEvents();
            Assert.True(BossTestHarness.Has<BossDamaged>(events));
            Assert.True(BossTestHarness.Has<BossDied>(events));
            Assert.Empty(events.OfType<BossPhaseChanged>());
        }

        [Fact]
        public void A_dead_boss_ignores_further_damage_and_stops_advancing()
        {
            var h = SpawnedWithTarget();
            h.Boss.ApplyDamage(100);
            h.Boss.DrainEvents();

            h.Boss.ApplyDamage(50);
            var events = h.Step(5f);

            Assert.Equal(0, h.Boss.Health);
            Assert.Empty(events);
            Assert.Empty(h.Boss.DrainEvents());
        }

        [Theory]
        [InlineData(0)]
        [InlineData(-5)]
        public void Non_positive_damage_is_ignored(int amount)
        {
            var h = SpawnedWithTarget();

            h.Boss.ApplyDamage(amount);

            Assert.Equal(100, h.Boss.Health);
            Assert.Empty(h.Boss.DrainEvents());
        }

        [Fact]
        public void Damage_before_spawn_is_ignored()
        {
            var h = new BossTestHarness();
            var boss = h.Build();

            boss.ApplyDamage(50);

            Assert.Equal(0, boss.Health); // never spawned, so still zero
            Assert.Empty(boss.DrainEvents());
        }
    }
}
