using System.Collections.Generic;

namespace FalseGods.Application.Arena
{
    /// <summary>
    /// One resolved material-borrow instruction the runtime can act on: paint the renderer at
    /// <see cref="TargetPath"/> (sub-material <see cref="TargetSubMaterialIndex"/>) in the realized arena with the
    /// vanilla material named <see cref="MaterialName"/>, taken from the carrier prefab <see cref="CarrierGuid"/>.
    /// </summary>
    /// <remarks>
    /// A plain, Unity-free request (Application has no Unity reference). The load flow builds these from the
    /// hashed <c>MaterialBorrowDefinition</c>s (carrier + material name + sub-material index) paired with their
    /// non-hashed <c>MaterialBorrowPlacement</c> target paths; the adapter turns them into Addressables loads and
    /// <c>sharedMaterials</c> assignments.
    /// </remarks>
    public sealed record MaterialBorrowRequest(
        string TargetPath,
        int TargetSubMaterialIndex,
        string CarrierGuid,
        string MaterialName);

    /// <summary>
    /// A convention-based paint for hand-authored decoration that carries no artifact rows: paint every renderer
    /// anywhere under <see cref="ParentPath"/> — at any depth, so the author is free to group décor under empty
    /// holder objects — whose GameObject name starts with <see cref="ChildNamePrefix"/> (sub-material
    /// <see cref="SubMaterialIndex"/>) with the vanilla material <see cref="MaterialName"/> from carrier
    /// <see cref="CarrierGuid"/>. Used for the decoration rocks — excluded from the content hash, so their count and
    /// placement are free to change without a rehash — via the same borrow machinery, targeted by naming convention
    /// rather than a hashed per-node row.
    /// </summary>
    public sealed record MaterialConventionPaint(
        string ParentPath,
        string ChildNamePrefix,
        int SubMaterialIndex,
        string CarrierGuid,
        string MaterialName);

    /// <summary>One sub-mesh's borrow, keyed by the placeholder material the authored mesh already wears rather
    /// than by sub-mesh index. <see cref="PlaceholderName"/> is matched against the renderer's current material
    /// name; a match is repainted with <see cref="VanillaMaterialName"/> from the carrier.</summary>
    public sealed record SubmeshMaterialRule(string PlaceholderName, string VanillaMaterialName);

    /// <summary>
    /// Paint the sub-materials of one hand-authored décor renderer (found at <see cref="TargetPath"/>) with vanilla
    /// materials from carrier <see cref="CarrierGuid"/>. Used for the sculpted cave shell, whose wall bands, floor
    /// and ceiling each borrow a different vanilla cave material. Like the other décor paints it targets an object
    /// excluded from the content hash, so the mesh is free to change without a rehash. Absent target is a success
    /// with zero applied (optional décor).
    /// <para><b>Matched by name, never by index.</b> Unity's FBX importer orders sub-meshes by the order faces
    /// first use each material, <i>not</i> by the authoring tool's material-slot order — so a re-sculpt that
    /// merely reorders faces silently permutes the sub-mesh indices. Binding each borrow to the placeholder
    /// material the authored mesh already carries makes the paint independent of that order; keeping the
    /// placeholders aligned with the imported sub-meshes is the authoring pipeline's job.</para>
    /// </summary>
    public sealed record SubmeshBorrow(
        string TargetPath,
        string CarrierGuid,
        IReadOnlyList<SubmeshMaterialRule> Rules);

    /// <summary>
    /// A piece of vanilla room scenery to clone into the arena. Where the material borrows take a <i>material</i>
    /// off a donor room and put it on our own mesh, this takes a whole authored <i>subtree</i> — mesh, sub-objects
    /// and all — and places a copy of it at each marker the arena authored for it. The prop's own materials come
    /// with it, so nothing needs borrowing.
    /// </summary>
    /// <remarks>
    /// Like the other décor this is targeted by naming convention and excluded from the content hash: markers are
    /// empty objects the author places freely, so a prop can be moved, turned or duplicated without a rehash or a
    /// re-export. Each marker owns its clone's position, rotation and scale; the clone keeps the source's own scale
    /// as its base, so a marker at scale 1 reproduces the prop at its vanilla proportions.
    /// <para><b>A vanilla prop is not automatically safe to drop in.</b> It carries the layers, colliders, triggers
    /// and gameplay components the donor room needed, and two of those matter here: a prop left on a layer the
    /// navigation scan rasterizes silently becomes terrain, and a gameplay component cloned along with it starts
    /// running in a room it was not written for. So the recipe names what to remove and which layer the whole
    /// subtree ends up on, and the implementation must apply both <i>before</i> the clone ever becomes active —
    /// a stripped component must never have run its lifecycle.</para>
    /// </remarks>
    /// <param name="ParentPath">Where the markers live in the realized arena.</param>
    /// <param name="MarkerNamePrefix">Markers whose name starts with this get a clone; anything else is left alone.</param>
    /// <param name="RoomKey">The donor room's <em>runtime</em> addressable key. Discovered from the running game
    /// (the reverse-engineered export's GUIDs are not the game's keys), so it is a pinned constant here.</param>
    /// <param name="PropPath">The prop's path inside the donor room — the clone selector.</param>
    /// <param name="StripChildNames">Child objects removed from the clone, by name, at any depth.</param>
    /// <param name="StripComponentNames">Components removed from the clone, by type name, at any depth.</param>
    /// <param name="LayerName">The layer the whole clone subtree is moved to. A name, never an index — indices are
    /// not stable across builds.</param>
    public sealed record VanillaPropClone(
        string ParentPath,
        string MarkerNamePrefix,
        string RoomKey,
        string PropPath,
        IReadOnlyList<string> StripChildNames,
        IReadOnlyList<string> StripComponentNames,
        string LayerName);

