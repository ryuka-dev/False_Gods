using System;

namespace FalseGods.RuntimeContracts.Arena
{
    /// <summary>A rotation quaternion crossing the arena realization seam (see <see cref="ArenaWorldPoint"/> for
    /// why this is its own vocabulary and not Unity's or Protocol's).</summary>
    public readonly struct ArenaRotation : IEquatable<ArenaRotation>
    {
        public ArenaRotation(float x, float y, float z, float w)
        {
            X = x;
            Y = y;
            Z = z;
            W = w;
        }

        /// <summary>The identity rotation.</summary>
        public static ArenaRotation Identity => new ArenaRotation(0f, 0f, 0f, 1f);

        public float X { get; }

        public float Y { get; }

        public float Z { get; }

        public float W { get; }

        public bool Equals(ArenaRotation other) =>
            X.Equals(other.X) && Y.Equals(other.Y) && Z.Equals(other.Z) && W.Equals(other.W);

        public override bool Equals(object? obj) => obj is ArenaRotation other && Equals(other);

        public override int GetHashCode()
        {
            unchecked
            {
                var hash = X.GetHashCode();
                hash = (hash * 397) ^ Y.GetHashCode();
                hash = (hash * 397) ^ Z.GetHashCode();
                hash = (hash * 397) ^ W.GetHashCode();
                return hash;
            }
        }

        public override string ToString() => $"({X:0.###}, {Y:0.###}, {Z:0.###}, {W:0.###})";

        public static bool operator ==(ArenaRotation left, ArenaRotation right) => left.Equals(right);

        public static bool operator !=(ArenaRotation left, ArenaRotation right) => !left.Equals(right);
    }
}
