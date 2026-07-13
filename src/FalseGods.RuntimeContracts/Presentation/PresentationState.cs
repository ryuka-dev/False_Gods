using FalseGods.Core.Bosses;
using FalseGods.Core.Simulation;

namespace FalseGods.RuntimeContracts.Presentation
{
    /// <summary>
    /// The complete continuous visual state of the boss for one frame: where it is, what it is doing, and how a
    /// renderer should show it.
    /// </summary>
    /// <remarks>
    /// This is a <b>project-owned, transport-agnostic presentation contract</b> (Docs/Architecture.md §7). It
    /// carries what a renderer needs — pose, activity, phase visual id, weak-point visual state, a health fraction
    /// for a bar — and nothing a socket needs: no sequence numbers, no protocol version, no delivery mode. Both the
    /// single-player/host path (Core domain state) and the multiplayer client path (wire snapshots) are mapped into
    /// this same type, so <see cref="IEncounterPresentation"/> cannot tell which it is driven by — which is the
    /// point.
    ///
    /// <para>
    /// It reuses Core value types (<see cref="SimVector2"/>, <see cref="BossInstanceId"/>) for pure data, but its
    /// <em>visual</em> axes are presentation-owned enums (<see cref="BossVisualActivity"/>) and a plain
    /// <see cref="PhaseVisualId"/>, never the domain's or the wire's own enums.
    /// </para>
    /// </remarks>
    public sealed record PresentationState(
        BossInstanceId Boss,
        SimVector2 Position,
        SimVector2 Facing,
        int PhaseVisualId,
        BossVisualActivity Activity,
        bool WeakPointExposed,
        float HealthFraction);
}
