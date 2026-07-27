using System;
using FalseGods.Core.Bosses.Combat;
using Xunit;

namespace FalseGods.CoreTests
{
    /// <summary>
    /// The ladder decides how hard the room works as the boss loses health. What a player faces is a rate, and a
    /// rate falls out of carriers, load and the length of the walk — so these pin the reading of the ladder and
    /// the arithmetic the tuning is done against.
    /// </summary>
    public sealed class SupplyEscalationTests
    {
        private static SupplyEscalation Ladder() => new SupplyEscalation(
            new SupplyStep(0.80f, carriers: 8, loadPerCarrier: 8),
            new SupplyStep(0.60f, carriers: 9, loadPerCarrier: 9),
            new SupplyStep(0.40f, carriers: 10, loadPerCarrier: 10),
            new SupplyStep(0.20f, carriers: 11, loadPerCarrier: 11),
            new SupplyStep(0.00f, carriers: 12, loadPerCarrier: 12));

        [Fact]
        public void A_healthy_boss_gets_the_first_step()
        {
            Assert.Equal(8, Ladder().At(1f).Carriers);
        }

        [Fact]
        public void The_step_hardens_as_the_boss_loses_health()
        {
            var ladder = Ladder();

            Assert.Equal(8, ladder.At(0.81f).Carriers);
            Assert.Equal(9, ladder.At(0.61f).Carriers);
            Assert.Equal(10, ladder.At(0.41f).Carriers);
            Assert.Equal(11, ladder.At(0.21f).Carriers);
            Assert.Equal(12, ladder.At(0.01f).Carriers);
        }

        [Fact]
        public void A_threshold_belongs_to_the_step_below_it()
        {
            // Exactly at 0.80 the boss is no longer ABOVE 0.80, so the harder step is in force.
            Assert.Equal(9, Ladder().At(0.80f).Carriers);
        }

        [Fact]
        public void A_boss_on_its_last_sliver_is_supplied_by_the_hardest_step_not_by_nobody()
        {
            Assert.Equal(12, Ladder().At(0f).Carriers);
        }

        [Fact]
        public void An_empty_ladder_puts_nobody_on_the_route()
        {
            var none = new SupplyEscalation();

            Assert.Equal(0, none.At(1f).Carriers);
            Assert.Equal(0, none.At(0f).Throughput);
        }

        [Fact]
        public void Throughput_is_what_doubles_when_the_barrage_doubles()
        {
            var ladder = Ladder();

            Assert.Equal(64, ladder.At(1f).Throughput);
            Assert.Equal(144, ladder.At(0f).Throughput);
            Assert.True(ladder.At(0f).Throughput >= 2 * ladder.At(1f).Throughput - 1);
        }

        [Fact]
        public void The_rate_is_throughput_over_the_round_trip()
        {
            // 64 crates in flight per 22-second round trip is a shade under three a second.
            var opening = SupplyEscalation.RatePerSecond(Ladder().At(1f), roundTripSeconds: 22f);
            Assert.InRange(opening, 2.8f, 3.0f);

            // ...and the last step is a shade over six.
            var closing = SupplyEscalation.RatePerSecond(Ladder().At(0f), roundTripSeconds: 22f);
            Assert.InRange(closing, 6.4f, 6.6f);
        }

        [Fact]
        public void A_route_that_takes_no_time_supplies_nothing_rather_than_dividing_by_zero()
        {
            Assert.Equal(0f, SupplyEscalation.RatePerSecond(Ladder().At(1f), roundTripSeconds: 0f));
        }

        [Fact]
        public void Steps_authored_out_of_order_are_refused()
        {
            Assert.Throws<ArgumentException>(() => new SupplyEscalation(
                new SupplyStep(0.20f, 8, 8),
                new SupplyStep(0.80f, 9, 9)));
        }
    }
}
