using FalseGods.RuntimeContracts.Transport;

namespace FalseGods.RuntimeContracts.Multiplayer
{
    /// <summary>
    /// The session's role and local identity — the project's read-only view of "am I host or client, and who am
    /// I".
    /// </summary>
    /// <remarks>
    /// Implemented by the optional ST adapter over the public session bridge (<c>SULFURTogether.Api.NetSessionInfo</c>),
    /// or trivially by <c>Integration.Sulfur</c> in single-player (host, one local peer). It exposes identity only —
    /// no positions, no gameplay authority (Docs/MultiplayerLoadingContract.md §5.1). Replication reads
    /// <see cref="Role"/> to decide whether to broadcast or receive; the ready flow reads <see cref="LocalPeer"/>
    /// to record the host's own local readiness (§5.3).
    /// </remarks>
    public interface IMultiplayerSession
    {
        /// <summary>Whether this peer is the authoritative host or a client.</summary>
        SessionRole Role { get; }

        /// <summary>Whether a session is currently active.</summary>
        bool IsActive { get; }

        /// <summary>This peer's own id in the session.</summary>
        SessionPeerId LocalPeer { get; }

        /// <summary>
        /// The authoritative host's id in the session — what inbound replication is validated against (a
        /// payload not sent by this peer is dropped; Docs/DependencyRules.md §12). On the host it equals
        /// <see cref="LocalPeer"/>; meaningful only while <see cref="IsActive"/>.
        /// </summary>
        SessionPeerId HostPeer { get; }
    }
}
