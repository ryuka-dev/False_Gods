using System.Collections.Generic;

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
    /// <param name="Explosive">Whether this one is a barrel that goes off. Sent rather than rolled on each peer:
    /// how often the room produces one climbs as the fight turns, so two peers rolling their own would disagree
    /// exactly at the moments the rate changes — and disagreeing about which crate is a bomb is not cosmetic.</param>
    public sealed record CrateDropped(WorldPosition At, int PileKind, int PileIndex, bool Explosive);

    /// <summary>
    /// Host → all peers, reliable-ordered: a carrier standing at <see cref="At"/> picked <see cref="Count"/>
    /// destructibles up off a heap, reaching <see cref="Radius"/> around itself.
    /// </summary>
    /// <remarks>
    /// The half of a carry that <i>removes</i> supply. A peer with no carriers of its own — every client, since
    /// the host owns them — would otherwise watch its production points fill up forever while its boss went
    /// hungry, because nothing there ever collects. Sent as a place and a number rather than as crate identities:
    /// the peers built their crates from the same commands in the same order, so taking the same count from the
    /// same heap at the same spot leaves the same piles behind.
    /// </remarks>
    public sealed record CratesTaken(WorldPosition At, int PileKind, int PileIndex, int Count, float Radius);

    /// <summary>
    /// Host → all peers, reliable-ordered: a load of <see cref="Count"/> destructibles was thrown from
    /// <see cref="From"/> down into a ring around <see cref="At"/>, laid out from <see cref="Seed"/>.
    /// </summary>
    /// <remarks>
    /// The half of a carry that <i>adds</i> supply — a delivery beside the boss, or a load bursting out of a
    /// carrier that was killed holding it, which is why the pile it lands on travels with it. The seed is the
    /// whole layout: every peer rings the same crates around the same spot from it, so no crate position is sent.
    /// </remarks>
    /// <param name="Explosives">How many of the load were barrels that go off. Sent because it is the one thing
    /// about a load a peer cannot work out for itself — the ring's positions come from the seed, but what a
    /// carrier was holding was decided when it collected, and a player who watched it walk is owed the same
    /// contents on the ground.</param>
    public sealed record CratesSetDown(
        WorldPosition From, WorldPosition At, int PileKind, int PileIndex, int Count, int Seed, int Explosives);

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
    /// <summary>One player's pair of threatened spots inside a volley: where they stand, and where they are
    /// predicted to be when the crates arrive.</summary>
    /// <remarks>
    /// Both travel rather than being recomputed per peer: a client cannot read another player's velocity, so the
    /// prediction is the host's to make — like the seed.
    /// </remarks>
    public sealed record CrateVolleyTarget(WorldPosition Current, WorldPosition Lead);

    /// <summary>
    /// Host → all peers, reliable-ordered: the destructible numbered <see cref="CrateId"/> is gone, and how.
    /// </summary>
    /// <remarks>
    /// <para><b>A number, not a place.</b> Every peer builds the same destructibles from the same commands in the
    /// same order, so the <i>n</i>th one made is the same crate on all of them — which is the identity a session
    /// layer matching by spawn position cannot have, since ours are all heaped in the same few spots.</para>
    /// <para>A peer that no longer has that crate does nothing. Piles are settled by physics rather than by the
    /// commands alone, so two peers can disagree about which crate a carrier picked up; an unknown number is that
    /// disagreement showing, and destroying nothing is the right answer to it.</para>
    /// </remarks>
    /// <param name="Death">How it died, as <c>CrateDeath</c>'s numbering. Like the pile, it travels as a plain
    /// number rather than as the domain type: it arrives from another machine, so it is a claim to be checked
    /// before it becomes a cause of death (Docs/DependencyRules.md §12).</param>
    public sealed record CrateDestroyed(int CrateId, int Death);

    /// <summary>
    /// Client → host, reliable-ordered: this peer's player destroyed the destructible numbered
    /// <see cref="CrateId"/>.
    /// </summary>
    /// <remarks>
    /// A request, not a statement, like a client's hits on the boss: what happens in the shared world is the
    /// host's to settle, and it answers with a <see cref="CrateDestroyed"/> that every peer including this one
    /// acts on. It matters beyond consistency — with shared loot on, a client's own loot roll is suppressed by the
    /// session layer, so a crate a client broke by itself would pay nobody. Broken on the host, it pays properly
    /// and the pickup mirrors back down.
    /// </remarks>
    public sealed record CrateDestroyRequested(int CrateId, int Death);

    public sealed record CrateVolleyFired(
        IReadOnlyList<CrateVolleyTarget> Targets,
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
