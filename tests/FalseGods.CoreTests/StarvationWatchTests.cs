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
        /// <summary>Stands in for a round trip and a half — the caller measures it from the carriers' real walk.</summary>
        private const float Patience = 20f;

        private static StarvationChange Wait(StarvationWatch watch, float seconds, int bandAlive = 0) =>
            watch.Advance(seconds, deliveryArrived: false, Patience, bandAlive);

        private static StarvationChange Deliver(StarvationWatch watch, int bandAlive = 0) =>
            watch.Advance(0.1f, deliveryArrived: true, Patience, bandAlive);

        [Fact]
        public void The_gap_a_working_route_leaves_passes_unremarked()
        {
            var watch = new StarvationWatch();

            Assert.Equal(StarvationChange.Nothing, Wait(watch, Patience - 0.1f));
            Assert.False(watch.Enraged);
        }

        [Fact]
        public void Nothing_arriving_for_long_enough_is_answered()
        {
            var watch = new StarvationWatch();

            Wait(watch, Patience - 1f);

            Assert.Equal(StarvationChange.Enraged, Wait(watch, 1.1f));
            Assert.True(watch.Enraged);
        }

        [Fact]
        public void An_empty_pile_is_not_starvation_while_loads_keep_arriving()
        {
            // The boss clears its pile every volley, so its level says nothing. Only arrivals count, and here they
            // keep coming.
            var watch = new StarvationWatch();

            for (var i = 0; i < 20; i++)
            {
                Assert.Equal(StarvationChange.Nothing, Wait(watch, Patience * 0.5f));
                Assert.Equal(StarvationChange.Nothing, Deliver(watch));
            }

            Assert.False(watch.Enraged);
        }

        [Fact]
        public void A_delivery_before_the_deadline_resets_the_count()
        {
            var watch = new StarvationWatch();

            Wait(watch, Patience - 0.1f);
            Deliver(watch);
            Assert.Equal(0f, watch.SinceDelivery);

            Assert.Equal(StarvationChange.Nothing, Wait(watch, Patience - 0.1f));
            Assert.False(watch.Enraged);
        }

        [Fact]
        public void Supply_alone_does_not_calm_it_while_its_band_still_stands()
        {
            var watch = new StarvationWatch();
            Wait(watch, Patience + 1f);

            // Delivering while its guard is alive would be buying the rage off far too cheaply.
            Assert.Equal(StarvationChange.Nothing, Deliver(watch, bandAlive: 3));
            Assert.True(watch.Enraged);
        }

        [Fact]
        public void Killing_its_band_alone_does_not_calm_it_while_the_route_is_still_cut()
        {
            var watch = new StarvationWatch();
            Wait(watch, Patience + 1f);

            Assert.Equal(StarvationChange.Nothing, Wait(watch, 1f, bandAlive: 0));
            Assert.True(watch.Enraged);
        }

        [Fact]
        public void Both_together_calm_it()
        {
            var watch = new StarvationWatch();
            Wait(watch, Patience + 1f);
            Deliver(watch, bandAlive: 2);

            Assert.Equal(StarvationChange.Calmed, Deliver(watch, bandAlive: 0));
            Assert.False(watch.Enraged);
        }

        [Fact]
        public void The_route_running_calms_it_even_between_two_arrivals()
        {
            // The calm must not have to land on the exact frame a load arrives: deliveries are moments, and the
            // band can die between two of them.
            var watch = new StarvationWatch();
            Wait(watch, Patience + 1f);
            Deliver(watch, bandAlive: 1);

            Assert.Equal(StarvationChange.Calmed, Wait(watch, 1f, bandAlive: 0));
        }

        [Fact]
        public void Running_dry_again_answers_again()
        {
            var watch = new StarvationWatch();
            Wait(watch, Patience + 1f);
            Deliver(watch);
            Assert.False(watch.Enraged);

            Assert.Equal(StarvationChange.Enraged, Wait(watch, Patience + 1f));
        }

        [Fact]
        public void A_fight_that_ended_leaves_no_rage_for_the_next_one()
        {
            var watch = new StarvationWatch();
            Wait(watch, Patience + 1f);
            Assert.True(watch.Enraged);

            watch.Reset();

            Assert.False(watch.Enraged);
            Assert.Equal(0f, watch.SinceDelivery);
        }

        [Fact]
        public void Throwing_without_hitting_anybody_provokes_it_too()
        {
            var watch = new StarvationWatch();

            // Supplied the whole time — the route is working perfectly, and none of it is landing.
            for (var i = 0; i < 29; i++)
            {
                Assert.Equal(
                    StarvationChange.Nothing,
                    watch.Advance(1f, deliveryArrived: true, patienceSeconds: 10f, emergencyBandAlive: 0,
                        hitSomebody: false, futilitySeconds: 30f));
            }

            Assert.Equal(
                StarvationChange.Enraged,
                watch.Advance(1f, deliveryArrived: true, patienceSeconds: 10f, emergencyBandAlive: 0,
                    hitSomebody: false, futilitySeconds: 30f));
            Assert.Equal(StarvationReason.NothingLanding, watch.Reason);
        }

        [Fact]
        public void A_hit_winds_the_futility_clock_back()
        {
            var watch = new StarvationWatch();
            for (var i = 0; i < 29; i++)
            {
                watch.Advance(1f, deliveryArrived: true, patienceSeconds: 10f, emergencyBandAlive: 0,
                    hitSomebody: false, futilitySeconds: 30f);
            }

            watch.Advance(1f, deliveryArrived: true, patienceSeconds: 10f, emergencyBandAlive: 0,
                hitSomebody: true, futilitySeconds: 30f);

            Assert.Equal(
                StarvationChange.Nothing,
                watch.Advance(1f, deliveryArrived: true, patienceSeconds: 10f, emergencyBandAlive: 0,
                    hitSomebody: false, futilitySeconds: 30f));
        }

        [Fact]
        public void Landing_again_is_not_enough_on_its_own_to_calm_it()
        {
            var watch = new StarvationWatch();
            for (var i = 0; i < 31; i++)
            {
                watch.Advance(1f, deliveryArrived: true, patienceSeconds: 10f, emergencyBandAlive: 0,
                    hitSomebody: false, futilitySeconds: 30f);
            }

            Assert.True(watch.Enraged);

            // Hitting again while its band is still standing buys nothing, exactly as supplying it again does not.
            Assert.Equal(
                StarvationChange.Nothing,
                watch.Advance(1f, deliveryArrived: true, patienceSeconds: 10f, emergencyBandAlive: 2,
                    hitSomebody: true, futilitySeconds: 30f));

            Assert.Equal(
                StarvationChange.Calmed,
                watch.Advance(1f, deliveryArrived: true, patienceSeconds: 10f, emergencyBandAlive: 0,
                    hitSomebody: true, futilitySeconds: 30f));
        }

        [Fact]
        public void A_boss_with_no_futility_window_is_only_provoked_by_starving()
        {
            var watch = new StarvationWatch();
            for (var i = 0; i < 500; i++)
            {
                Assert.Equal(
                    StarvationChange.Nothing,
                    watch.Advance(1f, deliveryArrived: true, patienceSeconds: 10f, emergencyBandAlive: 0));
            }
        }
    }
}
