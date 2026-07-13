using System.Collections.Generic;

namespace FalseGods.Core.Simulation
{
    /// <summary>
    /// The boss's read-only view of who is in the encounter and where they are.
    /// </summary>
    /// <remarks>
    /// The third of Core's permitted ports (Docs/Architecture.md §6): the boss calls it to choose a target and to
    /// aim. It is read-only by design — the boss <b>observes</b> participants, it never owns or mutates the roster
    /// (Docs/DependencyRules.md §3, Docs/Architecture.md §9). The real implementation lives in an outer adapter
    /// (<c>Integration.Sulfur</c> single-player, the ST adapter in multiplayer) and projects the game's players
    /// onto <see cref="ParticipantId"/> / <see cref="SimVector2"/>; Core never sees a game or transport type.
    /// </remarks>
    public interface IEncounterParticipantQuery
    {
        /// <summary>
        /// The participants currently eligible to be targeted. May be empty (everyone left, or none has spawned
        /// yet); the boss treats an empty roster as "no target" and idles rather than attacking nothing.
        /// </summary>
        IReadOnlyList<ParticipantId> Participants { get; }

        /// <summary>
        /// The current position of <paramref name="participant"/>, or <c>false</c> when it is no longer known
        /// (the participant left between being listed and being queried).
        /// </summary>
        bool TryGetPosition(ParticipantId participant, out SimVector2 position);
    }
}
