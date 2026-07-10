using System;

namespace FalseGods.Protocol.Arena
{
    /// <summary>
    /// A project-owned three-component vector for authored transform data.
    /// </summary>
    /// <remarks>
    /// <c>FalseGods.Protocol</c> never references <c>UnityEngine</c> (Docs/DependencyRules.md §5), so authored
    /// positions/scales/sizes cross the boundary as this value type. The raw float components are authored
    /// inputs only; they never enter the content hash directly — the hash quantises them to integers first
    /// (Docs/MultiplayerLoadingContract.md §5.2.1).
    /// </remarks>
    public readonly struct Vector3 : IEquatable<Vector3>
    {
        public Vector3(float x, float y, float z)
        {
            X = x;
            Y = y;
            Z = z;
        }

        public float X { get; }

        public float Y { get; }

        public float Z { get; }

        public bool Equals(Vector3 other) =>
            X.Equals(other.X) && Y.Equals(other.Y) && Z.Equals(other.Z);

        public override bool Equals(object? obj) => obj is Vector3 other && Equals(other);

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

        public override string ToString() => $"({X}, {Y}, {Z})";

        public static bool operator ==(Vector3 left, Vector3 right) => left.Equals(right);

        public static bool operator !=(Vector3 left, Vector3 right) => !left.Equals(right);
    }
}
