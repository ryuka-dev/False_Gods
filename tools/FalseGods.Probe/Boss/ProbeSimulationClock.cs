using FalseGods.Core.Simulation;

namespace FalseGods.Probe.Boss
{
    /// <summary>
    /// A probe stand-in for the host <see cref="ISimulationClock"/>, advanced by the plugin's Unity update.
    /// </summary>
    /// <remarks>
    /// In single-player the boss runs on the local host, so "host simulation time" is just the local game clock.
    /// The probe accumulates each frame's <c>Time.deltaTime</c> into <see cref="Time"/> (so it respects a game
    /// pause and only ever moves forward) and counts frames into <see cref="Tick"/>. Feeding the real
    /// <c>BossSimulation</c> off this is exactly the B3 property under test: telegraph/commit timings derive from
    /// simulation time, not from any per-frame heuristic (Docs/MinimalProofOfConceptPlan.md B3, Docs/ADRs/ADR-003).
    /// It is throwaway probe wiring; the production single-player clock lives in an outer adapter.
    /// </remarks>
    internal sealed class ProbeSimulationClock : ISimulationClock
    {
        public long Tick { get; private set; }

        public float Time { get; private set; }

        /// <summary>Advance the clock by one frame. <paramref name="deltaSeconds"/> is clamped non-negative.</summary>
        public void Advance(float deltaSeconds)
        {
            if (deltaSeconds > 0f)
            {
                Time += deltaSeconds;
            }

            Tick++;
        }
    }
}
