namespace FalseGods.Core.Bosses.Events
{
    /// <summary>
    /// The marker for every discrete, authoritative boss-domain event.
    /// </summary>
    /// <remarks>
    /// These are <b>project-owned domain events</b> (Docs/Architecture.md §3), not wire DTOs and not presentation
    /// contracts. They are the boss simulation's only output besides its queryable state: each one records an
    /// authoritative decision the host made — an attack was telegraphed, damage was applied, the phase changed, the
    /// boss died. Downstream, <c>FalseGods.Application</c> maps them to <c>PresentationState</c>/<c>PresentationEvent</c>
    /// for the renderer and, on a host, to <c>FalseGods.Protocol</c> events for the wire — but Core knows neither of
    /// those vocabularies (Docs/Architecture.md §7).
    ///
    /// <para>
    /// Only <b>discrete</b> facts are events. Continuous state — the boss's position, its current activity, its
    /// health — is read from <see cref="BossSimulation"/> directly and, in multiplayer, carried by unreliable
    /// snapshots rather than events (Docs/ADRs/ADR-005).
    /// </para>
    /// </remarks>
    public interface IBossDomainEvent
    {
        /// <summary>The boss instance this event is about.</summary>
        BossInstanceId Boss { get; }
    }
}
