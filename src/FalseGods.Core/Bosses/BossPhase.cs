namespace FalseGods.Core.Bosses
{
    /// <summary>
    /// Which of the test boss's two phases is active.
    /// </summary>
    /// <remarks>
    /// The PoC test boss has exactly two phases (Docs/MinimalProofOfConceptPlan.md §7.6.1). The phase is an
    /// authoritative boss-domain fact: the host decides the transition and replicates it, and the
    /// <c>EncounterCoordinator</c> is what turns a phase change into an arena mechanism change — the boss never
    /// touches the arena directly (Docs/Architecture.md §5).
    /// </remarks>
    public enum BossPhase
    {
        One = 1,
        Two = 2,
    }
}
