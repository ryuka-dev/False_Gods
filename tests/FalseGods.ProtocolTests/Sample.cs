using System;
using System.Collections.Generic;
using System.Linq;
using FalseGods.Protocol.Arena;

namespace FalseGods.ProtocolTests
{
    /// <summary>
    /// Builds representative authored arena content for the ContentHash tests. Uses fixed GUIDs so ordering
    /// is deterministic and controllable, and populates every §5.2.1 section so the tests exercise the whole
    /// canonical document, not just one branch.
    /// </summary>
    internal static class Sample
    {
        public static StableMarkerId Marker(int seed) =>
            new StableMarkerId(new Guid($"{seed:00000000}-0000-0000-0000-000000000000"));

        public static AuthoredTransform Transform(
            float x = 0f, float y = 0f, float z = 0f,
            Quaternion? rotation = null,
            float scale = 1f) =>
            new AuthoredTransform(
                new Vector3(x, y, z),
                rotation ?? Quaternion.Identity,
                new Vector3(scale, scale, scale));

        /// <summary>
        /// A fully-populated arena with two entries in every section. <paramref name="reversed"/> returns the
        /// identical content with every list order reversed — same content, different load order.
        /// </summary>
        public static ArenaContentDefinition FullArena(bool reversed = false)
        {
            var nodes = new List<AuthoredNode>
            {
                new AuthoredNode(Marker(1), "Root", null, Transform()),
                new AuthoredNode(Marker(2), "CollisionRoot", Marker(1), Transform(0f, 1f, 0f)),
            };

            var proxies = new List<VanillaProxyDefinition>
            {
                new VanillaProxyDefinition(Marker(10), "Assets/Chunks/Pools/PoolsChunk1.prefab", Transform(2f, 0f, 3f)),
                new VanillaProxyDefinition(Marker(11), "9f1c2d3e4b5a60718293a4b5c6d7e8f9", Transform(-2f, 0f, -3f)),
            };

            var colliders = new List<ColliderDefinition>
            {
                new ColliderDefinition(Marker(20), "Box", new double[] { 10d, 0.5d, 10d }, "Geometry"),
                new ColliderDefinition(Marker(21), "Sphere", new double[] { 1.25d }, "Geometry"),
            };

            var nav = new List<NavDefinition>
            {
                new NavDefinition(Marker(30), "WalkableSurface", new AuthoredBounds(new Vector3(0f, 0f, 0f), new Vector3(20f, 0.1f, 20f)), null),
                new NavDefinition(Marker(31), "Anchor", new AuthoredBounds(new Vector3(1f, 0f, 1f), new Vector3(0.2f, 0.2f, 0.2f)), "Assets/FalseGods/Nav/Room01Navmesh.prefab"),
            };

            var spawns = new List<SpawnDefinition>
            {
                new SpawnDefinition(Marker(40), "Player", "false_gods.spawn.player", Transform(0f, 0f, -8f)),
                new SpawnDefinition(Marker(41), "Enemy", "false_gods.spawn.dummy", Transform(0f, 0f, 8f)),
            };

            var mechanisms = new List<MechanismDefinition>
            {
                new MechanismDefinition(Marker(50), "false_gods.mech.pillar", "phase_2", Transform(0f, 0f, 0f)),
                new MechanismDefinition(Marker(51), "false_gods.mech.hazard", "phase_2", Transform(4f, 0f, 4f)),
            };

            var materialBorrows = new List<MaterialBorrowDefinition>
            {
                // Target the authored nodes above (Marker 1/2); carrier GUID + material name are the donor identity.
                new MaterialBorrowDefinition(Marker(60), Marker(1), 0, "92103c239550ca740906311170fcc458", "CaveFloor"),
                new MaterialBorrowDefinition(Marker(61), Marker(2), 1, "92103c239550ca740906311170fcc458", "CaveWall"),
            };

            if (reversed)
            {
                nodes.Reverse();
                proxies.Reverse();
                colliders.Reverse();
                nav.Reverse();
                spawns.Reverse();
                mechanisms.Reverse();
                materialBorrows.Reverse();
            }

            return new ArenaContentDefinition(
                "false_gods.arena.poc_room",
                ArenaVersion: 1,
                ArenaContentId: "FalseGods/Arenas/PocRoom.prefab#a1b2c3",
                nodes,
                proxies,
                colliders,
                nav,
                spawns,
                mechanisms,
                materialBorrows);
        }

        /// <summary>Returns a copy of <paramref name="content"/> with its node list replaced.</summary>
        public static ArenaContentDefinition WithNodes(this ArenaContentDefinition content, IEnumerable<AuthoredNode> nodes) =>
            content with { Nodes = nodes.ToList() };
    }
}
