using System;

namespace FalseGods.Application.Combat
{
    /// <summary>
    /// Converts the game's float weapon damage into the simulation's integer damage vocabulary — the one place
    /// the two damage number systems meet.
    /// </summary>
    /// <remarks>
    /// The game computes per-hit damage as a float; <c>BossSimulation.ApplyDamage</c> takes whole points. A
    /// positive hit never rounds away to nothing (minimum 1), halves round away from zero so 15.5 is 16, and a
    /// non-finite or non-positive input — the game's value is treated as untrusted (Docs/DependencyRules.md §12)
    /// — maps to 0, which the simulation ignores.
    /// </remarks>
    public static class WeaponDamage
    {
        /// <summary>Map a game damage amount to simulation damage points; 0 means "ignore this hit".</summary>
        public static int ToSimAmount(float amount)
        {
            if (float.IsNaN(amount) || float.IsInfinity(amount) || amount <= 0f)
            {
                return 0;
            }

            if (amount >= int.MaxValue)
            {
                return int.MaxValue;
            }

            return Math.Max(1, (int)Math.Round(amount, MidpointRounding.AwayFromZero));
        }
    }
}
