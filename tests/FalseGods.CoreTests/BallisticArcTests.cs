using FalseGods.Core.Bosses.Combat;
using Xunit;

namespace FalseGods.CoreTests
{
    public sealed class BallisticArcTests
    {
        [Fact]
        public void The_throw_starts_at_the_thrower_and_ends_on_the_target()
        {
            Assert.Equal(0f, BallisticArc.HorizontalFraction(0f));
            Assert.Equal(1f, BallisticArc.HorizontalFraction(1f));

            // Both ends sit exactly on the line between them, so the caller's endpoints are the real endpoints.
            Assert.Equal(0f, BallisticArc.Height(0f, 10f));
            Assert.Equal(0f, BallisticArc.Height(1f, 10f));
        }

        [Fact]
        public void The_arc_peaks_at_the_requested_height_halfway_through()
        {
            Assert.Equal(5f, BallisticArc.Height(0.5f, 5f), 4);
            Assert.Equal(0.5f, BallisticArc.HorizontalFraction(0.5f));
        }

        [Fact]
        public void The_arc_is_symmetric_about_its_midpoint()
        {
            Assert.Equal(BallisticArc.Height(0.25f, 8f), BallisticArc.Height(0.75f, 8f), 4);
            Assert.Equal(BallisticArc.Height(0.1f, 8f), BallisticArc.Height(0.9f, 8f), 4);
        }

        [Fact]
        public void A_flat_throw_never_leaves_the_line()
        {
            Assert.Equal(0f, BallisticArc.Height(0.5f, 0f));
            Assert.Equal(0f, BallisticArc.Height(0.25f, 0f));
        }

        [Fact]
        public void Progress_outside_the_flight_is_clamped_rather_than_extrapolated()
        {
            // A late tick must not fling the crate past its target or below the floor.
            Assert.Equal(1f, BallisticArc.HorizontalFraction(1.5f));
            Assert.Equal(0f, BallisticArc.HorizontalFraction(-0.5f));
            Assert.Equal(0f, BallisticArc.Height(1.5f, 10f));
            Assert.Equal(0f, BallisticArc.Height(-0.5f, 10f));
        }

        [Fact]
        public void The_same_progress_always_gives_the_same_point()
        {
            // The whole reason the flight is arithmetic: every peer computing this gets identical answers.
            for (var step = 0; step <= 10; step++)
            {
                var t = step / 10f;
                Assert.Equal(BallisticArc.Height(t, 6f), BallisticArc.Height(t, 6f));
                Assert.Equal(BallisticArc.HorizontalFraction(t), BallisticArc.HorizontalFraction(t));
            }
        }
    }
}
