using FalseGods.Core.Bosses;
using FalseGods.Core.Bosses.Events;
using FalseGods.Core.Simulation;
using Xunit;

namespace FalseGods.CoreTests
{
    /// <summary>
    /// The boss holds whether it is enraged, even though what provokes it — the room's supply line running dry —
    /// is not the boss's to know. Everything downstream reads it from here, so what matters is that it changes
    /// only when it really changed, and that it announces every change exactly once.
    /// </summary>
    public sealed class BossRageTests
    {
        [Fact]
        public void A_fresh_boss_is_not_enraged()
        {
            var boss = new BossTestHarness().Build();
            boss.Spawn(SimVector2.Zero);

            Assert.False(boss.IsEnraged);
        }

        [Fact]
        public void Enraging_sets_the_state_and_announces_it_once()
        {
            var h = new BossTestHarness();
            var boss = h.Build();
            boss.Spawn(SimVector2.Zero);
            boss.DrainEvents();

            Assert.True(boss.SetEnraged(true));

            Assert.True(boss.IsEnraged);
            var enraged = BossTestHarness.Single<BossEnraged>(boss.DrainEvents());
            Assert.Equal(boss.Id, enraged.Boss);
            Assert.True(enraged.Enraged);
        }

        [Fact]
        public void Saying_it_again_changes_nothing_and_announces_nothing()
        {
            var boss = new BossTestHarness().Build();
            boss.Spawn(SimVector2.Zero);
            boss.SetEnraged(true);
            boss.DrainEvents();

            Assert.False(boss.SetEnraged(true));

            Assert.True(boss.IsEnraged);
            Assert.Empty(boss.DrainEvents());
        }

        [Fact]
        public void Calming_down_is_announced_too()
        {
            var boss = new BossTestHarness().Build();
            boss.Spawn(SimVector2.Zero);
            boss.SetEnraged(true);
            boss.DrainEvents();

            Assert.True(boss.SetEnraged(false));

            Assert.False(boss.IsEnraged);
            var calmed = BossTestHarness.Single<BossEnraged>(boss.DrainEvents());
            Assert.False(calmed.Enraged);
        }

        [Fact]
        public void A_boss_that_has_not_appeared_yet_has_nothing_to_rage_about()
        {
            var boss = new BossTestHarness().Build();

            Assert.False(boss.SetEnraged(true));

            Assert.False(boss.IsEnraged);
            Assert.Empty(boss.DrainEvents());
        }

        [Fact]
        public void A_dead_boss_is_past_raging()
        {
            var boss = new BossTestHarness().Build();
            boss.Spawn(SimVector2.Zero);
            boss.ApplyDamage(BossTestHarness.StandardDefinition.MaxHealth * 10);
            boss.DrainEvents();

            Assert.False(boss.SetEnraged(true));

            Assert.False(boss.IsEnraged);
            Assert.Empty(boss.DrainEvents());
        }
    }
}
