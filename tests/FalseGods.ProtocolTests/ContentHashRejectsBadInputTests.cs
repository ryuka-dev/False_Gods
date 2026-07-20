using System;
using FalseGods.Protocol.Arena;
using Xunit;

namespace FalseGods.ProtocolTests
{
    /// <summary>
    /// The fail-closed half of RiskList R34: authored data that cannot yield a reproducible hash must raise a
    /// build-time export failure, never a runtime hash (Docs/MultiplayerLoadingContract.md §5.2.1). A quiet,
    /// slightly-wrong hash is worse than a loud refusal.
    /// </summary>
    public sealed class ContentHashRejectsBadInputTests
    {
        [Fact]
        public void NaN_position_is_rejected()
        {
            var content = ArenaWithRootTransform(
                new AuthoredTransform(new Vector3(float.NaN, 0f, 0f), Quaternion.Identity, Unit()));

            Assert.Throws<ArenaContentExportException>(() => ContentHashComputer.Compute(content));
        }

        [Fact]
        public void Infinite_scale_is_rejected()
        {
            var content = ArenaWithRootTransform(
                new AuthoredTransform(new Vector3(0f, 0f, 0f), Quaternion.Identity, new Vector3(float.PositiveInfinity, 1f, 1f)));

            Assert.Throws<ArenaContentExportException>(() => ContentHashComputer.Compute(content));
        }

        [Fact]
        public void Zero_length_quaternion_is_rejected()
        {
            var content = ArenaWithRootTransform(
                new AuthoredTransform(new Vector3(0f, 0f, 0f), new Quaternion(0f, 0f, 0f, 0f), Unit()));

            Assert.Throws<ArenaContentExportException>(() => ContentHashComputer.Compute(content));
        }

        [Fact]
        public void Unassigned_marker_is_rejected()
        {
            // default(StableMarkerId) is the empty GUID — never editor-assigned.
            var content = ArenaContentDefinition.Create(
                "false_gods.arena.bad", 1, "content",
                nodes: new[] { new AuthoredNode(default, "Root", null, Identity()) });

            Assert.Throws<ArenaContentExportException>(() => ContentHashComputer.Compute(content));
        }

        [Fact]
        public void Duplicate_marker_is_rejected()
        {
            // Same marker id on a node and a spawn — must be unique across the whole arena, not just per list.
            var content = ArenaContentDefinition.Create(
                "false_gods.arena.dup", 1, "content",
                nodes: new[] { new AuthoredNode(Sample.Marker(7), "Root", null, Identity()) },
                spawns: new[] { new SpawnDefinition(Sample.Marker(7), "Player", "false_gods.spawn.player", Identity()) });

            Assert.Throws<ArenaContentExportException>(() => ContentHashComputer.Compute(content));
        }

        [Fact]
        public void Null_section_list_is_rejected()
        {
            // A dropped section must not hash the same as an empty one. Bypasses the null-coalescing Create().
            var content = new ArenaContentDefinition(
                "false_gods.arena.nulllist", 1, "content",
                Nodes: new[] { new AuthoredNode(Sample.Marker(1), "Root", null, Identity()) },
                VanillaProxies: Array.Empty<VanillaProxyDefinition>(),
                Colliders: Array.Empty<ColliderDefinition>(),
                NavDefinitions: null!,
                Spawns: Array.Empty<SpawnDefinition>(),
                Mechanisms: Array.Empty<MechanismDefinition>(),
                MaterialBorrows: Array.Empty<MaterialBorrowDefinition>());

            Assert.Throws<ArenaContentExportException>(() => ContentHashComputer.Compute(content));
        }

        [Fact]
        public void Null_required_token_is_rejected()
        {
            var content = ArenaContentDefinition.Create(
                "false_gods.arena.bad", 1, "content",
                nodes: new[] { new AuthoredNode(Sample.Marker(1), null!, null, Identity()) });

            Assert.Throws<ArenaContentExportException>(() => ContentHashComputer.Compute(content));
        }

