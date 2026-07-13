using System;
using FalseGods.Protocol.Wire;
using FalseGods.RuntimeContracts.Multiplayer;
using FalseGods.RuntimeContracts.Transport;

namespace FalseGods.Application.Replication
{
    /// <summary>
    /// The host side of replication: encodes boss/arena snapshots, events, and baselines and sends them over the
    /// opaque <see cref="IEncounterChannel"/> with the right delivery guarantee and target
    /// (Docs/OriginalBossNetworkingArchitecture.md §9.6, Docs/MultiplayerLoadingContract.md §5.10).
    /// </summary>
    /// <remarks>
    /// Snapshots go out <see cref="MessageDelivery.Unreliable"/> to all clients (loss is corrected by the next
    /// one); discrete events and baselines go <see cref="MessageDelivery.ReliableOrdered"/>. Only the host sends —
    /// the sender asserts its session role, because a client broadcasting authoritative state is a bug, not a
    /// mode. It holds no gameplay state; it is a thin, transport-neutral shipping layer.
    /// </remarks>
    public sealed class ReplicationSender
    {
        private readonly IEncounterChannel _channel;
        private readonly IMultiplayerSession _session;

        public ReplicationSender(IEncounterChannel channel, IMultiplayerSession session)
        {
            _channel = channel ?? throw new ArgumentNullException(nameof(channel));
            _session = session ?? throw new ArgumentNullException(nameof(session));
        }

        public void BroadcastBossSnapshot(BossSnapshot snapshot) =>
            Send(EncounterCodec.Encode(snapshot), MessageDelivery.Unreliable, MessageTarget.AllClients);

        public void BroadcastArenaSnapshot(ArenaSnapshot snapshot) =>
            Send(EncounterCodec.Encode(snapshot), MessageDelivery.Unreliable, MessageTarget.AllClients);

        public void BroadcastBossEvent(IBossWireEvent bossEvent) =>
            Send(EncounterCodec.Encode(bossEvent), MessageDelivery.ReliableOrdered, MessageTarget.AllClients);

        public void BroadcastArenaEvent(IArenaWireEvent arenaEvent) =>
            Send(EncounterCodec.Encode(arenaEvent), MessageDelivery.ReliableOrdered, MessageTarget.AllClients);

        /// <summary>Send the once-per-join baseline reliably to the peer that is joining or being repaired.</summary>
        public void SendBaseline(EncounterBaseline baseline, SessionPeerId peer) =>
            Send(EncounterCodec.Encode(baseline), MessageDelivery.ReliableOrdered, MessageTarget.ToPeer(peer));

        private void Send(EncodedPayload payload, MessageDelivery delivery, MessageTarget target)
        {
            if (_session.Role != SessionRole.Host)
            {
                throw new InvalidOperationException("Only the host replicates encounter state; a client must not broadcast.");
            }

            _channel.Send(payload, delivery, target);
        }
    }
}
