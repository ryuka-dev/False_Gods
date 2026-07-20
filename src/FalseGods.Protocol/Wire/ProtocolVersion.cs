using System;

namespace FalseGods.Protocol.Wire
{
    /// <summary>
    /// The version of the False Gods replication wire contract — the boss/arena snapshots, event streams, and
    /// baseline defined in this namespace.
    /// </summary>
    /// <remarks>
    /// A mismatch is an explicit refusal, never silent divergence
    /// (Docs/OriginalBossNetworkingArchitecture.md §9.4, Docs/MultiplayerLoadingContract.md §5.3.1). It is
    /// independent of the arena <c>ContentHashSchemaVersion</c> (which versions the content hash, not the
    /// replication protocol). Bump <see cref="Current"/> on any change to a wire DTO's fields or their encoding.
    /// </remarks>
    public readonly struct ProtocolVersion : IEquatable<ProtocolVersion>
    {
        /// <summary>The wire contract this build of <c>FalseGods.Protocol</c> produces and understands.</summary>
        public static readonly ProtocolVersion Current = new ProtocolVersion(5);

        public ProtocolVersion(int value)
        {
            Value = value;
        }

        public int Value { get; }

        public bool Equals(ProtocolVersion other) => Value == other.Value;

        public override bool Equals(object? obj) => obj is ProtocolVersion other && Equals(other);

        public override int GetHashCode() => Value;

        public override string ToString() => Value.ToString();

        public static bool operator ==(ProtocolVersion left, ProtocolVersion right) => left.Equals(right);

        public static bool operator !=(ProtocolVersion left, ProtocolVersion right) => !left.Equals(right);
    }
}
