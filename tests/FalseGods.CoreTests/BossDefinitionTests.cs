using System;
using FalseGods.Core.Bosses;
using Xunit;

namespace FalseGods.CoreTests
{
    public sealed class BossDefinitionTests
    {
        private static BossDefinition Valid() => BossTestHarness.StandardDefinition;

        [Fact]
        public void Phase_two_threshold_floors_the_fraction_of_max_health()
        {
            var def = new BossDefinition(
                maxHealth: 75,
                phaseTwoHealthFraction: 0.5f,
                moveSpeed: 1f,
                idleSeconds: 1f,
                telegraphSeconds: 1f,
                commitSeconds: 1f,
                recoverSeconds: 1f,
                weakPointDamageMultiplier: 2);

            Assert.Equal(37, def.PhaseTwoHealthThreshold); // floor(75 * 0.5) = 37
        }

        [Fact]
        public void A_valid_definition_is_accepted()
        {
            var def = Valid();
            Assert.Equal(100, def.MaxHealth);
            Assert.Equal(50, def.PhaseTwoHealthThreshold);
        }

        [Theory]
        [InlineData(0)]
        [InlineData(-10)]
        public void Non_positive_max_health_is_rejected(int maxHealth)
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => new BossDefinition(
                maxHealth, 0.5f, 1f, 1f, 1f, 1f, 1f, 2));
        }

        [Theory]
        [InlineData(0f)]
        [InlineData(1f)]
        [InlineData(1.5f)]
        [InlineData(-0.1f)]
        public void A_phase_fraction_outside_the_open_unit_interval_is_rejected(float fraction)
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => new BossDefinition(
                100, fraction, 1f, 1f, 1f, 1f, 1f, 2));
        }

        [Fact]
        public void A_negative_move_speed_is_rejected()
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => new BossDefinition(
                100, 0.5f, -1f, 1f, 1f, 1f, 1f, 2));
        }

        [Theory]
        [InlineData(0f)]
        [InlineData(-1f)]
        [InlineData(float.NaN)]
        public void A_non_positive_telegraph_duration_is_rejected(float telegraph)
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => new BossDefinition(
                100, 0.5f, 1f, 1f, telegraph, 1f, 1f, 2));
        }

        [Fact]
        public void A_weak_point_multiplier_below_one_is_rejected()
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => new BossDefinition(
                100, 0.5f, 1f, 1f, 1f, 1f, 1f, 0));
        }

        [Fact]
        public void A_zero_idle_delay_is_allowed()
        {
            var def = new BossDefinition(100, 0.5f, 1f, 0f, 1f, 1f, 1f, 2);
            Assert.Equal(0f, def.IdleSeconds);
        }
    }
}
