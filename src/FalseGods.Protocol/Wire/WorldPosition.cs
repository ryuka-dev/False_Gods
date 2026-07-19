using System;

namespace FalseGods.Protocol.Wire
{
    /// <summary>
    /// A world-space point carried on the wire, in metres — used by <see cref="EnterArena"/> for the
    /// host-chosen arena origin.
    /// </summary>
    /// <remarks>
    /// A third, deliberately separate position vocabulary (Docs/DependencyRules.md §4): Core's
    /// <c>SimVector2</c> is a live planar simulation coordinate, <c>FalseGods.Protocol.Arena.Vector3</c> is an
    /// authored, hash-quantised input, and this is a runtime placement that rides the replication wire. Like
    /// every wire float it is raw IEEE-754 — round-trip fidelity, never cross-machine bit-identity (ADR-003).
    /// </remarks>
    public readonly struct WorldPosition : IEquatable<WorldPosition>
    {
        public WorldPosition(float x, float y, float z)
        {
            X = x;
            Y = y;
            Z = z;
        }

        public float X { get; }

        public float Y { get; }

        public float Z { get; }

        public bool Equals(WorldPosition other) => X.Equals(other.X) && Y.Equals(other.Y) && Z.Equals(other.Z);

        public override bool Equals(object? obj) => obj is WorldPosition other && Equals(other);

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

        public override string ToString() => $"({X:0.##}, {Y:0.##}, {Z:0.##})";

        public static bool operator ==(WorldPosition left, WorldPosition right) => left.Equals(right);

        public static bool operator !=(WorldPosition left, WorldPosition right) => !left.Equals(right);
    }
}
