using System;
using FalseGods.Protocol.Arena;
using Xunit;

namespace FalseGods.ProtocolTests
{
    /// <summary>
    /// Locks down the value-type invariants the ContentHash relies on: ordinal marker ordering, unassigned-id
    /// detection, and full-length (never truncated) hash comparison.
    /// </summary>
    public sealed class ValueTypeTests
    {
        [Fact]
        public void Marker_ordering_is_ordinal_over_the_canonical_string()
        {
            var a = new StableMarkerId(new Guid("00000000-0000-0000-0000-000000000001"));
            var b = new StableMarkerId(new Guid("00000000-0000-0000-0000-000000000002"));

            Assert.True(a.CompareTo(b) < 0);
            Assert.Equal(string.CompareOrdinal(a.ToCanonicalString(), b.ToCanonicalString()) < 0, a < b);
        }

        [Fact]
        public void Default_marker_is_unassigned()
        {
            Assert.True(default(StableMarkerId).IsUnassigned);
            Assert.False(new StableMarkerId(Guid.NewGuid()).IsUnassigned);
        }

        [Fact]
        public void Content_hash_compares_every_byte()
        {
            var a = new ContentHash(Digest(lastByte: 4));
            var same = new ContentHash(Digest(lastByte: 4));
            var differsInLastByte = new ContentHash(Digest(lastByte: 5));

            Assert.Equal(a, same);
            Assert.NotEqual(a, differsInLastByte); // a truncated compare could miss a difference in the last byte
        }

        [Fact]
        public void Content_hash_rejects_a_non_sha256_length()
        {
            Assert.Throws<ArgumentException>(() => new ContentHash(new byte[4]));
            Assert.Throws<ArgumentException>(() => new ContentHash(new byte[ContentHash.Sha256Length + 1]));
            Assert.Throws<ArgumentNullException>(() => new ContentHash(null!));
        }

        [Fact]
        public void Content_hash_hex_round_trips_the_bytes()
        {
            var bytes = new byte[ContentHash.Sha256Length];
            for (var i = 0; i < bytes.Length; i++)
                bytes[i] = (byte)i;

            Assert.Equal("000102030405060708090a0b0c0d0e0f101112131415161718191a1b1c1d1e1f", new ContentHash(bytes).ToHex());
        }

        [Fact]
        public void Content_hash_defensively_copies_its_bytes()
        {
            var bytes = Digest(lastByte: 7);
            var hash = new ContentHash(bytes);

            bytes[0] = 99; // mutate the caller's array after construction

            Assert.Equal(0, hash.ToArray()[0]);
        }

        private static byte[] Digest(byte lastByte)
        {
            var bytes = new byte[ContentHash.Sha256Length];
            for (var i = 0; i < bytes.Length; i++)
                bytes[i] = (byte)i;

            bytes[bytes.Length - 1] = lastByte;
            return bytes;
        }
    }
}