    /// <summary>The outcome of cloning one prop recipe: how many clones were placed, or the fail-closed reason.</summary>
    public sealed record VanillaPropResult(bool Success, string? Error, int Cloned)
    {
        public static VanillaPropResult Placed(int cloned) => new VanillaPropResult(true, null, cloned);

        public static VanillaPropResult Failed(string error) => new VanillaPropResult(false, error, 0);
    }

    /// <summary>The outcome of resolving the material borrows: how many were applied, or the fail-closed reason.
    /// Failure is an outcome, not an exception — the load flow tears down on it.</summary>
    public sealed record MaterialBorrowResult(bool Success, string? Error, int Applied)
    {
        public static MaterialBorrowResult Resolved(int applied) => new MaterialBorrowResult(true, null, applied);

        public static MaterialBorrowResult Failed(string error) => new MaterialBorrowResult(false, error, 0);
    }

    /// <summary>
    /// Resolves the arena's borrowed vanilla materials — loads each donor carrier from the player's own install
    /// via Addressables, finds the named material on it, and assigns it onto our own realized renderer — and
    /// releases every handle on teardown (Docs/MaterialCompatibilityReport.md §3.1, boss #1 roadmap P1,
    /// direction B).
    /// </summary>
    /// <remarks>
    /// Declared here — the innermost consumer is the arena load flow — and implemented in
    /// <c>FalseGods.Integration.Sulfur</c>, the only module that may operate Addressables directly. Like
    /// <see cref="INavigationPort"/>, the realized arena reaches the implementation by composition-time injection
    /// (the Composition Root hands it the realized-root accessor), never through this signature — Application has
    /// no Unity reference.
    /// <para>
    /// Borrowed materials are pure presentation: the resolver must never take collision, navigation, spawns, or
    /// any authoritative state from the donor carrier — those stay with our authored content (host authority,
    /// single ownership). A carrier that fails to load, a material name that resolves to zero or more than one
    /// distinct material, or a target path that is absent is a fail-closed <see cref="MaterialBorrowResult"/>,
    /// never a partial paint. <see cref="Release"/> releases every Addressables handle and is idempotent; it is
    /// called after the realized hierarchy is torn down, so no live renderer still references a released material.
    /// </para>
    /// </remarks>
    public interface IVanillaAssetProvider
    {
        MaterialBorrowResult Resolve(IReadOnlyList<MaterialBorrowRequest> requests);

        /// <summary>Paint hand-authored decoration renderers matched by naming convention (see
        /// <see cref="MaterialConventionPaint"/>). Zero matches is a success with zero applied, not a failure — the
        /// decoration is optional. Shares the carrier cache and <see cref="Release"/> lifetime with
        /// <see cref="Resolve"/>. A carrier that will not load or a material name that resolves to zero or more than
        /// one distinct material is fail-closed, exactly as in <see cref="Resolve"/>.</summary>
        MaterialBorrowResult PaintByConvention(MaterialConventionPaint paint);

        /// <summary>Paint the sub-materials of one décor renderer with a list of vanilla materials (see
        /// <see cref="SubmeshBorrow"/>). An absent target is a success with zero applied (optional décor). A
        /// carrier that will not load or a material name that resolves to zero or more than one distinct material
        /// is fail-closed, exactly as in <see cref="Resolve"/>. Shares the carrier cache and <see cref="Release"/>
        /// lifetime with the other paints.</summary>
        MaterialBorrowResult PaintSubmeshes(SubmeshBorrow borrow);

        /// <summary>Clone one piece of vanilla scenery onto every marker the arena authored for it (see
        /// <see cref="VanillaPropClone"/>). No markers — or no marker parent at all — is a success with zero
        /// clones: the décor is optional. A donor room that will not load, or a prop path, layer name or marker
        /// that does not resolve, is fail-closed, exactly as in <see cref="Resolve"/>: an arena missing the
        /// scenery it asked for should say so rather than quietly ship without it. Shares the donor cache and
        /// <see cref="Release"/> lifetime with the paints; the clones themselves belong to the realized
        /// hierarchy and die with it.</summary>
        VanillaPropResult CloneProps(VanillaPropClone request);

        void Release();
    }
}
