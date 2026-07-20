using FalseGods.RuntimeContracts.Multiplayer;

namespace FalseGods.RuntimeContracts.Integration
{
    /// <summary>
    /// The capability bundle an optional multiplayer integration supplies to the Composition Root — the stable
    /// contract that crosses the optional-integration seam (Docs/Architecture.md §4.1, ADR-004).
    /// </summary>
    /// <remarks>
    /// Only project-owned RuntimeContracts ports cross this seam, never an ST or transport type. The bundle
    /// carries exactly the ports that exist today: session role/identity, the opaque encounter channel, and the
    /// session membership roster. Ports that have no implementation yet (arena lockdown, remote-NPC activation)
    /// are added here when they exist — a port with no present consumer is premature (Docs/RiskList.md R31).
    ///
    /// <para>
    /// Implemented by <c>FalseGods.Integration.SulfurTogether</c> and handed to
    /// <see cref="FalseGodsIntegrations.Register"/>. <c>FalseGods.Plugin</c> is the only reader; everything else
    /// receives these ports by constructor injection (FG-ARCH-005). Unit tests construct fakes of this interface
    /// directly and never touch the broker.
    /// </para>
    /// </remarks>
    public interface IFalseGodsIntegration
    {
        /// <summary>The session's role and local identity.</summary>
        IMultiplayerSession Session { get; }

        /// <summary>The opaque encounter message channel.</summary>
        IEncounterChannel Channel { get; }

        /// <summary>The current session membership.</summary>
        IPlayerRoster Roster { get; }

        /// <summary>Maps a boss participant (game player index) to its owning session peer, so the host can address
        /// a per-player decision to the right client.</summary>
        IParticipantPeerMap Players { get; }
    }
}