        [Fact]
        public void Blank_required_token_is_rejected()
        {
            var content = ArenaContentDefinition.Create(
                "false_gods.arena.bad", 1, "content",
                colliders: new[] { new ColliderDefinition(Sample.Marker(1), "   ", new double[] { 1d }, "Geometry") });

            Assert.Throws<ArenaContentExportException>(() => ContentHashComputer.Compute(content));
        }

        [Fact]
        public void Blank_arena_id_is_rejected()
        {
            var content = ArenaContentDefinition.Create("", 1, "content");

            Assert.Throws<ArenaContentExportException>(() => ContentHashComputer.Compute(content));
        }

        [Fact]
        public void Null_section_element_is_rejected()
        {
            var content = ArenaContentDefinition.Create(
                "false_gods.arena.bad", 1, "content",
                nodes: new AuthoredNode[] { null! });

            Assert.Throws<ArenaContentExportException>(() => ContentHashComputer.Compute(content));
        }

        [Fact]
        public void Null_geometry_parameters_are_rejected()
        {
            var content = ArenaContentDefinition.Create(
                "false_gods.arena.bad", 1, "content",
                colliders: new[] { new ColliderDefinition(Sample.Marker(1), "Box", null!, "Geometry") });

            Assert.Throws<ArenaContentExportException>(() => ContentHashComputer.Compute(content));
        }

        [Fact]
        public void Null_transform_is_rejected()
        {
            var content = ArenaContentDefinition.Create(
                "false_gods.arena.bad", 1, "content",
                nodes: new[] { new AuthoredNode(Sample.Marker(1), "Root", null, null!) });

            Assert.Throws<ArenaContentExportException>(() => ContentHashComputer.Compute(content));
        }

        [Fact]
        public void Blank_optional_token_is_rejected()
        {
            var content = ArenaContentDefinition.Create(
                "false_gods.arena.bad", 1, "content",
                navDefinitions: new[]
                {
                    new NavDefinition(Sample.Marker(1), "WalkableSurface", new AuthoredBounds(Zero(), Unit()), "   "),
                });

            Assert.Throws<ArenaContentExportException>(() => ContentHashComputer.Compute(content));
        }

        [Fact]
        public void Unassigned_parent_marker_is_rejected()
        {
            var content = ArenaContentDefinition.Create(
                "false_gods.arena.bad", 1, "content",
                nodes: new[] { new AuthoredNode(Sample.Marker(1), "Root", default(StableMarkerId), Identity()) });

            Assert.Throws<ArenaContentExportException>(() => ContentHashComputer.Compute(content));
        }

        [Fact]
        public void Self_parent_is_rejected()
        {
            var content = ArenaContentDefinition.Create(
                "false_gods.arena.bad", 1, "content",
                nodes: new[] { new AuthoredNode(Sample.Marker(1), "Root", Sample.Marker(1), Identity()) });

            Assert.Throws<ArenaContentExportException>(() => ContentHashComputer.Compute(content));
        }

        [Fact]
        public void Dangling_parent_marker_is_rejected()
        {
            var content = ArenaContentDefinition.Create(
                "false_gods.arena.bad", 1, "content",
                nodes: new[] { new AuthoredNode(Sample.Marker(1), "Root", Sample.Marker(2), Identity()) });

            Assert.Throws<ArenaContentExportException>(() => ContentHashComputer.Compute(content));
        }

        private static Vector3 Zero() => new Vector3(0f, 0f, 0f);

        private static Vector3 Unit() => new Vector3(1f, 1f, 1f);

        private static AuthoredTransform Identity() => new AuthoredTransform(new Vector3(0f, 0f, 0f), Quaternion.Identity, Unit());

        private static ArenaContentDefinition ArenaWithRootTransform(AuthoredTransform transform) =>
            ArenaContentDefinition.Create(
                "false_gods.arena.bad", 1, "content",
                nodes: new[] { new AuthoredNode(Sample.Marker(1), "Root", null, transform) });
    }
}
