using System;
using System.Collections.Generic;

namespace FalseGods.Protocol.Arena
{
    /// <summary>
    /// The complete authored description of one arena, from which the canonical <see cref="ContentHash"/> is
    /// derived (Docs/MultiplayerLoadingContract.md §5.2.1).
    /// </summary>
    /// <remarks>
    /// This is the "canonical document derived from authored data" the hash is defined over — never live scene
    /// objects, runtime A* scan output, instance ids, or hierarchy enumeration order. Two peers on different
    /// machines that shipped the same arena build the same document and therefore the same hash. The lists may
    /// be empty (a fixed test room has no mechanisms, for instance) but must never be <c>null</c>;
    /// <see cref="ContentHashComputer"/> rejects a null list rather than treating it as empty, so an
    /// accidentally-dropped section cannot silently produce a matching hash.
    /// </remarks>
    public sealed record ArenaContentDefinition(
        string ArenaId,
        int ArenaVersion,
        string ArenaContentId,
        IReadOnlyList<AuthoredNode> Nodes,
        IReadOnlyList<VanillaProxyDefinition> VanillaProxies,
        IReadOnlyList<ColliderDefinition> Colliders,
        IReadOnlyList<NavDefinition> NavDefinitions,
        IReadOnlyList<SpawnDefinition> Spawns,
        IReadOnlyList<MechanismDefinition> Mechanisms,
        IReadOnlyList<MaterialBorrowDefinition> MaterialBorrows)
    {
        /// <summary>
        /// A convenience factory for an arena that has no content of some kinds yet (e.g. the PoC test room,
        /// which has no mechanisms). Any argument left null becomes an empty list, keeping the "no null list"
        /// invariant that <see cref="ContentHashComputer"/> enforces.
        /// </summary>
        public static ArenaContentDefinition Create(
            string arenaId,
            int arenaVersion,
            string arenaContentId,
            IReadOnlyList<AuthoredNode>? nodes = null,
            IReadOnlyList<VanillaProxyDefinition>? vanillaProxies = null,
            IReadOnlyList<ColliderDefinition>? colliders = null,
            IReadOnlyList<NavDefinition>? navDefinitions = null,
            IReadOnlyList<SpawnDefinition>? spawns = null,
            IReadOnlyList<MechanismDefinition>? mechanisms = null,
            IReadOnlyList<MaterialBorrowDefinition>? materialBorrows = null) =>
            new ArenaContentDefinition(
                arenaId,
                arenaVersion,
                arenaContentId,
                nodes ?? Array.Empty<AuthoredNode>(),
                vanillaProxies ?? Array.Empty<VanillaProxyDefinition>(),
                colliders ?? Array.Empty<ColliderDefinition>(),
                navDefinitions ?? Array.Empty<NavDefinition>(),
                spawns ?? Array.Empty<SpawnDefinition>(),
                mechanisms ?? Array.Empty<MechanismDefinition>(),
                materialBorrows ?? Array.Empty<MaterialBorrowDefinition>());
    }
}
