using System.Collections.Generic;
using FalseGods.Protocol.Arena;
using Xunit;

namespace FalseGods.ProtocolTests
{
    /// <summary>
    /// The shippable arena content artifact must round-trip the authored inputs losslessly, so the runtime
    /// rebuilds the exact same <see cref="ArenaContentDefinition"/> and therefore the exact same
    /// <see cref="ContentHash"/> it would have on any machine (RiskList R34), and carry the parity map the
    /// runtime checks the realized hierarchy against (RiskList R14). This guards the reader the plugin and the
    /// editor exporter both target; a change to the on-disk shape must break these before it reaches a session.
    /// </summary>
    public sealed class ArenaContentArtifactTests
    {
        private static ArenaContentArtifact Sample() => new ArenaContentArtifact(
            Definition: ProtocolTests.Sample.FullArena(),
            SchemaVersion: ContentHashSchemaVersion.Current,
            ProtocolVersion: 1,
            BundleVersion: "poc-2026-07-13",
            Parity: new List<ArenaParityNode>
            {
                new ArenaParityNode("VisualRoot/Floor", "Floor", ProtocolTests.Sample.Transform(0f, -0.25f, 0f)),
                new ArenaParityNode("GameplayRoot/PlayerSpawn", "Player", ProtocolTests.Sample.Transform(-7f, 0f, -7f)),
            });

        [Fact]
        public void Round_trip_preserves_the_content_hash()
        {
            var original = Sample();

            var parsed = ArenaContentArtifact.Parse(original.Serialize());

            // The whole point: the artifact is a faithful transport for the hashable authored inputs.
            Assert.Equal(original.ComputeContentHash(), parsed.ComputeContentHash());
        }

        [Fact]
        public void Round_trip_preserves_stamps_and_parity()
        {
            var original = Sample();

            var parsed = ArenaContentArtifact.Parse(original.Serialize());

            Assert.Equal(original.SchemaVersion, parsed.SchemaVersion);
            Assert.Equal(original.ProtocolVersion, parsed.ProtocolVersion);
            Assert.Equal(original.BundleVersion, parsed.BundleVersion);

            // Content fidelity is asserted via the hash (Round_trip_preserves_the_content_hash); here just prove
            // no section was dropped or duplicated on the way through the file, and that the identity scalars and
            // the parity map (whose element record has no nested list, so it compares by value) survive.
            Assert.Equal(original.Definition.ArenaId, parsed.Definition.ArenaId);
            Assert.Equal(original.Definition.ArenaVersion, parsed.Definition.ArenaVersion);
            Assert.Equal(original.Definition.ArenaContentId, parsed.Definition.ArenaContentId);
            Assert.Equal(original.Definition.Nodes.Count, parsed.Definition.Nodes.Count);
            Assert.Equal(original.Definition.VanillaProxies.Count, parsed.Definition.VanillaProxies.Count);
            Assert.Equal(original.Definition.Colliders.Count, parsed.Definition.Colliders.Count);
            Assert.Equal(original.Definition.NavDefinitions.Count, parsed.Definition.NavDefinitions.Count);
            Assert.Equal(original.Definition.Spawns.Count, parsed.Definition.Spawns.Count);
            Assert.Equal(original.Definition.Mechanisms.Count, parsed.Definition.Mechanisms.Count);
            Assert.Equal(original.Parity, parsed.Parity);
        }

        [Fact]
        public void Serialization_is_stable_under_reparse()
        {
            var once = Sample().Serialize();
            var twice = ArenaContentArtifact.Parse(once).Serialize();

            Assert.Equal(once, twice);
        }

        [Fact]
        public void Manifest_carries_the_recomputed_hash()
        {
            var artifact = Sample();

            var manifest = artifact.ToManifest();

            Assert.Equal(artifact.ComputeContentHash(), manifest.ContentHash);
            Assert.Equal(artifact.Definition.ArenaId, manifest.ArenaId);
            Assert.Equal(artifact.SchemaVersion, manifest.ContentHashSchemaVersion);
        }

        [Fact]
        public void Reversed_author_order_round_trips_to_the_same_hash()
        {
            // The artifact must not smuggle list order into identity: a peer that authored the same content in a
            // different order produces a different-looking file but the same hash.
            var forward = Sample() with { Definition = ProtocolTests.Sample.FullArena(reversed: false) };
            var reversed = Sample() with { Definition = ProtocolTests.Sample.FullArena(reversed: true) };

            Assert.Equal(
                ArenaContentArtifact.Parse(forward.Serialize()).ComputeContentHash(),
                ArenaContentArtifact.Parse(reversed.Serialize()).ComputeContentHash());
        }

        [Theory]
        [InlineData("")]
        [InlineData("NOTFGARENA\t1")]
        [InlineData("FGARENA\t999")]                       // unsupported artifact format version
        [InlineData("FGARENA\t1\narenaId\tx")]              // truncated header (missing later required rows)
        public void Malformed_artifacts_are_rejected(string text)
        {
            Assert.Throws<ArenaContentExportException>(() => ArenaContentArtifact.Parse(text));
        }

        [Fact]
        public void An_unknown_row_tag_is_rejected()
        {
            var text = Sample().Serialize() + "surprise\tvalue\n";

            Assert.Throws<ArenaContentExportException>(() => ArenaContentArtifact.Parse(text));
        }
    }
}
