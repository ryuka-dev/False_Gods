using FalseGods.Core.Simulation;

namespace FalseGods.Protocol.Wire
{
    /// <summary>
    /// Host → one client, reliable-ordered: the boss's authoritative attack resolution hit that client's player;
    /// apply <see cref="Amount"/> damage to your own local player (Docs/MultiplayerLoadingContract.md §5.6).
    /// </summary>
    /// <remarks>
    /// The host owns boss damage: it resolves who is hit and by how much, applies it to its own local player
    /// directly, and sends this to each remote peer whose player was hit. The client applies it to its <b>own</b>
    /// local player — never to a puppet of another player — because in SULFUR each player is authoritative over its
    /// own health. This message carries no player id: it is addressed to a specific peer, and "you" is always the
    /// receiver's local player. Untrusted on receipt (§12): a client applies it only when it comes from the session
    /// host and names the live encounter, and clamps a non-positive amount out.
    /// </remarks>
    public sealed record BossHitPlayer(
        EncounterId Encounter,
        int Amount);
}
