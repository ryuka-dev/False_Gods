namespace FalseGods.Core.Bosses
{
    /// <summary>
    /// The two attacks the test boss can perform.
    /// </summary>
    /// <remarks>
    /// The PoC test boss has one aimed projectile attack and one area telegraph attack
    /// (Docs/MinimalProofOfConceptPlan.md §7.6.1). Both are host-authoritative: the host selects the kind, aims
    /// it, telegraphs it, and commits it; client presentation only visualises what it is told and decides no
    /// damage (Docs/ADRs/ADR-003, Docs/MinimalProofOfConceptPlan.md B4).
    /// </remarks>
    public enum BossAttackKind
    {
        /// <summary>A projectile aimed at the target's position at the moment the attack was selected.</summary>
        AimedProjectile = 1,

        /// <summary>An area-of-effect attack telegraphed on the ground before it lands.</summary>
        AreaTelegraph = 2,
    }
}
