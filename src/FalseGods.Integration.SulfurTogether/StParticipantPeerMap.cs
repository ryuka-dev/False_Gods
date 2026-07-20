using System;
using FalseGods.RuntimeContracts.Multiplayer;
using FalseGods.RuntimeContracts.Transport;
using SULFURTogether.Api;

namespace FalseGods.Integration.SulfurTogether
{
    /// <summary>
    /// <see cref="IParticipantPeerMap"/> over the session bridge's per-peer player index: find the remote peer
    /// whose local player index matches the boss participant, mapped through the adapter's
    /// <see cref="StPeerDirectory"/>.
    /// </summary>
    /// <remarks>
    /// Read with an index loop in a regular method — no <c>ExternalPeer</c> is captured in a lambda, iterator, or
    /// field (the same type-load discipline <see cref="StPlayerRoster"/> follows). Only a remote peer resolves: the
    /// local peer and unknown indices carry <c>-1</c> on the bridge, so they never match.
    /// </remarks>
    internal sealed class StParticipantPeerMap : IParticipantPeerMap
    {
        private readonly StPeerDirectory _peers;

        public StParticipantPeerMap(StPeerDirectory peers) =>
            _peers = peers ?? throw new ArgumentNullException(nameof(peers));

        public bool TryGetRemotePeer(int participantIndex, out SessionPeerId peer)
        {
            var bridgePeers = NetSessionInfo.Peers;
            for (var i = 0; i < bridgePeers.Count; i++)
            {
                if (bridgePeers[i].IsLocal || bridgePeers[i].PlayerIndex != participantIndex)
                {
                    continue;
                }

                var bridgeId = bridgePeers[i].PeerId;
                if (!string.IsNullOrEmpty(bridgeId))
                {
                    peer = _peers.Map(bridgeId);
                    return true;
                }
            }

            peer = default;
            return false;
        }
    }
}
