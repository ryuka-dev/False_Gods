using FalseGods.Core.Simulation;

namespace FalseGods.Core.Bosses.Events
{
    /// <summary>The boss became active in the encounter, at full health in phase one.</summary>
    public sealed record BossSpawned(BossInstanceId Boss, BossPhase Phase, int Health) : IBossDomainEvent;

    /// <summary>
    /// The host selected an attack and began telegraphing it. Carries the <see cref="AttackInstanceId"/> that every
    /// later event for this attack repeats, so the effect applies exactly once (Docs/MinimalProofOfConceptPlan.md
    /// B2/B6). <see cref="AimPoint"/> is fixed at selection time — an aimed projectile is committed to where the
    /// target was, not where it ends up.
    /// </summary>
    public sealed record AttackTelegraphed(
        BossInstanceId Boss,
        AttackInstanceId Attack,
        BossAttackKind Kind,
        SimVector2 AimPoint,
        float TelegraphSeconds) : IBossDomainEvent;

    /// <summary>
    /// The telegraph elapsed and the attack landed. This is the authoritative "the attack happened" fact; client
    /// presentation shows it but decides no damage (Docs/MinimalProofOfConceptPlan.md B4).
    /// </summary>
    public sealed record AttackCommitted(
        BossInstanceId Boss,
        AttackInstanceId Attack,
        BossAttackKind Kind,
        SimVector2 AimPoint) : IBossDomainEvent;

    /// <summary>
    /// The boss's weak point opened (post-attack recovery began) or closed (recovery ended). Damage taken while it
    /// is open is amplified by <see cref="BossDefinition.WeakPointDamageMultiplier"/>.
    /// </summary>
    public sealed record WeakPointExposed(BossInstanceId Boss, bool Exposed) : IBossDomainEvent;

    /// <summary>The host advanced the boss to a new phase. The <c>EncounterCoordinator</c> reacts by driving the arena.</summary>
    public sealed record BossPhaseChanged(BossInstanceId Boss, BossPhase Phase) : IBossDomainEvent;

    /// <summary>
    /// The host applied damage. <see cref="Amount"/> is the amount actually dealt after the weak-point multiplier,
    /// <see cref="RemainingHealth"/> is the health left, and <see cref="WeakPointHit"/> records whether the hit
    /// landed on the exposed weak point.
    /// </summary>
    public sealed record BossDamaged(
        BossInstanceId Boss,
        int Amount,
        int RemainingHealth,
        bool WeakPointHit) : IBossDomainEvent;

    /// <summary>
    /// The boss's health fell far enough to move it to the next station of its itinerary, and it is now standing
    /// at <see cref="AnchorIndex"/>. The authoritative "the boss is somewhere else now" fact: the position it
    /// carries is where the boss <i>is</i>, not where it is heading.
    /// </summary>
    /// <remarks>
    /// A discrete event rather than something to notice from the position stream, because it is a decision: it is
    /// what a telegraphed vanish/appear will be hung on, and what tells a client this is a relocation rather than
    /// a lost snapshot. One hit big enough to cross several stations produces one event, for the station it
    /// ended at.
    /// </remarks>
    public sealed record BossRelocated(
        BossInstanceId Boss,
        int StationIndex,
        int AnchorIndex,
        SimVector2 Position,
        float Height) : IBossDomainEvent;

    /// <summary>The boss's health reached zero. Terminal — no further events follow for this instance.</summary>
    public sealed record BossDied(BossInstanceId Boss) : IBossDomainEvent;
}
