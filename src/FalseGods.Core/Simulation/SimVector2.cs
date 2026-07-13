using System;

namespace FalseGods.Core.Simulation
{
    /// <summary>
    /// A project-owned planar position/direction on the arena floor, in world metres.
    /// </summary>
    /// <remarks>
    /// <c>FalseGods.Core</c> references no <c>UnityEngine</c> (Docs/DependencyRules.md §2), so the boss domain
    /// reasons about positions with this value type rather than <c>UnityEngine.Vector3</c>. The arena floor of the
    /// PoC is flat (Docs/MinimalProofOfConceptPlan.md §7.1), so boss movement and aiming need only the horizontal
    /// plane; <see cref="X"/> and <see cref="Z"/> are the world X and Z axes.
    ///
    /// This is a <b>domain</b> value type. It is deliberately distinct from <c>FalseGods.Protocol</c>'s wire
    /// <c>Vector3</c>: the two carry different concepts (a live simulation coordinate versus an authored,
    /// hash-quantised input) and may change independently (Docs/DependencyRules.md §4).
    /// </remarks>
    public readonly struct SimVector2 : IEquatable<SimVector2>
    {
        public SimVector2(float x, float z)
        {
            X = x;
            Z = z;
        }

        public float X { get; }

        public float Z { get; }

        public static SimVector2 Zero => new SimVector2(0f, 0f);

        /// <summary>The straight-line distance to <paramref name="other"/> in metres.</summary>
        public float DistanceTo(SimVector2 other)
        {
            var dx = other.X - X;
            var dz = other.Z - Z;
            return (float)Math.Sqrt((dx * dx) + (dz * dz));
        }

        /// <summary>
        /// A step of at most <paramref name="maxDistance"/> metres from this point toward <paramref name="target"/>,
        /// stopping exactly at the target when it is within reach. Returns this point unchanged when
        /// <paramref name="maxDistance"/> is not positive or the target coincides with this point.
        /// </summary>
        public SimVector2 MoveToward(SimVector2 target, float maxDistance)
        {
            if (maxDistance <= 0f)
            {
                return this;
            }

            var dx = target.X - X;
            var dz = target.Z - Z;
            var distance = (float)Math.Sqrt((dx * dx) + (dz * dz));
            if (distance <= maxDistance || distance <= 0f)
            {
                return target;
            }

            var scale = maxDistance / distance;
            return new SimVector2(X + (dx * scale), Z + (dz * scale));
        }

        /// <summary>
        /// The unit direction from this point to <paramref name="target"/>, or <see cref="Zero"/> when the two
        /// coincide. The boss aims its projectile along this direction (host-authoritative).
        /// </summary>
        public SimVector2 DirectionTo(SimVector2 target)
        {
            var dx = target.X - X;
            var dz = target.Z - Z;
            var distance = (float)Math.Sqrt((dx * dx) + (dz * dz));
            if (distance <= 0f)
            {
                return Zero;
            }

            return new SimVector2(dx / distance, dz / distance);
        }

        public bool Equals(SimVector2 other) => X.Equals(other.X) && Z.Equals(other.Z);

        public override bool Equals(object? obj) => obj is SimVector2 other && Equals(other);

        public override int GetHashCode()
        {
            unchecked
            {
                return (X.GetHashCode() * 397) ^ Z.GetHashCode();
            }
        }

        public override string ToString() => $"({X}, {Z})";

        public static bool operator ==(SimVector2 left, SimVector2 right) => left.Equals(right);

        public static bool operator !=(SimVector2 left, SimVector2 right) => !left.Equals(right);
    }
}
