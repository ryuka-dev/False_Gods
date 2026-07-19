using FalseGods.Application.Combat;
using Xunit;

namespace FalseGods.ApplicationTests
{
    /// <summary>
    /// The float-to-simulation damage conversion: positive hits never vanish in rounding, halves round away
    /// from zero, and untrusted inputs (non-positive, NaN, infinity) map to the ignored value 0.
    /// </summary>
    public sealed class WeaponDamageTests
    {
        [Theory]
        [InlineData(15f, 15)]
        [InlineData(15.4f, 15)]
        [InlineData(15.5f, 16)]
        [InlineData(0.3f, 1)] // a positive hit never rounds away to nothing
        [InlineData(1f, 1)]
        public void PositiveAmountsRoundToWholePoints(float amount, int expected) =>
            Assert.Equal(expected, WeaponDamage.ToSimAmount(amount));

        [Theory]
        [InlineData(0f)]
        [InlineData(-15f)]
        [InlineData(float.NaN)]
        [InlineData(float.NegativeInfinity)]
        public void NonPositiveOrInvalidAmountsAreIgnored(float amount) =>
            Assert.Equal(0, WeaponDamage.ToSimAmount(amount));

        [Fact]
        public void AbsurdlyLargeAmountsClampInsteadOfOverflowing()
        {
            Assert.Equal(int.MaxValue, WeaponDamage.ToSimAmount(3e9f));
            Assert.Equal(0, WeaponDamage.ToSimAmount(float.PositiveInfinity));
        }
    }
}
