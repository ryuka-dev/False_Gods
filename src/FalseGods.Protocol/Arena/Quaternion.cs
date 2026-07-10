using System;

namespace FalseGods.Protocol.Arena
{
    /// <summary>
    /// A project-owned rotation quaternion for authored transform data.
    /// </summary>
    /// <remarks>
    /// Kept out of <c>UnityEngine</c> for the same reason as <see cref="Vector3"/>. The content hash does not
    /// encode these components as-authored: it normalises the quaternion, canonicalises the sign so that
    /// <c>q</c> and <c>-q</c> (the same rotation) collapse to one representation, then quantises
    /// (Docs/MultiplayerLoadingContract.md §5.2.1).
    /// </remarks>
    public readonly struct Quaternion : IEquatable<Quaternion>
    {
        public Quaternion(float x, float y, float z, float w)
        {
            X = x;
            Y = y;
            Z = z;
            W = w;
        }

        /// <summary>The identity rotation.</summary>
        public static Quaternion Identity => new Quaternion(0f, 0f, 0f, 1f);

        public float X { get; }

        public float Y { get; }

        public float Z { get; }

        public float W { get; }

        public bool Equals(Quaternion other) =>
            X.Equals(other.X) && Y.Equals(other.Y) && Z.Equals(other.Z) && W.Equals(other.W);

        public override bool Equals(object? obj) => obj is Quaternion other && Equals(other);

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

        public override string ToString() => $"({X}, {Y}, {Z}, {W})";

        public static bool operator ==(Quaternion left, Quaternion right) => left.Equals(right);

        public static bool operator !=(Quaternion left, Quaternion right) => !left.Equals(right);
    }
}
