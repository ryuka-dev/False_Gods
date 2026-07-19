using System;
using System.Collections.Generic;
using FalseGods.RuntimeContracts.Transport;

namespace FalseGods.Integration.SulfurTogether
{
    /// <summary>
    /// The adapter's explicit identity mapping: the ST bridge's string peer ids onto the project's
    /// <see cref="SessionPeerId"/>. Owned by the adapter, because a mapping between identity domains must have
    /// exactly one owner (Docs/DependencyRules.md §4) — nothing above the adapter ever sees a bridge peer id.
    /// </summary>
    /// <remarks>
    /// Ids are assigned on first sight and stable for the adapter's lifetime, so the same bridge peer always maps
    /// to the same <see cref="SessionPeerId"/> within a session (and a rejoining peer with the same bridge id maps
    /// back to the same value — which is what lets the host's replication re-baseline it by identity). The map
    /// only grows; it is session-scoped state whose size is bounded by the number of distinct peers seen, which is
    /// small by construction. It holds no ST type — bridge ids are plain strings.
    /// </remarks>
    internal sealed class StPeerDirectory
    {
        private readonly Dictionary<string, SessionPeerId> _byBridgeId = new Dictionary<string, SessionPeerId>(StringComparer.Ordinal);
        private readonly Dictionary<SessionPeerId, string> _byPeer = new Dictionary<SessionPeerId, string>();
        private int _next = 1;

        /// <summary>Map a bridge peer id to its stable <see cref="SessionPeerId"/>, assigning one on first sight.</summary>
        public SessionPeerId Map(string bridgePeerId)
        {
            if (string.IsNullOrEmpty(bridgePeerId))
            {
                throw new ArgumentException("A bridge peer id must be non-empty.", nameof(bridgePeerId));
            }

            if (!_byBridgeId.TryGetValue(bridgePeerId, out var peer))
            {
                peer = new SessionPeerId(_next++);
                _byBridgeId.Add(bridgePeerId, peer);
                _byPeer.Add(peer, bridgePeerId);
            }

            return peer;
        }

        /// <summary>Reverse lookup for addressing a specific peer; false when the id was never seen.</summary>
        public bool TryGetBridgeId(SessionPeerId peer, out string bridgePeerId) =>
            _byPeer.TryGetValue(peer, out bridgePeerId!);
    }
}
