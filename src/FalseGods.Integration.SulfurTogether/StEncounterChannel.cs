using System;
using FalseGods.RuntimeContracts.Multiplayer;
using FalseGods.RuntimeContracts.Transport;
using SULFURTogether.Api;
using ILogger = FalseGods.RuntimeContracts.Diagnostics.ILogger;

namespace FalseGods.Integration.SulfurTogether
{
    /// <summary>
    /// <see cref="IEncounterChannel"/> over the ST public bridge (<see cref="NetExternalChannel"/>): one opaque
    /// channel carries every False Gods payload, and the delivery/target vocabularies are translated here —
    /// nothing above this class sees a bridge type, and this class never sees a Protocol DTO
    /// (Docs/MultiplayerLoadingContract.md §5.10, ADR-004 §4).
    /// </summary>
    /// <remarks>
    /// <b>ST-type-load discipline (Docs/DependencyRules.md §6, the B0 gotcha):</b> this assembly is loaded even
    /// when the installed ST build lacks the bridge, so no <c>SULFURTogether.Api</c> type may end up in any field —
    /// including the compiler-generated fields of iterators, async methods, and lambdas. The bridge registration is
    /// therefore held as a plain <see cref="IDisposable"/>, every bridge call sits in a regular method body (JIT'd
    /// only when actually invoked), and there is no lambda or LINQ over bridge types anywhere in this assembly.
    /// The architecture check FG-ARCH-011 scans the built DLL's field signatures to hold this.
    ///
    /// <para>
    /// The bridge invokes <see cref="OnBridgePayload"/> on the Unity main thread with the sender id stamped from
    /// the authenticated connection, satisfying <see cref="IEncounterChannel.Received"/>'s contract; the id is
    /// mapped through the adapter-owned <see cref="StPeerDirectory"/>. A failed send (no session, role/target
    /// mismatch, oversize) is logged and dropped — the bridge already refuses it without throwing, and replication
    /// tolerates loss by design.
    /// </para>
    /// </remarks>
    internal sealed class StEncounterChannel : IEncounterChannel, IDisposable
    {
        /// <summary>The one bridge channel all False Gods traffic rides (kind bytes inside the payload disambiguate).</summary>
        public const string ChannelId = "false_gods.encounter";

        private readonly StPeerDirectory _peers;
        private readonly ILogger _logger;

        // Deliberately IDisposable, not IExternalChannelRegistration — see the class remarks.
        private IDisposable? _registration;

        public StEncounterChannel(StPeerDirectory peers, ILogger logger)
        {
            _peers = peers ?? throw new ArgumentNullException(nameof(peers));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        public event Action<SessionPeerId, EncodedPayload>? Received;

        /// <summary>
        /// Register the channel with the bridge. Idempotent. False when the bridge refuses the id (already
        /// registered by another live instance) — the caller must then stay inert rather than half-attach.
        /// </summary>
        public bool TryRegister()
        {
            if (_registration != null)
            {
                return true;
            }

            try
            {
                _registration = NetExternalChannel.Register(ChannelId, OnBridgePayload);
                _logger.Log($"Encounter channel '{ChannelId}' registered on the ST bridge (API v{NetExternalChannel.ApiVersion}).");
                return true;
            }
            catch (InvalidOperationException ex)
            {
                _logger.LogWarning($"Encounter channel '{ChannelId}' is already registered with the ST bridge: {ex.Message}");
                return false;
            }
        }

        public void Send(EncodedPayload payload, MessageDelivery delivery, MessageTarget target)
        {
            var bytes = payload.ToArray();
            string? bridgePeerId = null;
            if (target.Kind == MessageTargetKind.SpecificPeer && !_peers.TryGetBridgeId(target.Peer, out bridgePeerId))
            {
                _logger.LogWarning($"Dropping a send to unknown {target.Peer}: no bridge id is mapped for it.");
                return;
            }

            if (!NetExternalChannel.Send(ChannelId, bytes, MapDelivery(delivery), MapTarget(target.Kind), bridgePeerId))
            {
                // The bridge refuses (returns false) for: no live session, a role/target mismatch, an oversized
                // payload, or an unknown peer. All are conditions replication must survive, not crash on.
                _logger.LogWarning($"Bridge refused a {delivery} send to {target} ({bytes.Length} bytes).");
            }
        }

        public void Dispose()
        {
            try
            {
                _registration?.Dispose();
            }
            catch (Exception ex)
            {
                _logger.LogWarning($"Encounter channel unregister failed: {ex.Message}");
            }

            _registration = null;
        }

        private void OnBridgePayload(string senderBridgeId, byte[] payload)
        {
            if (string.IsNullOrEmpty(senderBridgeId) || payload is null)
            {
                _logger.LogWarning("Dropping a bridge payload with no sender id or no body.");
                return;
            }

            Received?.Invoke(_peers.Map(senderBridgeId), new EncodedPayload(payload));
        }

        private static ExternalDelivery MapDelivery(MessageDelivery delivery) =>
            delivery == MessageDelivery.Unreliable ? ExternalDelivery.Unreliable : ExternalDelivery.ReliableOrdered;

        private static ExternalTarget MapTarget(MessageTargetKind kind)
        {
            switch (kind)
            {
                case MessageTargetKind.Host:
                    return ExternalTarget.Host;
                case MessageTargetKind.AllClients:
                    return ExternalTarget.AllClients;
                case MessageTargetKind.SpecificPeer:
                    return ExternalTarget.SpecificPeer;
                default:
                    throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unknown message target kind.");
            }
        }
    }
}
