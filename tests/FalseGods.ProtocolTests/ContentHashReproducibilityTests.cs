using System.Collections.Generic;
using System.Linq;
using FalseGods.Protocol.Arena;
using Xunit;

namespace FalseGods.ProtocolTests
{
    /// <summary>
    /// The reproducibility half of RiskList R34: the same authored content must hash the same on any machine,
    /// under any load order, and quantisation must absorb sub-resolution float noise — otherwise the ready gate
    /// becomes a random source of refusals (Docs/MultiplayerLoadingContract.md §5.2.1).
    /// </summary>
    public sealed class ContentHashReproducibilityTests
    {
        [Fact]
        public void Same_content_hashes_identically_twice()
        {
            var first = ContentHashComputer.Compute(Sample.FullArena());
            var second = ContentHashComputer.Compute(Sample.FullArena());

            Assert.Equal(first, second);
            Assert.Equal(32, first.Length); // SHA-256
        }

        [Fact]
        public void List_order_does_not_change_the_hash()
        {
            // Every list is sorted by StableMarkerId before encoding, so reversing the authored order — the way
            // Addressables completion order or hierarchy enumeration might differ between peers — must not matter.
            var inOrder = ContentHashComputer.Compute(Sample.FullArena(reversed: false));
            var reversed = ContentHashComputer.Compute(Sample.FullArena(reversed: true));

            Assert.Equal(inOrder, reversed);
        }

        [Fact]
        public void Adding_a_node_changes_the_hash()
        {
            var baseline = Sample.FullArena();
            var extended = baseline.WithNodes(
                baseline.Nodes.Concat(new[]
                {
                    new AuthoredNode(Sample.Marker(999), "Marker", Sample.Marker(1), Sample.Transform(5f, 0f, 5f)),
                }));

            Assert.NotEqual(
                ContentHashComputer.Compute(baseline),
                ContentHashComputer.Compute(extended));
        }

        [Fact]
        public void Sub_quantum_translation_is_absorbed()
        {
            // Length quantisation is 0.1 mm (value * 10_000). A shift far below that rounds to the same integer.
            var baseline = Sample.FullArena();
            var nudged = baseline.WithNodes(NudgeFirstNode(baseline, dx: 0.000001f));

            Assert.Equal(
                ContentHashComputer.Compute(baseline),
                ContentHashComputer.Compute(nudged));
        }

        [Fact]
        public void Supra_quantum_translation_changes_the_hash()
        {
            var baseline = Sample.FullArena();
            var moved = baseline.WithNodes(NudgeFirstNode(baseline, dx: 0.01f)); // 100 quanta

            Assert.NotEqual(
                ContentHashComputer.Compute(baseline),
                ContentHashComputer.Compute(moved));
        }

        [Fact]
        public void Negated_quaternion_hashes_the_same()
        {
            // q and -q are the same rotation; the sign-canonicalisation rule must collapse them.
            var q = new Quaternion(0.1f, 0.2f, 0.3f, 0.9f);
            var negated = new Quaternion(-q.X, -q.Y, -q.Z, -q.W);

            Assert.Equal(
                ContentHashComputer.Compute(ArenaWithRootRotation(q)),
                ContentHashComputer.Compute(ArenaWithRootRotation(negated)));
        }

        [Fact]
        public void Negative_zero_position_hashes_like_positive_zero()
        {
            Assert.Equal(
                ContentHashComputer.Compute(ArenaWithRootPosition(new Vector3(0f, 0f, 0f))),
                ContentHashComputer.Compute(ArenaWithRootPosition(new Vector3(-0f, -0f, -0f))));
        }

        [Fact]
        public void Schema_version_is_part_of_the_hash()
        {
            var content = Sample.FullArena();

            Assert.NotEqual(
                ContentHashComputer.Compute(content, new ContentHashSchemaVersion(1)),
                ContentHashComputer.Compute(content, new ContentHashSchemaVersion(2)));
        }

        private static IEnumerable<AuthoredNode> NudgeFirstNode(ArenaContentDefinition content, float dx)
        {
            var nodes = content.Nodes.ToList();
            var first = nodes[0];
            var p = first.LocalTransform.Position;
            nodes[0] = first with
            {
                LocalTransform = first.LocalTransform with { Position = new Vector3(p.X + dx, p.Y, p.Z) },
            };
            return nodes;
        }

        private static ArenaContentDefinition ArenaWithRootRotation(Quaternion rotation) =>
            ArenaContentDefinition.Create(
                "false_gods.arena.rot", 1, "content",
                nodes: new[] { new AuthoredNode(Sample.Marker(1), "Root", null, Sample.Transform(rotation: rotation)) });

        private static ArenaContentDefinition ArenaWithRootPosition(Vector3 position) =>
            ArenaContentDefinition.Create(
                "false_gods.arena.pos", 1, "content",
                nodes: new[] { new AuthoredNode(Sample.Marker(1), "Root", null, new AuthoredTransform(position, Quaternion.Identity, new Vector3(1f, 1f, 1f))) });
    }
}
