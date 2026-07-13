namespace FalseGods.Core.Simulation
{
    /// <summary>
    /// The host simulation clock the boss domain advances against.
    /// </summary>
    /// <remarks>
    /// This is one of the three ports Core is allowed to declare, because the domain logic itself reads it
    /// (Docs/Architecture.md §6). It is <b>not</b> <c>UnityEngine.Time</c>: Core is Unity-less, and tying visual
    /// timing to host simulation time — rather than each machine's frame time — is exactly what makes attack
    /// telegraphs agree across peers (Docs/ADRs/ADR-003, Docs/MinimalProofOfConceptPlan.md B3).
    ///
    /// <para>
    /// <see cref="Time"/> is authoritative host simulation time and only moves forward. The boss reads it on each
    /// advance and derives the elapsed interval itself, so the caller never has to hand it a delta. A fake clock
    /// makes the whole boss deterministically testable without Unity.
    /// </para>
    /// </remarks>
    public interface ISimulationClock
    {
        /// <summary>The monotonically increasing host simulation tick count.</summary>
        long Tick { get; }

        /// <summary>Host simulation time in seconds. Monotonically non-decreasing.</summary>
        float Time { get; }
    }
}
