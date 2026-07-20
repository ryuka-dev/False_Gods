using System;
using FalseGods.Application.Replication;
using FalseGods.Core.Simulation;
using FalseGods.Protocol.Wire;
using FalseGods.RuntimeContracts.Multiplayer;
using FalseGods.RuntimeContracts.Transport;

namespace FalseGods.Application.Combat
{
    /// <summary>
    /// The client's side of the hit path (Docs/OriginalBossNetworkingArchitecture.md §5.6): when the local
    /// player's weapon strikes the boss puppet, send the host a <see cref="ClientHitRequest"/>. It reports intent
    /// only — the host validates membership, clamps the candidate, and its simulation decides the result, which
    /// returns through the ordinary <c>BossDamaged</c> stream. The client never applies damage locally (SULFUR
    /// Together invariant 2).
    /// </summary>
    /// <remarks>
    /// The counterpart of <see cref="Replication.ReplicationSender"/>, but upstream: it addresses
    /// <see cref="MessageTarget.Host"/> and asserts nothing about host role — reporting is the client's job. Sends
    /// are reliable-ordered so a hit is never silently lost; coalescing rapid fire is a later optimisation. A
    /// report into a dead session is dropped rather than thrown, since a hit can land on the same frame a session
    /// tears down.
    /// </remarks>
    public sealed class ClientHitReporter
    {
        private readonly IEncounterChannel _channel;
        private readonly IMultiplayerSession _session;
        private int _sequence;

        public ClientHitReporter(IEncounterChannel channel, IMultiplayerSession session)
        {
            _channel = channel ?? throw new ArgumentNullException(nameof(channel));
            _session = session ?? throw new ArgumentNullException(nameof(session));
        }

        /// <summary>Report one weapon hit on the boss puppet to the host. <paramref name="damageCandidate"/> is the
        /// client's locally computed damage — evidence the host clamps, never the final amount.</summary>
        public void ReportHit(EncounterId encounter, float damageCandidate, WorldPosition? attackerPosition = null)
        {
            if (!_session.IsActive)
            {
                return;
            }

            var request = new ClientHitRequest(encounter, ++_sequence, damageCandidate, attackerPosition);
            _channel.Send(EncounterCodec.Encode(request), MessageDelivery.ReliableOrdered, MessageTarget.Host);
        }
    }
}
