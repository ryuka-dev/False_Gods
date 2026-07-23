namespace FalseGods.Core.Bosses.Combat
{
    /// <summary>
    /// The shape of a thrown object's flight: a parabola travelled at a constant horizontal rate.
    /// </summary>
    /// <remarks>
    /// <para><b>Why this is a pure shape and not physics.</b> A thrown crate handed to a physics engine tumbles,
    /// collides, and comes to rest differently on every machine, which leaves a host no choice but to stream every
    /// crate's position to every peer for as long as it flies. A crate whose whole path follows from
    /// (start, target, duration, apex) needs none of that: each peer computes the identical flight from the same
    /// four numbers, so a volley costs one message to start and one to end, whatever happens in between. That is
    /// the entire reason this is arithmetic in the simulation rather than a rigidbody in the scene.</para>
    /// <para><b>Why it carries no vector type.</b> Only the <i>shape</i> is authoritative — where the endpoints
    /// are is the caller's business, and keeping this to two scalars means the simulation never grows a
    /// three-dimensional vector it would otherwise only use here.</para>
    /// </remarks>
    public static class BallisticArc
    {
        /// <summary>
        /// How far along the ground the throw has travelled, as a fraction of the whole distance. Constant rate:
        /// the horizontal speed of a thrown object does not change in flight.
        /// </summary>
        /// <param name="normalizedTime">Progress through the flight, 0 at the throw and 1 at the landing.</param>
        public static float HorizontalFraction(float normalizedTime) => Clamp01(normalizedTime);

        /// <summary>
        /// How high above the straight line from start to target the throw sits, peaking at
        /// <paramref name="apexHeight"/> halfway. Zero at both ends, so the crate leaves the boss's hands and
        /// reaches its target at exactly the heights the caller chose.
        /// </summary>
        /// <param name="normalizedTime">Progress through the flight, 0 at the throw and 1 at the landing.</param>
        /// <param name="apexHeight">Height above the line at the midpoint. Zero throws flat.</param>
        public static float Height(float normalizedTime, float apexHeight)
        {
            var t = Clamp01(normalizedTime);

            // 4 * t * (1 - t) is the unit parabola: 0 at both ends, exactly 1 at the midpoint.
            return 4f * apexHeight * t * (1f - t);
        }

        private static float Clamp01(float value)
        {
            if (value < 0f)
            {
                return 0f;
            }

            return value > 1f ? 1f : value;
        }
    }
}
