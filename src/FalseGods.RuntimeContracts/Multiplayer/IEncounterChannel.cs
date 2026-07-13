using System;
using FalseGods.RuntimeContracts.Transport;

namespace FalseGods.RuntimeContracts.Multiplayer
{
    /// <summary>
    /// The project's opaque message channel for the encounter — send and receive <see cref="EncodedPayload"/>s,
    /// transport invisible.
    /// </summary>
    /// <remarks>
    /// Implemented by the optional ST adapter over the public bridge (<c>SULFURTogether.Api.NetExternalChannel</c>,
    /// one opaque relay), or as a no-op in single-player. <c>FalseGods.Application</c> serializes every Protocol DTO
    /// into an <see cref="EncodedPayload"/> and picks a <see cref="MessageDelivery"/> and <see cref="MessageTarget"/>;
    /// the adapter maps that onto one transport send and never sees a DTO (Docs/MultiplayerLoadingContract.md §5.10).
    ///
    /// <para>
    /// <see cref="Received"/> fires on the game's main thread with the authenticated sender's id (stamped from the
    /// transport peer, never from the wire — Docs/DependencyRules.md §12) and the opaque bytes. Application decodes,
    /// validates, and applies them.
    /// </para>
    /// </remarks>
    public interface IEncounterChannel
    {
        /// <summary>Send <paramref name="payload"/> to <paramref name="target"/> with the given delivery guarantee.</summary>
        void Send(EncodedPayload payload, MessageDelivery delivery, MessageTarget target);

        /// <summary>Raised when a payload arrives, carrying the authenticated sender and the opaque bytes.</summary>
        event Action<SessionPeerId, EncodedPayload> Received;
    }
}
