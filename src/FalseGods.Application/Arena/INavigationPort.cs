namespace FalseGods.Application.Arena
{
    /// <summary>The outcome of applying the arena's navigation: a diagnostic walkable-node count on success, or
    /// an error. Failure is an outcome, not an exception — the load flow fails closed on it.</summary>
    public sealed record NavigationApplyResult(bool Success, string? Error, int WalkableNodesApplied)
    {
        public static NavigationApplyResult Applied(int walkableNodes) => new NavigationApplyResult(true, null, walkableNodes);

        public static NavigationApplyResult Failed(string error) => new NavigationApplyResult(false, error, 0);
    }

    /// <summary>
    /// Makes the realized arena's floor walkable in the active level's navigation and removes that contribution
    /// on teardown (ADR-002, Docs/CollisionAndNavigationProposal.md §4.3 Option 1 / §4.6).
    /// </summary>
    /// <remarks>
    /// Declared here — the innermost consumer is the arena load flow — and implemented in
    /// <c>FalseGods.Integration.Sulfur</c>, the only module that may operate the A* Pathfinding Project
    /// directly. The implementation owns the whole measured P7 discipline: bake the arena navmesh from a copy in
    /// clear space (a live rescan does not rasterize our meshes), snapshot the level's own tiles in the footprint
    /// (geometry <b>and</b> per-node walkability) before overwriting them, apply with <c>ReplaceTiles</c>, and on
    /// <see cref="Remove"/> restore the snapshot exactly — never a bare <c>ClearTiles</c>, which would leave a
    /// hole in the level players are still standing in (RiskList R8/R30).
    /// <para>
    /// The arena geometry reaches the implementation by composition-time injection (the Composition Root hands it
    /// a Unity-level source), never through this signature — Application has no Unity reference. A failed
    /// <see cref="Apply"/> leaves the live graph untouched. <see cref="Remove"/> is idempotent and safe to call
    /// when nothing was applied.
    /// </para>
    /// </remarks>
    public interface INavigationPort
    {
        NavigationApplyResult Apply();

        void Remove();
    }
}
