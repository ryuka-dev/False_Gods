using FalseGods.Core.Arena;
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

    /// <summary>
    /// The boss started, or stopped, being enraged — roar, and switch the look.
    /// </summary>
    /// <remarks>
    /// The same division as <see cref="WeakPointVisibilityChanged"/>: how an enraged boss <i>looks</i> is
    /// continuous and carried by <see cref="PresentationState.Enraged"/>, so a renderer that missed this cue still
    /// shows the right boss; this is the one-shot the change is <i>played</i> with, which is where the roar goes.
    /// </remarks>
    public sealed record RageChanged(BossInstanceId Boss, bool Enraged) : IPresentationEvent;

    /// <summary>
    /// The fight began — the boss announces itself, and the room opens around the players.
    /// </summary>
    /// <remarks>
    /// One-shot, and the only cue there is for the opening: a renderer that missed it shows a boss simply standing
    /// there, which is what the boss looks like either way. What the cue buys is the <i>ceremony</i> — the roar,
    /// the fog pulling back, the music — so it is played rather than derived from state.
    /// </remarks>
    public sealed record BossRoared(BossInstanceId Boss) : IPresentationEvent;

    /// <summary>The boss was defeated — play the death. Terminal, like its domain counterpart.</summary>
    public sealed record BossDefeated(BossInstanceId Boss) : IPresentationEvent;

    /// <summary>The boss is now standing somewhere else — the cue a relocation is shown with.</summary>
    public sealed record BossMoved(
        BossInstanceId Boss,
        SimVector2 Position,
        float PositionHeight) : IPresentationEvent;

    // Arena cues. Deliberately different names from the Core domain events (MechanismGroupActivated,
    // ArenaExitUnlocked) so a file mapping between the two vocabularies never needs alias gymnastics — the same
    // convention that pairs domain BossDied with presentation BossDefeated.

    /// <summary>An arena mechanism group switched on — show the group's active visual state.</summary>
    public sealed record MechanismGroupEngaged(MechanismGroupId Group) : IPresentationEvent;

    /// <summary>The arena exit unlocked — show the way out (the boss is defeated).</summary>
    public sealed record ExitOpened : IPresentationEvent;
}
