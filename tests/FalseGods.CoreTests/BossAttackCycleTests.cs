using System;
using System.Linq;
using FalseGods.Core.Bosses;
using FalseGods.Core.Bosses.Events;
using FalseGods.Core.Simulation;
using Xunit;

namespace FalseGods.CoreTests
{
    public sealed class BossAttackCycleTests
    {
        private static void AssertClose(SimVector2 expected, SimVector2 actual)
        {
            Assert.True(
                Math.Abs(expected.X - actual.X) < 1e-4f && Math.Abs(expected.Z - actual.Z) < 1e-4f,
                $"expected {expected} but was {actual}");
        }

        [Fact]
        public void Idle_boss_telegraphs_an_attack_after_the_idle_delay_aimed_at_the_target()
        {
            var h = new BossTestHarness().WithRandom(0).WithParticipantAt(1, 10f, 0f);
            var boss = h.Build();
            boss.Spawn(SimVector2.Zero);
            boss.DrainEvents();

            var events = h.Step(BossTestHarness.StandardDefinition.IdleSeconds);

            var telegraph = BossTestHarness.Single<AttackTelegraphed>(events);
            Assert.Equal(new AttackInstanceId(1), telegraph.Attack);
            Assert.Equal(BossAttackKind.AimedProjectile, telegraph.Kind);
            Assert.Equal(new SimVector2(10f, 0f), telegraph.AimPoint);
            Assert.Equal(BossTestHarness.StandardDefinition.TelegraphSeconds, telegraph.TelegraphSeconds);
            Assert.Equal(BossActivity.Telegraphing, boss.Activity);
            Assert.Equal(new AttackInstanceId(1), boss.CurrentAttack);
        }

        [Fact]
        public void Random_zero_selects_the_projectile_and_one_selects_the_area_attack()
        {
            var h = new BossTestHarness().WithRandom(1).WithParticipantAt(1, 10f, 0f);
            var boss = h.Build();
            boss.Spawn(SimVector2.Zero);
            boss.DrainEvents();

            var telegraph = BossTestHarness.Single<AttackTelegraphed>(h.Step(1f));

            Assert.Equal(BossAttackKind.AreaTelegraph, telegraph.Kind);
        }

        [Fact]
        public void Full_cycle_runs_idle_telegraph_commit_recover_and_back_to_idle()
        {
            var def = BossTestHarness.StandardDefinition;
            var h = new BossTestHarness().WithRandom(0, 1).WithParticipantAt(1, 20f, 0f);
            var boss = h.Build();
            boss.Spawn(SimVector2.Zero);
            boss.DrainEvents();

            // idle -> telegraph
            var e1 = h.Step(def.IdleSeconds);
            Assert.True(BossTestHarness.Has<AttackTelegraphed>(e1));
            Assert.Equal(BossActivity.Telegraphing, boss.Activity);

            // telegraph -> commit
            var e2 = h.Step(def.TelegraphSeconds);
            var commit = BossTestHarness.Single<AttackCommitted>(e2);
            Assert.Equal(new AttackInstanceId(1), commit.Attack);
            Assert.Equal(BossAttackKind.AimedProjectile, commit.Kind);
            Assert.Equal(BossActivity.Committing, boss.Activity);

            // commit -> recover (weak point opens)
            var e3 = h.Step(def.CommitSeconds);
            Assert.True(BossTestHarness.Single<WeakPointExposed>(e3).Exposed);
            Assert.Equal(BossActivity.Recovering, boss.Activity);
            Assert.True(boss.IsWeakPointExposed);
            Assert.Null(boss.CurrentAttack);

            // recover -> idle (weak point closes)
            var e4 = h.Step(def.RecoverSeconds);
            Assert.False(BossTestHarness.Single<WeakPointExposed>(e4).Exposed);
            Assert.Equal(BossActivity.Idle, boss.Activity);
            Assert.False(boss.IsWeakPointExposed);

            // next cycle mints the SECOND attack id, using the next random value
            var e5 = h.Step(def.IdleSeconds);
            var telegraph2 = BossTestHarness.Single<AttackTelegraphed>(e5);
            Assert.Equal(new AttackInstanceId(2), telegraph2.Attack);
            Assert.Equal(BossAttackKind.AreaTelegraph, telegraph2.Kind);
        }

        [Fact]
        public void An_empty_roster_pauses_the_idle_timer_instead_of_attacking_nothing()
        {
            var h = new BossTestHarness().WithRandom(0);
            var boss = h.Build();
            boss.Spawn(SimVector2.Zero);
            boss.DrainEvents();

            // No participants: even well past the idle delay, nothing is selected.
            Assert.Empty(h.Step(10f));
            Assert.Equal(BossActivity.Idle, boss.Activity);

            // Someone arrives; one idle delay later the boss attacks.
            h.Participants.Set(new ParticipantId(1), new SimVector2(5f, 0f));
            var events = h.Step(BossTestHarness.StandardDefinition.IdleSeconds);
            Assert.True(BossTestHarness.Has<AttackTelegraphed>(events));
        }

        [Fact]
        public void Idle_boss_moves_toward_the_nearest_target_and_stops_on_it()
        {
            var h = new BossTestHarness().WithParticipantAt(1, 1f, 0f);
            var boss = h.Build();
            boss.Spawn(SimVector2.Zero);
            boss.DrainEvents();

            // 0.6 s at 2 m/s = up to 1.2 m; target is 1 m away, so the boss lands exactly on it and stays idle.
            h.Step(0.6f);

            AssertClose(new SimVector2(1f, 0f), boss.Position);
            Assert.Equal(BossActivity.Idle, boss.Activity);
        }

        [Fact]
        public void Idle_boss_faces_and_advances_partway_toward_a_distant_target()
        {
            var h = new BossTestHarness().WithParticipantAt(1, 10f, 0f);
            var boss = h.Build();
            boss.Spawn(SimVector2.Zero);
            boss.DrainEvents();

            h.Step(0.5f); // 2 m/s * 0.5 s = 1 m toward (10,0)

            AssertClose(new SimVector2(1f, 0f), boss.Position);
            AssertClose(new SimVector2(1f, 0f), boss.Facing);
        }

        [Fact]
        public void The_nearest_of_several_participants_is_targeted()
        {
            var h = new BossTestHarness()
                .WithParticipantAt(1, 10f, 0f)
                .WithParticipantAt(2, 3f, 0f)
                .WithRandom(0);
            var boss = h.Build();
            boss.Spawn(SimVector2.Zero);
            boss.DrainEvents();

            var telegraph = BossTestHarness.Single<AttackTelegraphed>(h.Step(1f));

            // Aim is the nearer participant (id 2 at x=3), after the boss has closed 2 m toward it first.
            Assert.Equal(new SimVector2(3f, 0f), telegraph.AimPoint);
        }
    }
}
