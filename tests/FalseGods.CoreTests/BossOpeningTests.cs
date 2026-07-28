using FalseGods.Core.Bosses;
using FalseGods.Core.Bosses.Events;
using FalseGods.Core.Simulation;
using Xunit;

namespace FalseGods.CoreTests
{
    /// <summary>
    /// A boss can be standing in its room long before anyone starts the fight, and once started it announces itself
    /// before any of it runs. Both halves matter: what is being tested is that a waiting boss really does nothing —
    /// no attack cycle, no damage taken — and that the wait does not later show up as a huge first frame.
    /// </summary>
    public sealed class BossOpeningTests
    {
        private static BossDefinition WithOpening(float openingSeconds) => new BossDefinition(
            maxHealth: 100,
            phaseTwoHealthFraction: 0.5f,
            moveSpeed: 2f,
            idleSeconds: 1f,
            telegraphSeconds: 1f,
            commitSeconds: 0.5f,
            recoverSeconds: 1f,
            weakPointDamageMultiplier: 3,
            attackDamage: 10,
            aimedHitRadius: 2f,
            areaHitRadius: 5f,
            openingSeconds: openingSeconds);

        [Fact]
        public void Spawning_starts_the_fight_unless_asked_to_wait()
        {
            var boss = new BossTestHarness().Build();

            boss.Spawn(SimVector2.Zero);

            Assert.True(boss.HasBegun);
            Assert.True(BossTestHarness.Has<BossBegan>(boss.DrainEvents()));
        }

        [Fact]
        public void A_boss_told_to_wait_is_placed_but_has_not_begun()
        {
            var boss = new BossTestHarness().Build();

            boss.Spawn(new SimVector2(3f, 4f), 8f, waitToBegin: true);

            Assert.True(boss.IsSpawned);
            Assert.False(boss.HasBegun);
            Assert.True(boss.IsOutsideTheFight);
            var events = boss.DrainEvents();
            Assert.True(BossTestHarness.Has<BossSpawned>(events));
            Assert.False(BossTestHarness.Has<BossBegan>(events));
        }

        [Fact]
        public void A_waiting_boss_never_attacks_however_long_it_stands_there()
        {
            var h = new BossTestHarness().WithParticipantAt(1, 0f, 5f);
            var boss = h.Build();
            boss.Spawn(SimVector2.Zero, waitToBegin: true);
            boss.DrainEvents();

            for (var i = 0; i < 20; i++)
            {
                Assert.Empty(h.Step(1f));
            }

            Assert.Equal(BossActivity.Idle, boss.Activity);
        }

        [Fact]
        public void A_waiting_boss_takes_no_damage()
        {
            var boss = new BossTestHarness().Build();
            boss.Spawn(SimVector2.Zero, waitToBegin: true);
            boss.DrainEvents();

            boss.ApplyDamage(50);

            Assert.Equal(boss.MaxHealth, boss.Health);
            Assert.Empty(boss.DrainEvents());
        }

        [Fact]
        public void Beginning_announces_itself_once()
        {
            var h = new BossTestHarness();
            var boss = h.Build();
            boss.Spawn(SimVector2.Zero, waitToBegin: true);
            boss.DrainEvents();

            Assert.True(boss.Begin());

            Assert.True(boss.HasBegun);
            Assert.Equal(boss.Id, BossTestHarness.Single<BossBegan>(boss.DrainEvents()).Boss);

            Assert.False(boss.Begin());
            Assert.Empty(boss.DrainEvents());
        }

        [Fact]
        public void An_unspawned_boss_cannot_be_started()
        {
            var boss = new BossTestHarness().Build();

            Assert.False(boss.Begin());

            Assert.False(boss.HasBegun);
            Assert.Empty(boss.DrainEvents());
        }

        [Fact]
        public void A_dead_boss_cannot_be_started()
        {
            var boss = new BossTestHarness().Build();
            boss.Spawn(SimVector2.Zero);
            boss.ApplyDamage(BossTestHarness.StandardDefinition.MaxHealth * 10);
            boss.DrainEvents();

            Assert.False(boss.Begin());
            Assert.Empty(boss.DrainEvents());
        }

        [Fact]
        public void Nothing_runs_and_nothing_lands_during_the_opening()
        {
            var h = new BossTestHarness().WithParticipantAt(1, 0f, 5f);
            var boss = h.Build(WithOpening(2f));
            boss.Spawn(SimVector2.Zero, waitToBegin: true);
            boss.Begin();
            boss.DrainEvents();

            Assert.True(boss.IsOpening);

            // Well past the idle window, so a boss that were running would have telegraphed by now.
            Assert.Empty(h.Step(1.5f));
            boss.ApplyDamage(50);
            Assert.Equal(boss.MaxHealth, boss.Health);
            Assert.True(boss.IsOpening);
        }

        [Fact]
        public void The_fight_runs_once_the_opening_is_over_and_the_wait_is_not_charged_to_it()
        {
            var h = new BossTestHarness().WithParticipantAt(1, 0f, 5f);
            var boss = h.Build(WithOpening(2f));
            boss.Spawn(SimVector2.Zero, waitToBegin: true);

            h.Clock.Advance(30f); // it stood there for half a minute before anyone walked in
            boss.Begin();
            boss.DrainEvents();

            h.Step(2.5f); // opening over
            Assert.False(boss.IsOpening);
            Assert.False(boss.IsOutsideTheFight);

            // The idle window is a second, and it starts when the opening ends — not thirty seconds ago, and not
            // two and a half. One more short step is what selects the attack.
            Assert.Equal(BossActivity.Idle, boss.Activity);
            Assert.True(BossTestHarness.Has<AttackTelegraphed>(h.Step(1f)));
        }

        [Fact]
        public void A_boss_with_no_opening_is_in_the_fight_the_moment_it_begins()
        {
            var h = new BossTestHarness().WithParticipantAt(1, 0f, 5f);
            var boss = h.Build();
            boss.Spawn(SimVector2.Zero, waitToBegin: true);
            boss.Begin();
            boss.DrainEvents();

            Assert.False(boss.IsOpening);
            boss.ApplyDamage(10);
            Assert.Equal(boss.MaxHealth - 10, boss.Health);
        }
    }
}
