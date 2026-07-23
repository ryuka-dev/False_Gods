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
            "7d200bb138fcdb898e17cf1feb8235beae23b7a2ac74bdad840f9fb9b200f885";

        private static string FixtureText() =>
            File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "arena-content-PocRoom.artifact"));

        [Fact]
        public void The_exported_poc_room_artifact_parses()
        {
            var artifact = ArenaContentArtifact.Parse(FixtureText());

            Assert.Equal("false_gods.arena.poc_room", artifact.Definition.ArenaId);
            Assert.Equal(1, artifact.Definition.ArenaVersion);
            Assert.Equal(ContentHashSchemaVersion.Current, artifact.SchemaVersion);

            // The authored identity set the exporter declares for the cave arena. Decoration rocks are
            // hand-authored presentation and carry NO artifact rows (excluded from hash + parity, like lighting).
            // ArenaRoot + 4 roots + Floor + 4 walls + Ceiling.
            Assert.Equal(11, artifact.Definition.Nodes.Count);
            Assert.Empty(artifact.Definition.VanillaProxies);   // the cave uses only our own meshes
            Assert.Equal(5, artifact.Definition.Colliders.Count); // floor + four boundary walls
            Assert.Single(artifact.Definition.NavDefinitions);  // the walkable floor surface
            Assert.Equal(2, artifact.Definition.Spawns.Count);  // player + dummy enemy
            Assert.Empty(artifact.Definition.Mechanisms);
            // The borrowed cave surfaces: Floor + 4 walls + Ceiling (rocks carry their own baked material).
            Assert.Equal(6, artifact.Definition.MaterialBorrows.Count);
            Assert.Equal(6, artifact.MaterialBorrowPlacements.Count);        // their runtime target paths
            // 4 roots + Floor + 4 walls + Ceiling + 5 colliders + 2 spawns.
            Assert.Equal(17, artifact.Parity.Count);
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
