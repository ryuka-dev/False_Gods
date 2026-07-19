using System;
using System.Collections.Generic;
using FalseGods.RuntimeContracts.Multiplayer;
using FalseGods.RuntimeContracts.Transport;
using SULFURTogether.Api;

namespace FalseGods.Integration.SulfurTogether
{
    /// <summary>
    /// <see cref="IPlayerRoster"/> over <see cref="NetSessionInfo.Peers"/> — the current session membership,
    /// including the local peer, mapped through the adapter's <see cref="StPeerDirectory"/>. Polled, because the
    /// bridge deliberately exposes no join/leave events — the consumers re-read membership each tick.
    /// </summary>
    /// <remarks>
    /// The bridge list is read with an index loop in a regular method, so no <c>ExternalPeer</c> ever lands in an
    /// enumerator or compiler-generated field — see <see cref="StEncounterChannel"/>'s remarks for the type-load
    /// discipline this assembly follows.
    /// </remarks>
    internal sealed class StPlayerRoster : IPlayerRoster
    {
        private readonly StPeerDirectory _peers;

        public StPlayerRoster(StPeerDirectory peers) =>
            _peers = peers ?? throw new ArgumentNullException(nameof(peers));

        public IReadOnlyList<SessionPeerId> Members => ReadMembers();

        private IReadOnlyList<SessionPeerId> ReadMembers()
        {
            var result = new List<SessionPeerId>();
            var bridgePeers = NetSessionInfo.Peers;
            for (var i = 0; i < bridgePeers.Count; i++)
            {
                var bridgeId = bridgePeers[i].PeerId;
                if (!string.IsNullOrEmpty(bridgeId))
                {
                    result.Add(_peers.Map(bridgeId));
                }
            }

            return result;
        }
    }
}
