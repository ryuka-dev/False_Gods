using FalseGods.Application.Replication;
using FalseGods.Core.Simulation;
using FalseGods.Protocol.Arena;
using FalseGods.Protocol.Wire;
using Xunit;

namespace FalseGods.ApplicationTests
{
    /// <summary>
    /// The <see cref="EncounterCodec"/> framing for the encounter control messages: each rides the one opaque
    /// channel under its own <see cref="ReplicationKind"/> and decodes back to an equal DTO
    /// (Docs/MultiplayerLoadingContract.md §5.10).
    /// </summary>
    public sealed class EncounterCodecControlTests
    {
        private static ArenaManifest Manifest()
        {
            var bytes = new byte[ContentHash.Sha256Length];
            for (var i = 0; i < bytes.Length; i++)
            {
                bytes[i] = (byte)i;
            }

            return new ArenaManifest(
                "false_gods.arena.poc", 1, new ContentHashSchemaVersion(1), new ContentHash(bytes),
                ProtocolVersion.Current.Value, "0.1.0");
        }

        [Fact]
        public void EnterArena_encodes_and_decodes_under_its_kind()
        {
            var original = new EnterArena(new EncounterId(3), Manifest(), new WorldPosition(1f, 2f, 3f));

            var decoded = EncounterCodec.Decode(EncounterCodec.Encode(original));

            Assert.Equal(ReplicationKind.EnterArena, decoded.Kind);
            Assert.Equal(original, decoded.Value);
        }

        [Fact]
        public void ArenaReady_encodes_and_decodes_under_its_kind()
        {
            var original = new ArenaReady(new EncounterId(3), Manifest());

            var decoded = EncounterCodec.Decode(EncounterCodec.Encode(original));

            Assert.Equal(ReplicationKind.ArenaReady, decoded.Kind);
            Assert.Equal(original, decoded.Value);
        }

        [Fact]
        public void ArenaLoadFailed_encodes_and_decodes_under_its_kind()
        {
            var original = new ArenaLoadFailed(new EncounterId(3), "artifact missing");

            var decoded = EncounterCodec.Decode(EncounterCodec.Encode(original));

            Assert.Equal(ReplicationKind.ArenaLoadFailed, decoded.Kind);
            Assert.Equal(original, decoded.Value);
        }

        [Fact]
        public void EncounterAborted_encodes_and_decodes_under_its_kind()
        {
            var original = new EncounterAborted(new EncounterId(3), EncounterAbortReason.ContentMismatch);

            var decoded = EncounterCodec.Decode(EncounterCodec.Encode(original));

            Assert.Equal(ReplicationKind.EncounterAborted, decoded.Kind);
            Assert.Equal(original, decoded.Value);
        }

        [Fact]
        public void EncounterEnded_encodes_and_decodes_under_its_kind()
        {
            var original = new EncounterEnded(new EncounterId(3), new SimulationTick(77));

            var decoded = EncounterCodec.Decode(EncounterCodec.Encode(original));

            Assert.Equal(ReplicationKind.EncounterEnded, decoded.Kind);
            Assert.Equal(original, decoded.Value);
        }
    }
}
