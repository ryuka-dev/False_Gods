namespace FalseGods.Core.Arena.Events
{
    /// <summary>
    /// The marker for every discrete, authoritative arena-domain event.
    /// </summary>
    /// <remarks>
    /// The arena counterpart of the boss's <c>IBossDomainEvent</c>, and deliberately a <b>separate</b> stream: a
    /// boss reusable across arenas cannot carry one arena's mechanism vocabulary, so arena transitions are never
    /// boss events (Docs/Architecture.md §5, Docs/ADRs/ADR-005). These are project-owned domain events — not wire
    /// DTOs, not presentation contracts. On a host, <c>FalseGods.Application</c> maps them onto the reliable
    /// <c>ArenaEvent</c> stream and onto <c>PresentationEvent</c>s; Core knows neither.
    /// </remarks>
    public interface IArenaDomainEvent
    {
    }
}
