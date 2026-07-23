namespace FalseGods.Application.Arena
{
    /// <summary>
    /// The "load our arena AS the level" seam (Strategy A). Instead of overlaying an additive arena onto a live
    /// level — which fail-closes when that level's navigation is not scanned at the raise site (the Strategy B
    /// limit on a large boss arena) — this drives the game's OWN level load so our cave IS the level. The game then
    /// generates the level, scans navigation, and spawns the player natively, sidestepping the manual rescan.
    /// </summary>
    /// <remarks>
    /// The implementation lives in <c>Integration.Sulfur</c> (which references <c>Application</c>): it calls the
    /// game's level-load entry point for the first cave environment. This is a development trigger for now — the
    /// player-facing entry (a developer-menu button) is a later concern. Loading the level is the game's job; what
    /// content that level contains is decided by a separate generation hook (added incrementally).
    /// </remarks>
    public interface IArenaHijackPort
    {
        /// <summary>Load the boss arena as the first cave level, through the game's own level generation (native
        /// generation, navigation, and player spawn). A missing game manager is a no-op.</summary>
        void LoadHijackedArena();
    }
}
