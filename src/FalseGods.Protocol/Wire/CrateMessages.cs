namespace FalseGods.Protocol.Wire
{
    /// <summary>
    /// Host → all peers, reliable-ordered: put one destructible on the ground at <see cref="At"/> and let physics
    /// have it.
    /// </summary>
    /// <remarks>
    /// <para><b>The command travels, not the crate.</b> These destructibles are assembled at runtime from the
    /// player's own installed content, so there is no shipped prefab for a session layer to spawn on a client, and
    /// nothing to match one against. What every peer <i>does</i> have is the same assembly recipe — so each peer
    /// is told what was done and builds its own, exactly as it builds its own copy of the arena.</para>
    /// <para>That is only sound because the mechanic is a pure function of its inputs: a volley's scatter, its
    /// hold, and which crates lead the target all come from a seed rather than from live randomness, so identical
    /// inputs produce identical crates on every peer without a single position ever crossing the wire.</para>
    /// </remarks>
    /// <param name="PileKind">Which kind of heap the crate joins, as <c>CratePileKind</c>'s numbering.</param>
    /// <param name="PileIndex">Which heap of that kind, indexing the room's authored group.</param>
    /// <remarks>
    /// The pile travels as two plain numbers rather than as the domain type: it arrives from another machine, so
    /// it is a claim to be checked before it becomes a pile, not a pile (Docs/DependencyRules.md §12). Without it
    /// a client could not tell a crate produced at a source from one delivered to the boss, and the two peers
    /// would fire different crates from the same volley command.
    /// </remarks>
    public sealed record CrateDropped(WorldPosition At, int PileKind, int PileIndex);

    /// <summary>
    /// Host → all peers, reliable-ordered: throw one destructible from <see cref="From"/> so it lands on
    /// <see cref="To"/> after <see cref="FlightSeconds"/>, arcing <see cref="ApexHeight"/> over the straight line.
    /// </summary>
    public sealed record CrateThrown(
        WorldPosition From,
        WorldPosition To,
        float FlightSeconds,
        float ApexHeight);

    /// <summary>
    /// Host → all peers, reliable-ordered: fire a volley off the pile named by <see cref="PileKind"/> and
    /// <see cref="PileIndex"/>. Every field is an input to the shared computation — <see cref="Seed"/> above all —
    /// so this is the whole volley, not a description of one.
    /// </summary>
    public sealed record CrateVolleyFired(
        WorldPosition CurrentCenter,
        WorldPosition LeadCenter,
        int PileKind,
        int PileIndex,
        int Seed,
        int Count,
        float SpreadMinRadius,
        float SpreadMaxRadius,
        float LiftHeight,
        float LiftSeconds,
        float HoldSeconds,
        float FlightSeconds,
        float ApexHeight,
        float LeadShare,
        float FireIntervalSeconds);
}
