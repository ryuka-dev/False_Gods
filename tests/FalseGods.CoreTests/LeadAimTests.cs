using FalseGods.Core.Bosses.Combat;
using FalseGods.Core.Simulation;
using Xunit;

namespace FalseGods.CoreTests
{
    public sealed class LeadAimTests
    {
        [Fact]
        public void A_still_target_is_aimed_at_where_it_stands()
        {
            var here = new SimVector2(3f, -4f);
            var aim = LeadAim.Predict(here, SimVector2.Zero, 2f);
            Assert.Equal(here.X, aim.X);
            Assert.Equal(here.Z, aim.Z);
        }

        [Fact]
        public void A_moving_target_is_led_by_velocity_times_time()
        {
            var position = new SimVector2(0f, 0f);
            var velocity = new SimVector2(2f, -1f);
            var aim = LeadAim.Predict(position, velocity, 3f);

            // Carried three seconds forward: 2*3 sideways, -1*3 forward.
            Assert.Equal(6f, aim.X, 4);
            Assert.Equal(-3f, aim.Z, 4);
        }

        [Fact]
        public void Zero_time_gives_the_position_back_however_fast_the_target_moves()
        {
            var position = new SimVector2(5f, 5f);
            var aim = LeadAim.Predict(position, new SimVector2(100f, 100f), 0f);
            Assert.Equal(position.X, aim.X);
            Assert.Equal(position.Z, aim.Z);
        }
    }
}
