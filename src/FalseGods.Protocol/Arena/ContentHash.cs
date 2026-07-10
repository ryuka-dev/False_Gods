using System;

namespace FalseGods.Protocol.Arena
{
    /// <summary>
    /// A canonical arena content hash: the full SHA-256 over the canonical encoding of an
    /// <see cref="ArenaContentDefinition"/> (Docs/MultiplayerLoadingContract.md §5.2.1).
    /// </summary>
    /// <remarks>
    /// Compared in full and never truncated — <see cref="Equals(ContentHash)"/> compares every byte. Two peers
    /// that realised the same arena content produce byte-identical hashes; a mismatch under a matching schema is
    /// a hard, fail-closed refusal (§5.3.1), not a race to paper over. The stored bytes are copied in and out so
    /// the value is genuinely immutable.
    /// </remarks>
    public readonly struct ContentHash : IEquatable<ContentHash>
    {
        /// <summary>A SHA-256 digest is exactly 32 bytes; anything else is not a well-formed content hash.</summary>
        public const int Sha256Length = 32;

        private readonly byte[]? _bytes;

        /// <summary>
        /// Wraps a full SHA-256 digest (defensively copied). Rejects any length other than
        /// <see cref="Sha256Length"/>, so a malformed or truncated digest can never masquerade as a
        /// <see cref="ContentHash"/>.
        /// </summary>
        public ContentHash(byte[] bytes)
        {
            if (bytes is null)
                throw new ArgumentNullException(nameof(bytes));

            if (bytes.Length != Sha256Length)
            {
                throw new ArgumentException(
                    $"A ContentHash is a full SHA-256 digest of {Sha256Length} bytes, but {bytes.Length} were " +
                    "given. The hash is never truncated (MultiplayerLoadingContract §5.2.1).",
                    nameof(bytes));
            }

            _bytes = (byte[])bytes.Clone();
        }

        /// <summary>The digest length in bytes (32 for a constructed value; 0 for the default/unset value).</summary>
        public int Length => _bytes?.Length ?? 0;

        /// <summary>A copy of the raw digest bytes.</summary>
        public byte[] ToArray() => (byte[])(_bytes ?? Array.Empty<byte>()).Clone();

        /// <summary>The digest as a lowercase, unseparated hex string.</summary>
        public string ToHex()
        {
            var bytes = _bytes ?? Array.Empty<byte>();
            var chars = new char[bytes.Length * 2];
            for (var i = 0; i < bytes.Length; i++)
            {
                var b = bytes[i];
                chars[i * 2] = ToHexChar(b >> 4);
                chars[(i * 2) + 1] = ToHexChar(b & 0xF);
            }

            return new string(chars);
        }

        public bool Equals(ContentHash other)
        {
            var left = _bytes ?? Array.Empty<byte>();
            var right = other._bytes ?? Array.Empty<byte>();

            if (left.Length != right.Length)
                return false;

            for (var i = 0; i < left.Length; i++)
            {
                if (left[i] != right[i])
                    return false;
            }

            return true;
        }

        public override bool Equals(object? obj) => obj is ContentHash other && Equals(other);

        public override int GetHashCode()
        {
            // A digest is already uniformly distributed; fold a few leading bytes into an int. Never used for
            // the authoritative comparison — Equals compares every byte.
            var bytes = _bytes;
            if (bytes is null || bytes.Length == 0)
                return 0;

            unchecked
            {
                var hash = 17;
                var count = Math.Min(bytes.Length, 4);
                for (var i = 0; i < count; i++)
                    hash = (hash * 31) + bytes[i];

                return hash;
            }
        }

        public override string ToString() => ToHex();

        public static bool operator ==(ContentHash left, ContentHash right) => left.Equals(right);

        public static bool operator !=(ContentHash left, ContentHash right) => !left.Equals(right);

        private static char ToHexChar(int nibble) =>
            (char)(nibble < 10 ? '0' + nibble : 'a' + (nibble - 10));
    }
}
