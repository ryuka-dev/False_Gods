using FalseGods.Core.Bosses.Combat;
using Xunit;

namespace FalseGods.CoreTests
{
    public sealed class SeededRandomTests
    {
        [Fact]
        public void The_same_seed_and_salt_always_give_the_same_value()
        {
            Assert.Equal(SeededRandom.Unit01(42, 7), SeededRandom.Unit01(42, 7));
            Assert.Equal(SeededRandom.Range(42, 7, 0.5f, 1.5f), SeededRandom.Range(42, 7, 0.5f, 1.5f));
        }

        [Fact]
        public void Unit01_stays_within_the_unit_interval()
        {
            for (var seed = 0; seed < 50; seed++)
            {
                var value = SeededRandom.Unit01(seed, seed * 3 + 1);
                Assert.InRange(value, 0.0, 1.0);
            }
        }

        [Fact]
        public void Range_stays_within_its_bounds()
        {
            for (var seed = 0; seed < 50; seed++)
            {
                var value = SeededRandom.Range(seed, 9973, 0.5f, 1.5f);
                Assert.InRange(value, 0.5f, 1.5f);
            }
        }

        [Fact]
        public void Different_salts_draw_independent_values()
        {
            // Two draws from one seed must not be locked together, or the hold would track the scatter.
            var a = SeededRandom.Unit01(1234, 0);
            var b = SeededRandom.Unit01(1234, 9973);
            Assert.NotEqual(a, b);
        }
    }
}
