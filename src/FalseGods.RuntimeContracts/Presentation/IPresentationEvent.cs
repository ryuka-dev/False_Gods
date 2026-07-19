namespace FalseGods.RuntimeContracts.Presentation
{
    /// <summary>
    /// The marker for a discrete presentation cue — something that happens once and triggers a one-shot visual
    /// (a telegraph starting, an impact, a hit flash, a death, an arena mechanism engaging).
    /// </summary>
    /// <remarks>
    /// The presentation-side counterpart of Core's domain events, but a different vocabulary: these carry
    /// only what a renderer needs, never a wire concern (Docs/Architecture.md §7). Continuous state (pose, phase,
    /// health) travels in <see cref="PresentationState"/>, not here — only genuinely discrete cues are events
    /// (Docs/ADRs/ADR-005). The mapper in <c>FalseGods.Application</c> produces them; presentation consumes them and
    /// decides nothing authoritative. A pure marker: boss cues carry their <c>BossInstanceId</c> on the concrete
    /// record, and arena cues carry none — renderers dispatch on the concrete type.
    /// </remarks>
    public interface IPresentationEvent
    {
    }
}
