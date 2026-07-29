using System;
using FalseGods.Application.Replication;
using FalseGods.RuntimeContracts.Diagnostics;
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
        private readonly ILogger? _logger;
        private int _sequence;

        // Which encounter this end has already announced it is reporting for, so the line below is said once per
        // fight rather than once per shot.
        private EncounterId _announced;

        public ClientHitReporter(IEncounterChannel channel, IMultiplayerSession session, ILogger? logger = null)
        {
            _channel = channel ?? throw new ArgumentNullException(nameof(channel));
            _session = session ?? throw new ArgumentNullException(nameof(session));
            _logger = logger;
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

            // Said once per encounter, on the first hit reported for it. Whether this end is sending at all is the
            // first question asked when a client cannot hurt the boss, and it used to be unanswerable from this
            // log: the host says when it accepts a hit, and there was nothing to compare that against when it
            // never did. One line, not one per shot - the rate here is a player's rate of fire.
            if (_announced != encounter)
            {
                _announced = encounter;
                _logger?.Log($"Reporting weapon hits to the host for {encounter}; the first is on its way.");
            }
        }
    }
}
