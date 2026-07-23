using System;

namespace FalseGods.Core.Bosses.Combat
{
    /// <summary>Where a single crate in a volley lands relative to the volley's centre, on the ground plane.</summary>
    /// <remarks>Two scalars, no vector type, for the same reason <see cref="BallisticArc"/> carries none: the
    /// simulation owns the <i>pattern</i>, not the world position the caller adds it to.</remarks>
    public readonly struct SpreadOffset
    {
        public SpreadOffset(float x, float z)
        {
            X = x;
            Z = z;
        }

        /// <summary>Sideways offset from the centre, along the world X axis.</summary>
        public float X { get; }

        /// <summary>Forward/back offset from the centre, along the world Z axis.</summary>
        public float Z { get; }
    }

    /// <summary>
    /// The scatter pattern of a shotgun volley: several crates spread around a centre point rather than piled onto
    /// it, laid out deterministically from a single seed.
    /// </summary>
    /// <remarks>
    /// <para><b>Why a seed and not a random object.</b> A volley thrown in a multiplayer fight must land the same
    /// way on every machine, or one peer shoots a crate out of the air that another peer never saw there. Deriving
    /// the whole pattern from (seed, index, count) means the host sends one seed and every peer computes the
    /// identical scatter — the same bargain <see cref="BallisticArc"/> strikes for the flight itself, so a volley
    /// costs one message however many crates it throws.</para>
    /// <para><b>Why it surrounds the target.</b> Each crate owns one angular slice of the circle, jittered only
    /// within its own slice, so the crates ring the player instead of clustering — a spread that cannot be dodged
    /// by a single sidestep, and reads as a shotgun rather than a stream.</para>
    /// </remarks>
    public static class ShotgunSpread
    {
        /// <summary>
        /// The ground offset for crate <paramref name="index"/> of a <paramref name="count"/>-crate volley seeded
        /// by <paramref name="seed"/>, landing between <paramref name="minRadius"/> and
        /// <paramref name="maxRadius"/> from the centre. Deterministic: the same arguments always give the same
        /// point.
        /// </summary>
        public static SpreadOffset Offset(int seed, int index, int count, float minRadius, float maxRadius)
        {
            if (count < 1)
            {
                count = 1;
            }

            if (index < 0)
            {
                index = 0;
            }

            // One angular slice per crate, so the pattern rings the centre. The whole ring is rotated by the seed,
            // and each crate is jittered only within the middle half of its own slice, so adjacent crates keep a
            // clear gap between their bearings and never trade places or overlap.
            var slice = 2.0 * Math.PI / count;
            var phase = Unit01(seed, 0) * 2.0 * Math.PI;
            var jitter = (Unit01(seed, index * 2 + 1) - 0.5) * (slice * 0.5);
            var angle = phase + slice * index + jitter;

            var radius = Lerp(minRadius, maxRadius, (float)Unit01(seed, index * 2 + 2));
            return new SpreadOffset((float)(Math.Cos(angle) * radius), (float)(Math.Sin(angle) * radius));
        }

        /// <summary>
        /// A deterministic value in [0, 1) from two integers — a small integer hash finalizer, so the pattern is
        /// reproducible without carrying a random-number generator through the call.
        /// </summary>
        private static double Unit01(int seed, int salt)
        {
            var h = unchecked((uint)seed * 2654435761u + (uint)salt * 2246822519u + 374761393u);
            h ^= h >> 15;
            h = unchecked(h * 2246822519u);
            h ^= h >> 13;
            h = unchecked(h * 3266489917u);
            h ^= h >> 16;
            return h / 4294967296.0; // 2^32 — maps the full uint range onto [0, 1)
        }

        private static float Lerp(float a, float b, float t) => a + (b - a) * t;
    }
}
