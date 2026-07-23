using FalseGods.Core.Bosses.Combat;
using FalseGods.Core.Simulation;
using Xunit;

namespace FalseGods.CoreTests
{
    public sealed class TargetMotionTrackerTests
    {
        [Fact]
        public void A_steady_velocity_is_reported_as_itself()
        {
            var tracker = new TargetMotionTracker(0.4f);
            var velocity = new SimVector2(5f, 0f);

            // Fed the same velocity for a while, the average converges on it — a real sprint survives smoothing.
            for (var step = 0; step < 60; step++)
            {
                tracker.Observe(velocity, 1f / 60f);
            }

            Assert.Equal(5f, tracker.SmoothedVelocity.X, 2);
            Assert.Equal(0f, tracker.SmoothedVelocity.Z, 2);
        }

        [Fact]
        public void A_left_right_jitter_averages_close_to_zero()
        {
            var tracker = new TargetMotionTracker(0.4f);

            // Alternating full-speed left and right, the way a player wiggling in place reads instant to instant.
            for (var step = 0; step < 120; step++)
            {
                var velocity = new SimVector2(step % 2 == 0 ? 5f : -5f, 0f);
                tracker.Observe(velocity, 1f / 60f);
            }

            // The bluff cancels itself: the smoothed velocity is a small fraction of the instantaneous swing.
            Assert.True(System.Math.Abs(tracker.SmoothedVelocity.X) < 1f,
                $"jitter should average near zero but was {tracker.SmoothedVelocity.X}");
        }

        [Fact]
        public void The_first_sample_is_taken_as_is()
        {
            var tracker = new TargetMotionTracker(0.4f);
            tracker.Observe(new SimVector2(3f, -2f), 1f / 60f);
            Assert.Equal(3f, tracker.SmoothedVelocity.X, 4);
            Assert.Equal(-2f, tracker.SmoothedVelocity.Z, 4);
        }

        [Fact]
        public void Reset_forgets_the_history()
        {
            var tracker = new TargetMotionTracker(0.4f);
            tracker.Observe(new SimVector2(5f, 5f), 1f / 60f);
            tracker.Reset();
            Assert.Equal(SimVector2.Zero, tracker.SmoothedVelocity);
        }

        [Fact]
        public void Non_positive_smoothing_passes_the_latest_sample_straight_through()
        {
            var tracker = new TargetMotionTracker(0f);
            tracker.Observe(new SimVector2(1f, 1f), 1f / 60f);
            tracker.Observe(new SimVector2(9f, -4f), 1f / 60f);
            Assert.Equal(9f, tracker.SmoothedVelocity.X, 4);
            Assert.Equal(-4f, tracker.SmoothedVelocity.Z, 4);
        }
    }
}
