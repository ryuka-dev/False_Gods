using System.Collections.Generic;

namespace FalseGods.Protocol.Arena
{
    // The authored building blocks of an arena's content identity. Each corresponds to one numbered input in
    // the canonical ContentHash definition (Docs/MultiplayerLoadingContract.md §5.2.1). They are dumb,
    // immutable carriers: all validation (missing/duplicate marker ids, NaN/infinite floats, zero-length
    // quaternions) happens once, at hash time, in ContentHashComputer — mirroring the doc's "build-time export
    // failure" rule. "Kind" and "id" fields are opaque canonical string tokens produced by the editor exporter;
    // the hash treats them as raw bytes and does not interpret them, so the taxonomy is not fixed here.

    /// <summary>Input 4 — an authored hierarchy node: its id, kind, parent (absent for a root), and local transform.</summary>
    public sealed record AuthoredNode(
        StableMarkerId MarkerId,
        string NodeKind,
        StableMarkerId? ParentMarkerId,
        AuthoredTransform LocalTransform);

    /// <summary>
    /// Input 5 — a vanilla asset reused at runtime. The hash encodes the <em>resolved stable asset identity</em>
    /// (Addressables key or content GUID), never the loaded object (Docs/ArenaLoadingProposal.md).
    /// </summary>
    public sealed record VanillaProxyDefinition(
        StableMarkerId MarkerId,
        string AddressableKeyOrGuid,
        AuthoredTransform LocalTransform);

    /// <summary>
    /// Input 10 — a vanilla MATERIAL borrowed onto one of our own authored renderers (direction B: our own
    /// large-cave meshes wearing vanilla cave materials). Vanilla materials are not individually addressable, so
    /// the donor is named by a <em>carrier</em> Room prefab GUID plus the material's authored NAME (semantic and
    /// unique within a carrier — far stabler than a hierarchy path; a name path breaks on the duplicate-sibling
    /// mesh names vanilla cave rooms carry). The hash encodes that stable asset identity — carrier GUID +
    /// material name + which of our renderers receives it — never the loaded <c>Material</c>.
    /// </summary>
    /// <param name="MarkerId">This borrow's own authored identity (ordering + uniqueness in the hash).</param>
    /// <param name="TargetMarkerId">The authored node whose renderer receives the borrowed material.</param>
    /// <param name="TargetSubMaterialIndex">Which sub-material slot on the target renderer to overwrite (0 for a
    /// single-material mesh).</param>
    /// <param name="CarrierGuid">The vanilla Room prefab GUID to load as the material donor.</param>
    /// <param name="MaterialName">The <c>Material.name</c> to resolve within the carrier's renderers.</param>
    public sealed record MaterialBorrowDefinition(
        StableMarkerId MarkerId,
        StableMarkerId TargetMarkerId,
        int TargetSubMaterialIndex,
        string CarrierGuid,
        string MaterialName);

    /// <summary>
    /// Input 6 — an authored collider. <see cref="GeometryParameters"/> are the kind-specific dimensions
    /// (e.g. box half-extents, sphere radius) in a fixed authored order; they are quantised as lengths.
    /// <see cref="LayerName"/> is the physics/nav layer <em>name</em>, never a layer index (indices are not
    /// stable across builds).
    /// </summary>
    public sealed record ColliderDefinition(
        StableMarkerId MarkerId,
        string ColliderKind,
        IReadOnlyList<double> GeometryParameters,
        string LayerName);

    /// <summary>
    /// Input 7 — an authored navigation contribution (walkable surface, anchor, off-mesh-link endpoint, or
    /// forbidden region). <see cref="NavmeshPrefabContentId"/> is present only on the prebaked
    /// <c>NavmeshPrefab</c> path (Docs/CollisionAndNavigationProposal.md, Option 1) and absent otherwise.
    /// </summary>
    public sealed record NavDefinition(
        StableMarkerId MarkerId,
        string NavKind,
        AuthoredBounds Bounds,
        string? NavmeshPrefabContentId);

    /// <summary>Input 8 — an authored spawn point (player/boss/enemy), its definition id, and local transform.</summary>
    public sealed record SpawnDefinition(
        StableMarkerId MarkerId,
        string SpawnKind,
        string DefinitionId,
        AuthoredTransform LocalTransform);

    /// <summary>
    /// Input 9 — an authored arena mechanism, tagged with the group it belongs to (e.g. "phase_2"), and its
    /// local transform. The arena mechanism vocabulary lives here, never welded into a boss snapshot
    /// (Docs/Architecture.md §5, ADR-005).
    /// </summary>
    public sealed record MechanismDefinition(
        StableMarkerId MarkerId,
        string MechanismDefinitionId,
        string MechanismGroupId,
        AuthoredTransform LocalTransform);
}
