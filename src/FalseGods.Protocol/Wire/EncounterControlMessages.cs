using FalseGods.Core.Simulation;
using FalseGods.Protocol.Arena;

namespace FalseGods.Protocol.Wire
{
    /// <summary>Why the host aborted the encounter before it started — the wire form of the fail-closed gate
    /// outcomes (Docs/MultiplayerLoadingContract.md §5.3.1).</summary>
    /// <remarks>
    /// A wire-owned enum: the gate's own reason type lives in <c>FalseGods.Application</c>, which Protocol cannot
    /// reference, so the codec carries this mirror and the application layer maps between the two. Values are part
    /// of the wire contract — never reused or reordered.
    /// </remarks>
    public enum EncounterAbortReason
    {
        Unspecified = 0,
        ContentHashSchemaMismatch = 1,
        VersionMismatch = 2,
        ContentMismatch = 3,
        LoadFailed = 4,
        Timeout = 5,
    }

    /// <summary>
    /// Host → all peers, reliable-ordered: enter the arena named by <see cref="Manifest"/>, realized at the
    /// host-chosen world <see cref="Origin"/> (Docs/MultiplayerLoadingContract.md §5.3 step 1).
    /// </summary>
    /// <remarks>
    /// The origin is host-authoritative so every peer realizes the arena at the same world coordinates — the
    /// level itself is already host-synchronized, so world space is shared, and replicated boss positions stay
    /// world-space. The arena is always realized with identity rotation (the navigation tile grid is axis-aligned;
    /// a rotated arena would need its own design pass). Every field is untrusted input on receipt (§5.2).
    /// </remarks>
    public sealed record EnterArena(
        EncounterId Encounter,
        ArenaManifest Manifest,
        WorldPosition Origin);

    /// <summary>
    /// Peer → host, reliable-ordered: this peer realized and validated its arena content; <see cref="Manifest"/>
    /// is the peer's <b>own</b> locally-computed identity, which the host's gate compares against its own
    /// (Docs/MultiplayerLoadingContract.md §5.3 step 4).
    /// </summary>
    public sealed record ArenaReady(
        EncounterId Encounter,
        ArenaManifest Manifest);

    /// <summary>
    /// Peer → host, reliable-ordered: this peer failed to load or validate the arena. The gate aborts the
    /// encounter for everyone — there is no partial start (Docs/MultiplayerLoadingContract.md §5.3.1).
    /// <see cref="Reason"/> is diagnostic text, never program input.
    /// </summary>
    public sealed record ArenaLoadFailed(
        EncounterId Encounter,
        string Reason);

    /// <summary>
    /// Host → all peers, reliable-ordered: the encounter was aborted before it started (gate failure). Every
    /// peer tears its arena down and returns to the pre-arena state (Docs/MultiplayerLoadingContract.md §5.3.1).
    /// </summary>
    public sealed record EncounterAborted(
        EncounterId Encounter,
        EncounterAbortReason Reason);

    /// <summary>
    /// Host → all peers, reliable-ordered: the encounter is over — completed or torn down by the host — and every
    /// peer discards its replicated state and presentation for this <see cref="Encounter"/>
    /// (Docs/MultiplayerLoadingContract.md §5.11).
    /// </summary>
    /// <remarks>
    /// The discrete teardown signal a client cannot infer: without it, a host dropping the encounter mid-fight
    /// leaves the last-known puppet rendering forever. Terminal per encounter id, so applying it twice is
    /// naturally idempotent — it needs no stream sequence.
    /// </remarks>
    public sealed record EncounterEnded(
        EncounterId Encounter,
        SimulationTick Tick);
}
