using FalseGods.Core.Bosses;
using FalseGods.Core.Simulation;

namespace FalseGods.RuntimeContracts.Presentation
{
    /// <summary>The boss became visible in the encounter — play its intro. Initial pose/health arrive via state.</summary>
    public sealed record BossAppeared(BossInstanceId Boss, int PhaseVisualId) : IPresentationEvent;

    /// <summary>
    /// A telegraph began. The renderer draws the wind-up over <see cref="TelegraphSeconds"/> aimed at
    /// <see cref="AimPoint"/>. <see cref="Attack"/> lets presentation correlate this cue with its later
    /// <see cref="AttackLanded"/> and ignore a duplicate (Docs/MinimalProofOfConceptPlan.md B5/B6).
    /// </summary>
    public sealed record AttackTelegraphStarted(
        BossInstanceId Boss,
        AttackInstanceId Attack,
        AttackVisualKind Kind,
        SimVector2 AimPoint,
        float TelegraphSeconds) : IPresentationEvent;

    /// <summary>The attack landed — play the impact at <see cref="AimPoint"/>. Presentation decides no damage.</summary>
    public sealed record AttackLanded(
        BossInstanceId Boss,
        AttackInstanceId Attack,
        AttackVisualKind Kind,
        SimVector2 AimPoint) : IPresentationEvent;

    /// <summary>The boss's weak point opened or closed — show or hide the vulnerable-state visual.</summary>
    public sealed record WeakPointVisibilityChanged(BossInstanceId Boss, bool Exposed) : IPresentationEvent;

    /// <summary>The boss transitioned to a new phase — play the transition and switch to the phase's look.</summary>
    public sealed record PhaseTransition(BossInstanceId Boss, int PhaseVisualId) : IPresentationEvent;

    /// <summary>
    /// The boss took a hit — flash it and, if desired, show <see cref="Amount"/> as a damage number.
    /// <see cref="WeakPointHit"/> lets the renderer emphasise a weak-point hit. This is a visual cue only; the
    /// authoritative health is carried by <see cref="PresentationState.HealthFraction"/>.
    /// </summary>
    public sealed record BossHit(BossInstanceId Boss, int Amount, bool WeakPointHit) : IPresentationEvent;

    /// <summary>The boss was defeated — play the death. Terminal, like its domain counterpart.</summary>
    public sealed record BossDefeated(BossInstanceId Boss) : IPresentationEvent;
}
