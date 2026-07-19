using System;

namespace FalseGods.RuntimeContracts.Arena
{
    /// <summary>
    /// A world-space point (or component-wise triple, for scales), in metres, crossing the arena realization
    /// seam.
    /// </summary>
    /// <remarks>
    /// Deliberately its own vocabulary (Docs/DependencyRules.md §4): Core's <c>SimVector2</c> is a planar
    /// simulation coordinate, Protocol's authored <c>Vector3</c> is a hash input, Protocol's wire
    /// <c>WorldPosition</c> is a replication payload — and this is what the realization ports exchange with
    /// their Unity-side implementations, which cannot see <c>FalseGods.Protocol</c> (FG-ARCH-007 / the
    /// UnityRuntime reference list).
    /// </remarks>
    public readonly struct ArenaWorldPoint : IEquatable<ArenaWorldPoint>
    {
        public ArenaWorldPoint(float x, float y, float z)
        {
            X = x;
            Y = y;
            Z = z;
        }

        public float X { get; }

        public float Y { get; }

        public float Z { get; }

        public bool Equals(ArenaWorldPoint other) => X.Equals(other.X) && Y.Equals(other.Y) && Z.Equals(other.Z);

        public override bool Equals(object? obj) => obj is ArenaWorldPoint other && Equals(other);

        public override int GetHashCode()
        {
            unchecked
            {
                var hash = X.GetHashCode();
                hash = (hash * 397) ^ Y.GetHashCode();
                hash = (hash * 397) ^ Z.GetHashCode();
                return hash;
            }
        }

        public override string ToString() => $"({X:0.###}, {Y:0.###}, {Z:0.###})";

        public static bool operator ==(ArenaWorldPoint left, ArenaWorldPoint right) => left.Equals(right);

        public static bool operator !=(ArenaWorldPoint left, ArenaWorldPoint right) => !left.Equals(right);
    }
}
