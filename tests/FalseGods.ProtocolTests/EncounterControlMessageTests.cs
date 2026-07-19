using System;
using System.IO;
using FalseGods.Core.Simulation;
using FalseGods.Protocol.Arena;
using FalseGods.Protocol.Wire;
using Xunit;

namespace FalseGods.ProtocolTests
{
    /// <summary>
    /// Round-trip and untrusted-input tests for the encounter control messages
    /// (Docs/MultiplayerLoadingContract.md §5.3/§5.10): EnterArena, ArenaReady, ArenaLoadFailed,
    /// EncounterAborted, EncounterEnded.
    /// </summary>
    public sealed class EncounterControlMessageTests
    {
        private static ContentHash SampleHash()
        {
            var bytes = new byte[ContentHash.Sha256Length];
            for (var i = 0; i < bytes.Length; i++)
            {
                bytes[i] = (byte)(i * 5);
            }

            return new ContentHash(bytes);
        }

        private static ArenaManifest SampleManifest(bool withHash = true) => new ArenaManifest(
            "false_gods.arena.poc",
            ArenaVersion: 3,
            new ContentHashSchemaVersion(1),
            withHash ? SampleHash() : default,
            ProtocolVersion: ProtocolVersion.Current.Value,
            BundleVersion: "0.1.0");

        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public void EnterArena_round_trips(bool withHash)
        {
            var original = new EnterArena(
                new EncounterId(7),
                SampleManifest(withHash),
                new WorldPosition(102.25f, -3.5f, 88f));

            var decoded = WireCodec.DeserializeEnterArena(WireCodec.Serialize(original));

            Assert.Equal(original, decoded);
        }

        [Fact]
        public void ArenaReady_round_trips()
        {
            var original = new ArenaReady(new EncounterId(7), SampleManifest());

            var decoded = WireCodec.DeserializeArenaReady(WireCodec.Serialize(original));

            Assert.Equal(original, decoded);
        }

        [Fact]
        public void ArenaLoadFailed_round_trips_including_non_ascii_reason()
        {
            var original = new ArenaLoadFailed(new EncounterId(9), "bundle 未找到 — \"quoted\"");

            var decoded = WireCodec.DeserializeArenaLoadFailed(WireCodec.Serialize(original));

            Assert.Equal(original, decoded);
        }

        [Theory]
        [InlineData(EncounterAbortReason.Unspecified)]
        [InlineData(EncounterAbortReason.ContentHashSchemaMismatch)]
        [InlineData(EncounterAbortReason.VersionMismatch)]
        [InlineData(EncounterAbortReason.ContentMismatch)]
        [InlineData(EncounterAbortReason.LoadFailed)]
        [InlineData(EncounterAbortReason.Timeout)]
        public void EncounterAborted_round_trips_every_reason(EncounterAbortReason reason)
        {
            var original = new EncounterAborted(new EncounterId(4), reason);

            var decoded = WireCodec.DeserializeEncounterAborted(WireCodec.Serialize(original));

            Assert.Equal(original, decoded);
        }

        [Fact]
        public void EncounterAborted_rejects_unknown_reason_value()
        {
            var payload = WireCodec.Serialize(new EncounterAborted(new EncounterId(4), EncounterAbortReason.Timeout));
            // The reason int32 is the last four bytes; overwrite with an out-of-range value.
            payload[payload.Length - 4] = 99;

            Assert.Throws<InvalidDataException>(() => WireCodec.DeserializeEncounterAborted(payload));
        }

        [Fact]
        public void EncounterEnded_round_trips()
        {
            var original = new EncounterEnded(new EncounterId(11), new SimulationTick(123456789L));

            var decoded = WireCodec.DeserializeEncounterEnded(WireCodec.Serialize(original));

            Assert.Equal(original, decoded);
        }

        [Fact]
        public void Truncated_payload_throws_not_misreads()
        {
            var payload = WireCodec.Serialize(new EnterArena(
                new EncounterId(7), SampleManifest(), new WorldPosition(1f, 2f, 3f)));
            var truncated = new byte[payload.Length - 5];
            Array.Copy(payload, truncated, truncated.Length);

            Assert.ThrowsAny<Exception>(() => WireCodec.DeserializeEnterArena(truncated));
        }

        [Fact]
        public void Trailing_bytes_throw_not_ignored()
        {
            var payload = WireCodec.Serialize(new EncounterEnded(new EncounterId(1), new SimulationTick(2)));
            var padded = new byte[payload.Length + 1];
            Array.Copy(payload, padded, payload.Length);

            Assert.Throws<InvalidDataException>(() => WireCodec.DeserializeEncounterEnded(padded));
        }
    }
}
