namespace FalseGods.RuntimeContracts.Presentation
{
    /// <summary>
    /// What the boss should currently look like it is doing — the animation-state axis a renderer switches on.
    /// </summary>
    /// <remarks>
    /// This is a <b>presentation</b> vocabulary, deliberately distinct from Core's <c>BossActivity</c> (the sim's
    /// authoritative state machine) and from any future wire enum (Docs/Architecture.md §7). Keeping it separate is
    /// the whole point of the presentation seam: the same <see cref="IEncounterPresentation"/> is fed by the local
    /// simulation in single-player/host and by remote wire snapshots on a client, each through its own mapper, and
    /// neither of those source enums is allowed to dictate the renderer's states. Today it mirrors the sim's cycle
    /// one-to-one; it is free to diverge (blend states, sub-states) without touching the domain or the wire.
    /// </remarks>
    public enum BossVisualActivity
    {
        Idle = 0,
        Telegraphing = 1,
        Committing = 2,
        Recovering = 3,
        Dead = 4,
    }
}
