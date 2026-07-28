using FalseGods.Core.Bosses.Combat;
using Xunit;

namespace FalseGods.CoreTests
{
    /// <summary>
    /// When a starved boss answers, and what it takes to calm it. The rule exists because cutting the supply is
    /// the most interesting thing the room offers and must not also be the way to make the fight stop happening.
    /// </summary>
    public sealed class StarvationWatchTests
    {
        private static StarvationWatch Watch(float after = 3f) => new StarvationWatch(after);

        [Fact]
        public void An_ordinary_gap_between_deliveries_passes_unremarked()
        {
            var watch = Watch();

            Assert.Equal(StarvationChange.Nothing, watch.Advance(2.9f, hasAmmunition: false, emergencyBandAlive: 0));
            Assert.False(watch.Enraged);
        }

        [Fact]
        public void Running_dry_long_enough_is_answered()
        {
            var watch = Watch();

            watch.Advance(2f, hasAmmunition: false, emergencyBandAlive: 0);

            Assert.Equal(StarvationChange.Enraged, watch.Advance(1.1f, hasAmmunition: false, emergencyBandAlive: 0));
            Assert.True(watch.Enraged);
        }

        [Fact]
        public void A_delivery_before_the_deadline_resets_the_count()
        {
            var watch = Watch();

            watch.Advance(2.9f, hasAmmunition: false, emergencyBandAlive: 0);
            watch.Advance(0.1f, hasAmmunition: true, emergencyBandAlive: 0);   // one crate arrives
            Assert.Equal(0f, watch.StarvingFor);

            Assert.Equal(StarvationChange.Nothing, watch.Advance(2.9f, hasAmmunition: false, emergencyBandAlive: 0));
            Assert.False(watch.Enraged);
        }

        [Fact]
        public void Supply_alone_does_not_calm_it_while_its_band_still_stands()
        {
            var watch = Watch();
            watch.Advance(4f, hasAmmunition: false, emergencyBandAlive: 0);

            // Delivering one crate while its guard is alive would be buying the rage off far too cheaply.
            Assert.Equal(StarvationChange.Nothing, watch.Advance(1f, hasAmmunition: true, emergencyBandAlive: 3));
            Assert.True(watch.Enraged);
        }

        [Fact]
        public void Killing_its_band_alone_does_not_calm_it_while_the_route_is_still_cut()
        {
            var watch = Watch();
            watch.Advance(4f, hasAmmunition: false, emergencyBandAlive: 0);

            Assert.Equal(StarvationChange.Nothing, watch.Advance(1f, hasAmmunition: false, emergencyBandAlive: 0));
            Assert.True(watch.Enraged);
        }

        [Fact]
        public void Both_together_calm_it()
        {
            var watch = Watch();
            watch.Advance(4f, hasAmmunition: false, emergencyBandAlive: 0);
            watch.Advance(1f, hasAmmunition: true, emergencyBandAlive: 2);

            Assert.Equal(StarvationChange.Calmed, watch.Advance(1f, hasAmmunition: true, emergencyBandAlive: 0));
            Assert.False(watch.Enraged);
        }

        [Fact]
        public void Running_dry_again_answers_again()
        {
            var watch = Watch();
            watch.Advance(4f, hasAmmunition: false, emergencyBandAlive: 0);
            watch.Advance(1f, hasAmmunition: true, emergencyBandAlive: 0);
            Assert.False(watch.Enraged);

            Assert.Equal(StarvationChange.Enraged, watch.Advance(4f, hasAmmunition: false, emergencyBandAlive: 0));
        }

        [Fact]
        public void A_fight_that_ended_leaves_no_rage_for_the_next_one()
        {
            var watch = Watch();
            watch.Advance(4f, hasAmmunition: false, emergencyBandAlive: 0);
            Assert.True(watch.Enraged);

            watch.Reset();

            Assert.False(watch.Enraged);
            Assert.Equal(0f, watch.StarvingFor);
        }
    }
}
