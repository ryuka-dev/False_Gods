using System;
using FalseGods.Core.Bosses.Combat;
using Xunit;

namespace FalseGods.CoreTests
{
    public sealed class ShotgunSpreadTests
    {
        [Fact]
        public void The_same_volley_scatters_the_same_way_on_every_peer()
        {
            // The whole reason the pattern is seeded: a volley must land identically wherever it is computed.
            for (var index = 0; index < 8; index++)
            {
                var first = ShotgunSpread.Offset(1234, index, 8, 2f, 6f);
                var second = ShotgunSpread.Offset(1234, index, 8, 2f, 6f);
                Assert.Equal(first.X, second.X);
                Assert.Equal(first.Z, second.Z);
            }
        }

        [Fact]
        public void Every_crate_lands_within_the_requested_radius_band()
        {
            const float min = 2f;
            const float max = 6f;
            for (var index = 0; index < 12; index++)
            {
                var offset = ShotgunSpread.Offset(99, index, 12, min, max);
                var radius = (float)Math.Sqrt(offset.X * offset.X + offset.Z * offset.Z);

                // A small tolerance for the trigonometry, not for a crate escaping the band.
                Assert.InRange(radius, min - 1e-3f, max + 1e-3f);
            }
        }

        [Fact]
        public void The_crates_ring_the_centre_rather_than_clustering()
        {
            // Each crate owns an angular slice, so no two of a volley share an angle — the spread surrounds the
            // target instead of stacking on one bearing.
            const int count = 6;
            var angles = new double[count];
            for (var index = 0; index < count; index++)
            {
                var offset = ShotgunSpread.Offset(7, index, count, 3f, 5f);
                angles[index] = Math.Atan2(offset.Z, offset.X);
            }

            for (var a = 0; a < count; a++)
            {
                for (var b = a + 1; b < count; b++)
                {
                    Assert.True(Separation(angles[a], angles[b]) > 0.2,
                        $"crates {a} and {b} landed on nearly the same bearing");
                }
            }
        }

        [Fact]
        public void A_different_seed_lays_out_a_different_spread()
        {
            var one = ShotgunSpread.Offset(1, 0, 6, 2f, 6f);
            var two = ShotgunSpread.Offset(2, 0, 6, 2f, 6f);
            Assert.False(one.X == two.X && one.Z == two.Z);
        }

        [Fact]
        public void A_degenerate_count_or_index_is_clamped_rather_than_throwing()
        {
            // A caller that asks for a zero-crate or negative-index volley gets a defined point, not an exception.
            var zeroCount = ShotgunSpread.Offset(5, 0, 0, 1f, 3f);
            Assert.InRange((float)Math.Sqrt(zeroCount.X * zeroCount.X + zeroCount.Z * zeroCount.Z), 1f - 1e-3f, 3f + 1e-3f);

            var negativeIndex = ShotgunSpread.Offset(5, -3, 4, 1f, 3f);
            Assert.InRange((float)Math.Sqrt(negativeIndex.X * negativeIndex.X + negativeIndex.Z * negativeIndex.Z), 1f - 1e-3f, 3f + 1e-3f);
        }

        // Smallest angle between two bearings, in radians (0..pi).
        private static double Separation(double a, double b)
        {
            var diff = Math.Abs(a - b) % (2.0 * Math.PI);
            return diff > Math.PI ? 2.0 * Math.PI - diff : diff;
        }
    }
}
