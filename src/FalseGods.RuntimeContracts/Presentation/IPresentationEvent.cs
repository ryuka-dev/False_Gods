using FalseGods.Core.Bosses;

namespace FalseGods.RuntimeContracts.Presentation
{
    /// <summary>
    /// The marker for a discrete presentation cue — something that happens once and triggers a one-shot visual
    /// (a telegraph starting, an impact, a hit flash, a death).
    /// </summary>
    /// <remarks>
    /// The presentation-side counterpart of Core's <c>IBossDomainEvent</c>, but a different vocabulary: these carry
    /// only what a renderer needs, never a wire concern (Docs/Architecture.md §7). Continuous state (pose, phase,
    /// health) travels in <see cref="PresentationState"/>, not here — only genuinely discrete cues are events
    /// (Docs/ADRs/ADR-005). The mapper in <c>FalseGods.Application</c> produces them; presentation consumes them and
    /// decides nothing authoritative.
    /// </remarks>
    public interface IPresentationEvent
    {
        /// <summary>The boss instance this cue is about.</summary>
        BossInstanceId Boss { get; }
    }
}
