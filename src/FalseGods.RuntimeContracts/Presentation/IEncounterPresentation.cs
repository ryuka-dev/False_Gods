namespace FalseGods.RuntimeContracts.Presentation
{
    /// <summary>
    /// The single entry point through which the boss is shown, implemented by the renderer in
    /// <c>FalseGods.UnityRuntime</c>.
    /// </summary>
    /// <remarks>
    /// This is <b>the</b> presentation seam (Docs/Architecture.md §7, Docs/ADRs/ADR-003). Single-player, host, and
    /// client all converge on it: each source (local Core simulation, or remote wire snapshots) is mapped to
    /// <see cref="PresentationState"/> / <see cref="IPresentationEvent"/> by <c>FalseGods.Application</c> and then
    /// enters this interface identically — presentation cannot tell the modes apart.
    ///
    /// <para>
    /// It lives in <c>FalseGods.RuntimeContracts</c> so the renderer can implement it without referencing
    /// <c>FalseGods.Protocol</c>; a wire DTO never appears in these signatures. The implementation makes <b>no</b>
    /// authoritative decision — not damage, phase, death, target, or attack outcome — and must be inert when nothing
    /// drives it (RiskList R16/R27): calling neither method advances no state.
    /// </para>
    /// </remarks>
    public interface IEncounterPresentation
    {
        /// <summary>Apply the latest continuous visual state (pose, activity, phase, weak point, health).</summary>
        void Apply(PresentationState state);

        /// <summary>Play a discrete visual cue.</summary>
        void Handle(IPresentationEvent presentationEvent);
    }
}
