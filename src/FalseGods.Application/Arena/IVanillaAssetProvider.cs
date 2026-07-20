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

        void Release();
    }
}
