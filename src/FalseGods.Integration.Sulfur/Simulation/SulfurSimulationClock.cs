using FalseGods.Core.Simulation;

namespace FalseGods.Integration.Sulfur.Simulation
{
    /// <summary>
    /// The single-player implementation of the boss domain's <see cref="ISimulationClock"/>: host simulation time is
    /// the local game clock.
    /// </summary>
    /// <remarks>
    /// In single-player the boss runs on the local host, so "host simulation time" is exactly the game's own frame
    /// clock (the probe's B0 doc note; Docs/ADRs/ADR-003). This reads <see cref="Time.time"/> and
    /// <see cref="Time.frameCount"/> directly rather than accumulating a delta, so it needs no driving from the
    /// Composition Root and cannot drift from the game: it is monotonically non-decreasing and stops advancing while
    /// the game is paused (both are scaled by <c>Time.timeScale</c>). The boss derives elapsed intervals from
    /// successive reads, so the absolute offset at spawn is irrelevant.
    ///
    /// <para>
    /// This wrapper is the reason Core stays Unity-less: the domain names only <see cref="ISimulationClock"/>, and the
    /// one place that touches <c>UnityEngine.Time</c> is here, inside the game-facing adapter. A host clock for
    /// multiplayer (shared simulation time across peers) would be a different implementation injected by the ST
    /// composition; it is not built here.
    /// </para>
    /// </remarks>
    public sealed class SulfurSimulationClock : ISimulationClock
    {
        public long Tick => UnityEngine.Time.frameCount;

        public float Time => UnityEngine.Time.time;
    }
}
