using System;

namespace FalseGods.Protocol.Wire
{
    /// <summary>
    /// A per-stream reliable-event sequence number. The boss stream and the arena stream each have their own,
    /// independent <see cref="Sequence"/> space (Docs/OriginalBossNetworkingArchitecture.md §9.6, ADR-005).
    /// </summary>
    /// <remarks>
    /// Idempotence is by (<c>EncounterId</c>, stream, <see cref="Sequence"/>): a retransmitted event with an
    /// already-applied sequence is discarded, and a gap in one stream is detected independently of the other
    /// (§9.9). Sequences increase by one within a stream; the first event is <see cref="Sequence(long)"/> 0.
    /// </remarks>
    public readonly struct Sequence : IEquatable<Sequence>, IComparable<Sequence>
    {
        private readonly long _value;

        public Sequence(long value)
        {
            _value = value;
        }

        public long Value => _value;

        /// <summary>The next sequence in the stream. Used on the host, where events are minted.</summary>
        public Sequence Next() => new Sequence(_value + 1);

        public int CompareTo(Sequence other) => _value.CompareTo(other._value);

        public bool Equals(Sequence other) => _value == other._value;

        public override bool Equals(object? obj) => obj is Sequence other && Equals(other);

        public override int GetHashCode() => _value.GetHashCode();

        public override string ToString() => $"seq:{_value}";

        public static bool operator ==(Sequence left, Sequence right) => left.Equals(right);

        public static bool operator !=(Sequence left, Sequence right) => !left.Equals(right);

        public static bool operator <(Sequence left, Sequence right) => left.CompareTo(right) < 0;

        public static bool operator >(Sequence left, Sequence right) => left.CompareTo(right) > 0;

        public static bool operator <=(Sequence left, Sequence right) => left.CompareTo(right) <= 0;

        public static bool operator >=(Sequence left, Sequence right) => left.CompareTo(right) >= 0;
    }
}
