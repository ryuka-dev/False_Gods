using System;

namespace FalseGods.Protocol.Arena
{
    /// <summary>
    /// The identity of a single authored node that participates in arena content identity: a GUID assigned in
    /// the Unity editor and serialised into the arena prefab.
    /// </summary>
    /// <remarks>
    /// It is deliberately <b>not</b> a scene-object name, a hierarchy path, or <c>GetInstanceID()</c>
    /// (Docs/DependencyRules.md §4). The content hash orders every list by this id and encodes it as its
    /// canonical string, compared as raw UTF-8 bytes (Docs/MultiplayerLoadingContract.md §5.2.1). A GUID's
    /// canonical "D" form is pure ASCII, so ordinal string ordering coincides exactly with UTF-8 byte ordering.
    /// </remarks>
    public readonly struct StableMarkerId : IEquatable<StableMarkerId>, IComparable<StableMarkerId>
    {
        private readonly Guid _value;

        public StableMarkerId(Guid value)
        {
            _value = value;
        }

        /// <summary>
        /// True when this id was never assigned (the default/empty GUID). Hashing treats an unassigned id on
        /// any authored node as a build-time export failure, never as a runtime hash
        /// (Docs/MultiplayerLoadingContract.md §5.2.1, Docs/OriginalContentPipeline.md §8.3).
        /// </summary>
        public bool IsUnassigned => _value == Guid.Empty;

        /// <summary>
        /// The canonical textual form fed to the content hash: the 36-character lowercase "D" format
        /// (<c>xxxxxxxx-xxxx-xxxx-xxxx-xxxxxxxxxxxx</c>). Pure ASCII, so ordinal comparison of two of these
        /// equals raw-UTF-8-byte comparison — the ordering the hash specification mandates.
        /// </summary>
        public string ToCanonicalString() => _value.ToString("D");

        public int CompareTo(StableMarkerId other) =>
            string.CompareOrdinal(ToCanonicalString(), other.ToCanonicalString());

        public bool Equals(StableMarkerId other) => _value.Equals(other._value);

        public override bool Equals(object? obj) => obj is StableMarkerId other && Equals(other);

        public override int GetHashCode() => _value.GetHashCode();

        public override string ToString() => ToCanonicalString();

        public static bool operator ==(StableMarkerId left, StableMarkerId right) => left.Equals(right);

        public static bool operator !=(StableMarkerId left, StableMarkerId right) => !left.Equals(right);

        public static bool operator <(StableMarkerId left, StableMarkerId right) => left.CompareTo(right) < 0;

        public static bool operator >(StableMarkerId left, StableMarkerId right) => left.CompareTo(right) > 0;

        public static bool operator <=(StableMarkerId left, StableMarkerId right) => left.CompareTo(right) <= 0;

        public static bool operator >=(StableMarkerId left, StableMarkerId right) => left.CompareTo(right) >= 0;
    }
}
