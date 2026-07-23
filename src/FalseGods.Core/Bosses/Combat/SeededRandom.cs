namespace FalseGods.Core.Bosses.Combat
{
    /// <summary>
    /// Deterministic values drawn from a seed and a salt, so a choice made once by a host can be recomputed
    /// identically by every peer instead of being sent.
    /// </summary>
    /// <remarks>This is the same bargain <see cref="BallisticArc"/> and <see cref="ShotgunSpread"/> strike: a
    /// volley's whole shape — its scatter, and how long it hangs before firing — follows from one seed, so the
    /// message that starts it need carry no more than that. It is a hash, not a sequence: there is no state to
    /// advance and no order to keep, so the same (seed, salt) always gives the same value wherever it is asked.</remarks>
    public static class SeededRandom
    {
        /// <summary>A reproducible value in [0, 1) from a <paramref name="seed"/> and a <paramref name="salt"/>
        /// that distinguishes independent draws from the same seed.</summary>
        public static double Unit01(int seed, int salt)
        {
            var h = unchecked((uint)seed * 2654435761u + (uint)salt * 2246822519u + 374761393u);
            h ^= h >> 15;
            h = unchecked(h * 2246822519u);
            h ^= h >> 13;
            h = unchecked(h * 3266489917u);
            h ^= h >> 16;
            return h / 4294967296.0; // 2^32 — maps the full uint range onto [0, 1)
        }

        /// <summary>A reproducible value between <paramref name="min"/> and <paramref name="max"/>, drawn the same
        /// way as <see cref="Unit01"/>.</summary>
        public static float Range(int seed, int salt, float min, float max)
        {
            return min + (float)Unit01(seed, salt) * (max - min);
        }
    }
}
