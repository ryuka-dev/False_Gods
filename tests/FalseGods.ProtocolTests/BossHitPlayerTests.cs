using System;
using System.IO;
using FalseGods.Core.Simulation;
using FalseGods.Protocol.Wire;
using Xunit;

namespace FalseGods.ProtocolTests
{
    /// <summary>Round-trip and untrusted-input tests for the host → client player-damage message
    /// (Docs/MultiplayerLoadingContract.md §5.6): the host owns the amount, the client applies it.</summary>
    public sealed class BossHitPlayerTests
    {
        [Fact]
        public void Round_trips()
        {
            var original = new BossHitPlayer(new EncounterId(7), 42);

            var decoded = WireCodec.DeserializeBossHitPlayer(WireCodec.Serialize(original));

            Assert.Equal(original, decoded);
        }

        [Fact]
        public void Trailing_bytes_throw_not_ignored()
        {
            var payload = WireCodec.Serialize(new BossHitPlayer(new EncounterId(1), 5));
            var padded = new byte[payload.Length + 1];
            Array.Copy(payload, padded, payload.Length);

            Assert.Throws<InvalidDataException>(() => WireCodec.DeserializeBossHitPlayer(padded));
        }

        [Fact]
        public void Truncated_payload_throws_not_misreads()
        {
            var payload = WireCodec.Serialize(new BossHitPlayer(new EncounterId(9), 30));
            var truncated = new byte[payload.Length - 3];
            Array.Copy(payload, truncated, truncated.Length);

            Assert.ThrowsAny<Exception>(() => WireCodec.DeserializeBossHitPlayer(truncated));
        }
    }
}
