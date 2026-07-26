using System;
using System.Collections.Generic;
using FalseGods.Core.Bosses.Combat;
using Xunit;

namespace FalseGods.CoreTests
{
    /// <summary>
    /// The supply line decides when the room yields the boss's ammunition. Its whole job is timing under a
    /// ceiling, so these pin the two things a fight would notice: that a point produces on its own clock, and
    /// that a full one does not bank the waiting and then flood the room.
    /// </summary>
    public sealed class SupplyLineTests
    {
        private static readonly SupplyLineShape Shape =
            new SupplyLineShape(secondsPerCrate: 5f, sourceCapacity: 3, deliveryCapacity: 8);

        private static IReadOnlyList<int> Empty(int sources) => new int[sources];

        [Fact]
        public void A_point_produces_once_its_interval_has_passed()
        {
            var line = new SupplyLine(Shape, sourceCount: 1);

            line.Advance(4.9f, Empty(1));
            Assert.Empty(line.DrainProductionRequests());

            line.Advance(0.2f, Empty(1));
            Assert.Equal(new[] { 0 }, line.DrainProductionRequests());
        }

        [Fact]
        public void Every_point_keeps_its_own_clock_so_two_supply_twice_as_fast()
        {
            var line = new SupplyLine(Shape, sourceCount: 2);

            line.Advance(5f, Empty(2));

            Assert.Equal(new[] { 0, 1 }, line.DrainProductionRequests());
        }

        [Fact]
        public void A_full_point_holds_instead_of_producing()
        {
            var line = new SupplyLine(Shape, sourceCount: 1);

            line.Advance(60f, new[] { Shape.SourceCapacity });

            Assert.Empty(line.DrainProductionRequests());
        }

        [Fact]
        public void Clearing_a_long_full_point_yields_one_crate_not_the_whole_backlog()
        {
            var line = new SupplyLine(Shape, sourceCount: 1);

            // Full and ignored for a long time: the clock must not bank all of it.
            line.Advance(600f, new[] { Shape.SourceCapacity });
            Assert.Empty(line.DrainProductionRequests());

            // A player clears the pile. One crate comes immediately, because the point was already due...
            line.Advance(0.1f, Empty(1));
            Assert.Equal(new[] { 0 }, line.DrainProductionRequests());

            // ...and then the ordinary wait resumes rather than another burst.
            line.Advance(0.1f, Empty(1));
            Assert.Empty(line.DrainProductionRequests());
        }

        [Fact]
        public void A_long_frame_still_yields_only_one_crate_per_point()
        {
            var line = new SupplyLine(Shape, sourceCount: 1);

            line.Advance(100f, Empty(1));

            Assert.Single(line.DrainProductionRequests());
        }

        [Fact]
        public void A_room_with_no_production_points_produces_nothing()
        {
            var line = new SupplyLine(Shape, sourceCount: 0);

            line.Advance(1000f, Array.Empty<int>());

            Assert.Empty(line.DrainProductionRequests());
            Assert.Equal(0, line.SourceCount);
        }

        [Fact]
        public void A_short_count_list_treats_the_missing_points_as_empty_rather_than_stalling()
        {
            var line = new SupplyLine(Shape, sourceCount: 2);

            line.Advance(5f, new[] { Shape.SourceCapacity }); // only source 0 reported, and it is full

            Assert.Equal(new[] { 1 }, line.DrainProductionRequests());
        }

        [Fact]
        public void A_delivery_pile_stops_accepting_at_its_ceiling()
        {
            var line = new SupplyLine(Shape, sourceCount: 1);

            Assert.True(line.AcceptsDelivery(Shape.DeliveryCapacity - 1));
            Assert.False(line.AcceptsDelivery(Shape.DeliveryCapacity));
        }

        [Fact]
        public void An_interval_of_zero_is_refused_rather_than_producing_every_frame()
        {
            Assert.Throws<ArgumentOutOfRangeException>(
                () => new SupplyLineShape(secondsPerCrate: 0f, sourceCapacity: 1, deliveryCapacity: 1));
        }
    }

    /// <summary>
    /// A pile identity decides whether the boss may fire a crate, and it arrives from another machine — so the
    /// rebuilding half is what these cover.
    /// </summary>
    public sealed class CratePileIdTests
    {
        [Fact]
        public void A_source_and_a_delivery_of_the_same_number_are_different_places()
        {
            Assert.NotEqual(CratePileId.Source(0), CratePileId.Delivery(0));
        }

        [Fact]
        public void Every_loose_crate_is_on_the_same_nowhere()
        {
            Assert.Equal(CratePileId.Loose, CratePileId.Loose);
            Assert.True(CratePileId.TryFrom((int)CratePileKind.Loose, 7, out var pile));
            Assert.Equal(CratePileId.Loose, pile);
        }

        [Fact]
        public void A_pile_round_trips_through_the_numbers_the_wire_carries()
        {
            var original = CratePileId.Delivery(3);

            Assert.True(CratePileId.TryFrom((int)original.Kind, original.Index, out var rebuilt));
            Assert.Equal(original, rebuilt);
        }

        [Theory]
        [InlineData(99, 0)]   // a kind that does not exist
        [InlineData(-1, 0)]   // a negative kind
        [InlineData(1, -1)]   // a negative index
        public void A_claim_that_is_not_a_pile_is_refused_rather_than_becoming_one(int kind, int index)
        {
            Assert.False(CratePileId.TryFrom(kind, index, out _));
        }

        [Fact]
        public void A_negative_index_is_refused_at_construction_too()
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => CratePileId.Source(-1));
        }
    }
}
