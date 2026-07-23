using FalseGods.Application.Arena;
using ILogger = FalseGods.RuntimeContracts.Diagnostics.ILogger;

namespace FalseGods.Integration.Sulfur.Navigation
{
    /// <summary>
    /// The <see cref="INavigationPort"/> for an arena that <i>is</i> the level (Strategy A): navigation is the
    /// level's own, so there is nothing for the arena load to apply or take back.
    /// </summary>
    /// <remarks>
    /// When the arena arrives as the level's start area, the game's own navigation step scans the recast graph
    /// over it — the arena floor is a mesh on the Geometry layer, which is exactly what that scan rasterizes. The
    /// additive port is not merely unnecessary here, it is wrong: it fails closed when the live level's
    /// navigation is not scanned at the point it is asked to apply, which is precisely the state during a level
    /// load. Reporting a successful no-op keeps the one arena load sequence intact — the flow still runs the same
    /// steps in the same order — while leaving the level's navigation to the level.
    /// </remarks>
    public sealed class NativeLevelNavigationPort : INavigationPort
    {
        private readonly ILogger? _logger;

        public NativeLevelNavigationPort(ILogger? logger = null)
        {
            _logger = logger;
        }

        public NavigationApplyResult Apply()
        {
            _logger?.Log("[nav] arena navigation left to the level's own scan (native level load).");
            // No nodes were applied by us; the count is a diagnostic of this port's contribution, not of the graph.
            return NavigationApplyResult.Applied(0);
        }

        public void Remove()
        {
        }
    }
}
