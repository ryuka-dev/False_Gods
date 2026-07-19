using System;
using FalseGods.RuntimeContracts.Multiplayer;
using FalseGods.RuntimeContracts.Transport;
using SULFURTogether.Api;
using FgSessionRole = FalseGods.RuntimeContracts.Multiplayer.SessionRole;
using StSessionRole = SULFURTogether.Api.SessionRole;

namespace FalseGods.Integration.SulfurTogether
{
    /// <summary>
    /// <see cref="IMultiplayerSession"/> over <see cref="NetSessionInfo"/> — the live role/identity snapshot,
    /// read from the bridge on every access so the Composition Root always sees the session as it is now
    /// (sessions start and end in-game, not at plugin load).
    /// </summary>
    /// <remarks>
    /// The bridge's <c>Offline</c> role maps to "no active session" (<see cref="IsActive"/> false), and
    /// <see cref="Role"/> then reports <see cref="FgSessionRole.Client"/> as an inert default — the project enum
    /// deliberately has no Offline member, because "no session" is a session-state fact, not a third authority
    /// role. Callers gate on <see cref="IsActive"/> first (the Composition Root does). Every bridge read sits in a
    /// regular method body — see <see cref="StEncounterChannel"/>'s remarks for the type-load discipline.
    /// </remarks>
    internal sealed class StMultiplayerSession : IMultiplayerSession
    {
        private readonly StPeerDirectory _peers;

        public StMultiplayerSession(StPeerDirectory peers) =>
            _peers = peers ?? throw new ArgumentNullException(nameof(peers));

        public FgSessionRole Role => ReadRole();

        public bool IsActive => ReadIsActive();

        public SessionPeerId LocalPeer => ReadLocalPeer();

        private static FgSessionRole ReadRole() =>
            NetSessionInfo.Role == StSessionRole.Host ? FgSessionRole.Host : FgSessionRole.Client;

        private static bool ReadIsActive() =>
            NetSessionInfo.IsSessionActive && NetSessionInfo.Role != StSessionRole.Offline;

        private SessionPeerId ReadLocalPeer()
        {
            var localBridgeId = NetSessionInfo.LocalPeerId;
            return string.IsNullOrEmpty(localBridgeId) ? default : _peers.Map(localBridgeId);
        }
    }
}
