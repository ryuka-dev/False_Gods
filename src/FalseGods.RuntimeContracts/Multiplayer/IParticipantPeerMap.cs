using FalseGods.RuntimeContracts.Transport;

namespace FalseGods.RuntimeContracts.Multiplayer
{
    /// <summary>
    /// Maps a boss participant — a player index in this machine's game roster — to the session peer that owns it,
    /// so the authoritative host can address a decision about a specific player (e.g. boss damage) to the right
    /// client (Docs/DependencyRules.md §3 — an explicit, owned identity mapping).
    /// </summary>
    /// <remarks>
    /// Implemented by the optional ST adapter over the session bridge's per-peer player index. Only <b>remote</b>
    /// players resolve: the local player is not a remote peer, so a caller checks its own local participant index
    /// separately and treats a non-resolving index as local-or-unknown. The mapping is host-meaningful — a client
    /// keeps no per-peer players and resolves nothing. Participant indices are each machine's own roster indices,
    /// never compared across machines (Docs/DependencyRules.md §3).
    /// </remarks>
    public interface IParticipantPeerMap
    {
        /// <summary>The peer that owns the player at <paramref name="participantIndex"/>, when that player is a
        /// remote peer's; false for the local player or an unknown index.</summary>
        bool TryGetRemotePeer(int participantIndex, out SessionPeerId peer);
    }
}
