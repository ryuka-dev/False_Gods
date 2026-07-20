using System;
using System.IO;
using FalseGods.Protocol.Arena;
using Xunit;

namespace FalseGods.ProtocolTests
{
    /// <summary>
    /// Pins the real artifact the Unity editor exporter produced from the PoC room. The exporter's writer and
    /// this assembly's reader are re-implemented separately (Unity is .NET Standard, Protocol is net472), so this
    /// fixture is the contract between them: if the format drifts on either side, parsing fails or the golden
    /// hash changes here — long before a ready gate refuses a correct session (RiskList R34).
    /// </summary>
    public sealed class ArenaContentArtifactFixtureTests
    {
        // Recomputed from the committed fixture; a change to the exporter output, the artifact format, or the
        // canonical hash definition must update this deliberately (a hash change is a ContentHashSchemaVersion
        // change — MultiplayerLoadingContract §5.2.1).
        private const string GoldenContentHashHex =
            "7dc53023e646e16574c5c35cc5fecf1f7202f267775d39fe2a7bedd98c8bcac6";

        private static string FixtureText() =>
            File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "arena-content-PocRoom.artifact"));

        [Fact]
        public void The_exported_poc_room_artifact_parses()
        {
            var artifact = ArenaContentArtifact.Parse(FixtureText());

            Assert.Equal("false_gods.arena.poc_room", artifact.Definition.ArenaId);
            Assert.Equal(1, artifact.Definition.ArenaVersion);
            Assert.Equal(ContentHashSchemaVersion.Current, artifact.SchemaVersion);

            // The authored identity set the exporter declares for the PoC room.
            Assert.Equal(7, artifact.Definition.Nodes.Count);
            Assert.Empty(artifact.Definition.VanillaProxies);   // the PoC room uses only our own meshes
            Assert.Equal(6, artifact.Definition.Colliders.Count); // floor, pillar, four walls
            Assert.Single(artifact.Definition.NavDefinitions);  // the walkable floor surface
            Assert.Equal(2, artifact.Definition.Spawns.Count);  // player + dummy enemy
            Assert.Empty(artifact.Definition.Mechanisms);
            Assert.Empty(artifact.Definition.MaterialBorrows); // the PoC room borrows no vanilla materials yet
            Assert.Equal(14, artifact.Parity.Count);
        }

        [Fact]
        public void The_exported_artifact_hashes_to_the_golden_value()
        {
            var hash = ArenaContentArtifact.Parse(FixtureText()).ComputeContentHash();

            Assert.Equal(ContentHash.Sha256Length, hash.Length);
            Assert.Equal(GoldenContentHashHex, hash.ToHex());
        }

        [Fact]
        public void Reparsing_the_artifact_is_byte_stable()
        {
            // The R34 property at the artifact level: the same shipped bytes recompute the same hash every time,
            // regardless of anything the runtime did between loads (here, a re-parse from scratch).
            var first = ArenaContentArtifact.Parse(FixtureText()).ComputeContentHash();
            var second = ArenaContentArtifact.Parse(FixtureText()).ComputeContentHash();

            Assert.Equal(first, second);
        }
    }
}
