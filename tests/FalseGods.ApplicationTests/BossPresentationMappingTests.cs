using System;
using FalseGods.Application.Presentation;
using FalseGods.Core.Bosses;
using FalseGods.Core.Bosses.Events;
using FalseGods.Core.Simulation;
using FalseGods.RuntimeContracts.Presentation;
using Xunit;

namespace FalseGods.ApplicationTests
{
    public sealed class BossPresentationMappingTests
    {
        private static readonly BossInstanceId Boss = new BossInstanceId(7);
        private static readonly SimVector2 Aim = new SimVector2(4f, -2f);

        [Fact]
        public void BossSpawned_maps_to_BossAppeared_with_the_phase_visual_id()
        {
            var mapped = Assert.IsType<BossAppeared>(BossPresentationMapping.ToEvent(new BossSpawned(Boss, BossPhase.Two, 80)));
            Assert.Equal(Boss, mapped.Boss);
            Assert.Equal(2, mapped.PhaseVisualId);
        }

        [Fact]
        public void AttackTelegraphed_maps_to_AttackTelegraphStarted_preserving_id_kind_aim_and_duration()
        {
            var domain = new AttackTelegraphed(Boss, new AttackInstanceId(3), BossAttackKind.AimedProjectile, Aim, 1.5f);

            var mapped = Assert.IsType<AttackTelegraphStarted>(BossPresentationMapping.ToEvent(domain));

            Assert.Equal(Boss, mapped.Boss);
            Assert.Equal(new AttackInstanceId(3), mapped.Attack);
            Assert.Equal(AttackVisualKind.Projectile, mapped.Kind);
            Assert.Equal(Aim, mapped.AimPoint);
            Assert.Equal(1.5f, mapped.TelegraphSeconds);
        }

        [Fact]
        public void AttackCommitted_maps_to_AttackLanded_with_the_area_visual_kind()
        {
            var domain = new AttackCommitted(Boss, new AttackInstanceId(9), BossAttackKind.AreaTelegraph, Aim);

            var mapped = Assert.IsType<AttackLanded>(BossPresentationMapping.ToEvent(domain));

            Assert.Equal(new AttackInstanceId(9), mapped.Attack);
            Assert.Equal(AttackVisualKind.Area, mapped.Kind);
            Assert.Equal(Aim, mapped.AimPoint);
        }

        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public void WeakPointExposed_maps_to_WeakPointVisibilityChanged(bool exposed)
        {
            var mapped = Assert.IsType<WeakPointVisibilityChanged>(
                BossPresentationMapping.ToEvent(new WeakPointExposed(Boss, exposed)));
            Assert.Equal(exposed, mapped.Exposed);
        }

        [Fact]
        public void BossPhaseChanged_maps_to_PhaseTransition()
        {
            var mapped = Assert.IsType<PhaseTransition>(
                BossPresentationMapping.ToEvent(new BossPhaseChanged(Boss, BossPhase.Two)));
            Assert.Equal(2, mapped.PhaseVisualId);
        }

        [Fact]
        public void BossDamaged_maps_to_BossHit_preserving_amount_and_weak_point_flag()
        {
            var mapped = Assert.IsType<BossHit>(
                BossPresentationMapping.ToEvent(new BossDamaged(Boss, 30, 70, WeakPointHit: true)));
            Assert.Equal(30, mapped.Amount);
            Assert.True(mapped.WeakPointHit);
        }

        [Fact]
        public void BossDied_maps_to_BossDefeated()
        {
            var mapped = Assert.IsType<BossDefeated>(BossPresentationMapping.ToEvent(new BossDied(Boss)));
            Assert.Equal(Boss, mapped.Boss);
        }

        [Fact]
        public void ToEvent_rejects_null()
        {
            Assert.Throws<ArgumentNullException>(() => BossPresentationMapping.ToEvent(null!));
        }

        private sealed record UnknownDomainEvent(BossInstanceId Boss) : IBossDomainEvent;

        [Fact]
        public void ToEvent_throws_on_an_unmapped_domain_event_rather_than_dropping_it()
        {
            Assert.Throws<ArgumentOutOfRangeException>(
                () => BossPresentationMapping.ToEvent(new UnknownDomainEvent(Boss)));
        }

        [Fact]
        public void ToState_rejects_null()
        {
            Assert.Throws<ArgumentNullException>(() => BossPresentationMapping.ToState(null!));
        }

        [Fact]
        public void ToState_projects_a_fresh_boss_as_full_health_phase_one_idle_at_its_spawn_position()
        {
            var f = new BossFixture();
            f.Boss.Spawn(new SimVector2(2f, 3f));

            var state = BossPresentationMapping.ToState(f.Boss);

            Assert.Equal(f.Boss.Id, state.Boss);
            Assert.Equal(new SimVector2(2f, 3f), state.Position);
            Assert.Equal(SimVector2.Zero, state.Facing);
            Assert.Equal(1, state.PhaseVisualId);
            Assert.Equal(BossVisualActivity.Idle, state.Activity);
            Assert.False(state.WeakPointExposed);
            Assert.Equal(1f, state.HealthFraction);
        }

        [Fact]
        public void ToState_reflects_a_phase_two_boss_at_half_health()
        {
            var f = new BossFixture();
            f.Boss.Spawn(SimVector2.Zero);
            f.Boss.ApplyDamage(50);

            var state = BossPresentationMapping.ToState(f.Boss);

            Assert.Equal(2, state.PhaseVisualId);
            Assert.Equal(0.5f, state.HealthFraction);
        }

        [Fact]
        public void ToState_reports_the_exposed_weak_point_during_recovery()
        {
            var f = new BossFixture();
            f.Boss.Spawn(SimVector2.Zero);
            f.Step(BossFixture.Definition.IdleSeconds);      // -> telegraphing
            f.Step(BossFixture.Definition.TelegraphSeconds); // -> committing
            f.Step(BossFixture.Definition.CommitSeconds);    // -> recovering

            var state = BossPresentationMapping.ToState(f.Boss);

            Assert.Equal(BossVisualActivity.Recovering, state.Activity);
            Assert.True(state.WeakPointExposed);
        }

        [Fact]
        public void ToState_reports_a_dead_boss_at_zero_health()
        {
            var f = new BossFixture();
            f.Boss.Spawn(SimVector2.Zero);
            f.Boss.ApplyDamage(1000);

            var state = BossPresentationMapping.ToState(f.Boss);

            Assert.Equal(BossVisualActivity.Dead, state.Activity);
            Assert.Equal(0f, state.HealthFraction);
        }
    }
}
