using System;

namespace FalseGods.Protocol.Arena
{
    /// <summary>
    /// Identifies which definition produced a <see cref="ContentHash"/>: the exact input set, ordering rule,
    /// quantisation rule, and algorithm (Docs/MultiplayerLoadingContract.md §5.2.1).
    /// </summary>
    /// <remarks>
    /// Peers compare the pair <c>(ContentHashSchemaVersion, ContentHash)</c>. A schema mismatch is refused
    /// <em>without comparing the hashes at all</em> — hashes from different schemas are not comparable, and a
    /// clean refusal beats an accidental collision (§5.2.1, §5.3.1). Because of that, changing anything the
    /// hash is computed from is a protocol-compatibility change, not a refactor: bump <see cref="Current"/>.
    /// </remarks>
    public readonly struct ContentHashSchemaVersion : IEquatable<ContentHashSchemaVersion>
    {
        /// <summary>
        /// The schema this build of <c>FalseGods.Protocol</c> produces and understands.
        /// </summary>
        /// <remarks>
        /// Version 1 hashed, in the fixed order of §5.2.1: schema version; arena id + version; arena content
        /// id; then authored nodes, vanilla proxies, colliders, navigation, spawns, and mechanisms — each list
        /// ordered by <see cref="StableMarkerId"/> and float-quantised.
        /// <para>Version 2 appends input 10, the material borrows (vanilla materials reused onto our own
        /// renderers), each ordered by <see cref="StableMarkerId"/> and encoding carrier GUID + material name +
        /// target renderer/sub-material — the stable asset identity, never the loaded material.</para>
        /// Add, remove, or reorder any of that and this constant must change.
        /// </remarks>
        public static readonly ContentHashSchemaVersion Current = new ContentHashSchemaVersion(2);

        public ContentHashSchemaVersion(int value)
        {
            Value = value;
        }

        public int Value { get; }

        public bool Equals(ContentHashSchemaVersion other) => Value == other.Value;

        public override bool Equals(object? obj) => obj is ContentHashSchemaVersion other && Equals(other);

        public override int GetHashCode() => Value;

        public override string ToString() => Value.ToString();

        public static bool operator ==(ContentHashSchemaVersion left, ContentHashSchemaVersion right) => left.Equals(right);

        public static bool operator !=(ContentHashSchemaVersion left, ContentHashSchemaVersion right) => !left.Equals(right);
    }
}
