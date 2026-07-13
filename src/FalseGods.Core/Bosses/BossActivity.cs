namespace FalseGods.Core.Bosses
{
    /// <summary>
    /// What the boss is doing right now — the state of its attack/activity machine.
    /// </summary>
    /// <remarks>
    /// This is authoritative boss state, advanced on the host against <see cref="Simulation.ISimulationClock"/>.
    /// The cycle is <see cref="Idle"/> → <see cref="Telegraphing"/> → <see cref="Committing"/> →
    /// <see cref="Recovering"/> → <see cref="Idle"/>. <see cref="Recovering"/> is the window in which the boss's
    /// weak point is exposed (Docs/MinimalProofOfConceptPlan.md §7.6.1, "one weak-point or stagger state").
    /// <see cref="Dead"/> is terminal.
    /// </remarks>
    public enum BossActivity
    {
        /// <summary>Waiting between attacks; the boss moves toward its target and decides nothing else.</summary>
        Idle = 0,

        /// <summary>An attack has been selected and telegraphed; the boss is committed to that attack instance.</summary>
        Telegraphing = 1,

        /// <summary>The telegraph has elapsed and the attack lands; the authoritative effect happens here.</summary>
        Committing = 2,

        /// <summary>Post-attack recovery; the weak point is exposed and damage is amplified.</summary>
        Recovering = 3,

        /// <summary>The boss is dead. Terminal — it neither moves nor attacks.</summary>
        Dead = 4,
    }
}
