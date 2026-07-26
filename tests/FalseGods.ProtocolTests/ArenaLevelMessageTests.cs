using System;
using System.IO;
using FalseGods.Protocol.Wire;
using Xunit;

namespace FalseGods.ProtocolTests
{
    /// <summary>
    /// Round-trip and untrusted-input tests for the session's boss-arena declaration: the host says which level
    /// is a boss arena, and a peer asks the host to take everyone there.
    /// </summary>
    public sealed class ArenaLevelMessageTests
    {
        [Fact]
        public void Declaration_round_trips()
        {
            var original = new ArenaLevelDeclared(new ArenaLevelId(3, 0), true);

            var decoded = WireCodec.DeserializeArenaLevelDeclared(WireCodec.Serialize(original));

            Assert.Equal(original, decoded);
        }

        [Fact]
        public void Withdrawal_round_trips_distinctly_from_a_declaration()
        {
            var level = new ArenaLevelId(3, 0);

            var declared = WireCodec.DeserializeArenaLevelDeclared(
                WireCodec.Serialize(new ArenaLevelDeclared(level, true)));
            var withdrawn = WireCodec.DeserializeArenaLevelDeclared(
                WireCodec.Serialize(new ArenaLevelDeclared(level, false)));

            Assert.True(declared.IsBossArena);
            Assert.False(withdrawn.IsBossArena);
        }

        [Fact]
        public void Request_round_trips()
        {
            var original = new ArenaLevelRequested(new ArenaLevelId(3, 0));

            var decoded = WireCodec.DeserializeArenaLevelRequested(WireCodec.Serialize(original));

            Assert.Equal(original, decoded);
        }

        [Fact]
        public void A_negative_environment_survives_the_wire_for_the_receiver_to_refuse()
        {
            // The codec does not know which environments exist; validating that is the integration layer's job,
            // and it can only do it if the value arrives intact rather than being clamped or thrown here.
            var original = new ArenaLevelDeclared(new ArenaLevelId(-7, 4), true);

            var decoded = WireCodec.DeserializeArenaLevelDeclared(WireCodec.Serialize(original));

            Assert.Equal(-7, decoded.Level.Environment);
            Assert.Equal(4, decoded.Level.LevelIndex);
        }

        [Fact]
        public void Trailing_bytes_throw_not_ignored()
        {
            var payload = WireCodec.Serialize(new ArenaLevelDeclared(new ArenaLevelId(3, 0), true));
            var padded = new byte[payload.Length + 1];
            Array.Copy(payload, padded, payload.Length);

            Assert.Throws<InvalidDataException>(() => WireCodec.DeserializeArenaLevelDeclared(padded));
        }

        [Fact]
        public void Truncated_payload_throws_not_misreads()
        {
            var payload = WireCodec.Serialize(new ArenaLevelRequested(new ArenaLevelId(3, 0)));
            var truncated = new byte[payload.Length - 3];
            Array.Copy(payload, truncated, truncated.Length);

            Assert.ThrowsAny<Exception>(() => WireCodec.DeserializeArenaLevelRequested(truncated));
        }
    }
}
