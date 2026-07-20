using System;
using System.IO;
using FalseGods.Core.Simulation;
using FalseGods.Protocol.Wire;
using Xunit;

namespace FalseGods.ProtocolTests
{
    /// <summary>
    /// Round-trip and untrusted-input tests for the client → host hit request
    /// (Docs/OriginalBossNetworkingArchitecture.md §5.6): the wire form of "client reports intent, host owns result".
    /// </summary>
    public sealed class ClientHitRequestTests
    {
        [Fact]
        public void Round_trips_with_attacker_position()
        {
            var original = new ClientHitRequest(
                new EncounterId(7),
                RequestSequence: 42,
                DamageCandidate: 137.5f,
                new WorldPosition(102.25f, -3.5f, 88f));

            var decoded = WireCodec.DeserializeClientHitRequest(WireCodec.Serialize(original));

            Assert.Equal(original, decoded);
        }

        [Fact]
        public void Round_trips_without_attacker_position()
        {
            var original = new ClientHitRequest(new EncounterId(3), RequestSequence: 1, DamageCandidate: 9f, null);

            var decoded = WireCodec.DeserializeClientHitRequest(WireCodec.Serialize(original));

            Assert.Equal(original, decoded);
            Assert.Null(decoded.AttackerPosition);
        }

        [Fact]
        public void Preserves_non_finite_candidate_verbatim_for_the_host_to_reject()
        {
            // The codec is a faithful pipe; finiteness is the host's validation concern, not the wire's.
            var original = new ClientHitRequest(new EncounterId(1), 0, float.PositiveInfinity, null);

            var decoded = WireCodec.DeserializeClientHitRequest(WireCodec.Serialize(original));

            Assert.True(float.IsInfinity(decoded.DamageCandidate));
        }

        [Fact]
        public void Truncated_payload_throws_not_misreads()
        {
            var payload = WireCodec.Serialize(
                new ClientHitRequest(new EncounterId(7), 5, 12f, new WorldPosition(1f, 2f, 3f)));
            var truncated = new byte[payload.Length - 5];
            Array.Copy(payload, truncated, truncated.Length);

            Assert.ThrowsAny<Exception>(() => WireCodec.DeserializeClientHitRequest(truncated));
        }

        [Fact]
        public void Trailing_bytes_throw_not_ignored()
        {
            var payload = WireCodec.Serialize(new ClientHitRequest(new EncounterId(1), 2, 3f, null));
            var padded = new byte[payload.Length + 1];
            Array.Copy(payload, padded, payload.Length);

            Assert.Throws<InvalidDataException>(() => WireCodec.DeserializeClientHitRequest(padded));
        }
    }
}
