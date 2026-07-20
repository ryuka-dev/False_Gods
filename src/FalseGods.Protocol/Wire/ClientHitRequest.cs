using FalseGods.Core.Simulation;

namespace FalseGods.Protocol.Wire
{
    /// <summary>
    /// Client → host, reliable-ordered: the local player's weapon struck the boss puppet, and the client asks the
    /// host to apply the hit (Docs/OriginalBossNetworkingArchitecture.md §5.6). The client reports intent; the host
    /// owns the result.
    /// </summary>
    /// <remarks>
    /// Every field is untrusted input on receipt (Docs/DependencyRules.md §12). <see cref="DamageCandidate"/> is the
    /// client's locally computed per-hit damage — <b>evidence, never the verdict</b>: the host validates the sender
    /// is a session member, that the request is for the live encounter, then clamps the candidate and applies its own
    /// weak-point / phase / death rules through <c>BossSimulation.ApplyDamage</c> (SULFUR Together invariants 1/2). The
    /// authoritative result reaches every peer through the ordinary <c>BossDamaged</c> event stream, never from this
    /// request. The sender id is the channel's authenticated peer, so this DTO deliberately carries no peer identity.
    ///
    /// <para>
    /// <see cref="RequestSequence"/> is the client's own monotonic per-hit counter, for the host's de-duplication and
    /// rate limiting. <see cref="AttackerPosition"/>, when present, is the shooter's world position at fire time, for
    /// an optional host range check. Neither grants the client any authority.
    /// </para>
    /// </remarks>
    public sealed record ClientHitRequest(
        EncounterId Encounter,
        int RequestSequence,
        float DamageCandidate,
        WorldPosition? AttackerPosition);
}
