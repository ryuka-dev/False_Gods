using System;
using FalseGods.Application.Replication;
using FalseGods.Core.Simulation;
using FalseGods.Protocol.Wire;
using FalseGods.RuntimeContracts.Multiplayer;
using FalseGods.RuntimeContracts.Transport;

namespace FalseGods.Application.Combat
{
    /// <summary>
    /// The host's intake for client hit requests (Docs/OriginalBossNetworkingArchitecture.md §5.6): subscribe to
    /// the encounter channel and, for a request that belongs to this encounter and comes from a session member,
    /// clamp the untrusted damage candidate and hand it to the authoritative damage path. The simulation — never
    /// the request — decides weak-point amplification, phase, and death (SULFUR Together invariants 1/2).
    /// </summary>
    /// <remarks>
    /// Untrusted input (Docs/DependencyRules.md §12), mirroring <see cref="Arena.HostEncounterGate"/>: traffic that
    /// does not decode, a request for another encounter, any non-<see cref="ClientHitRequest"/>, a sender that is
    /// not a current member, or a non-finite / non-positive candidate are all dropped. The <b>sender id is the
    /// channel's authenticated peer</b>, never read from the payload. The candidate is clamped to
    /// <paramref name="maxDamagePerHit"/> — a sanity bound on a single forged message, not a substitute for rate
    /// limiting. The clamped value is delivered as a float to the same apply-damage path a local weapon hit uses,
    /// so the float→int conversion and the simulation's rules stay in one place. Callbacks fire on the channel's
    /// delivery thread (the game's main thread, per the channel contract).
    /// </remarks>
    public sealed class HostHitIntake : IDisposable
    {
        private readonly IEncounterChannel _channel;
        private readonly IPlayerRoster _roster;
        private readonly EncounterId _encounter;
        private readonly float _maxDamagePerHit;
        private readonly Action<float> _applyDamage;
        private readonly Action<string>? _log;

        public HostHitIntake(
            IEncounterChannel channel,
            IPlayerRoster roster,
            EncounterId encounter,
            float maxDamagePerHit,
            Action<float> applyDamage,
            Action<string>? log = null)
        {
            _channel = channel ?? throw new ArgumentNullException(nameof(channel));
            _roster = roster ?? throw new ArgumentNullException(nameof(roster));
            _applyDamage = applyDamage ?? throw new ArgumentNullException(nameof(applyDamage));
            _encounter = encounter;
            _maxDamagePerHit = maxDamagePerHit;
            _log = log;
            _channel.Received += OnReceived;
        }

        public void Dispose() => _channel.Received -= OnReceived;

        private void OnReceived(SessionPeerId sender, EncodedPayload payload)
        {
            DecodedMessage message;
            try
            {
                message = EncounterCodec.Decode(payload);
            }
            catch (Exception)
            {
                return; // undecodable traffic is not the intake's problem
            }

            if (!(message.Value is ClientHitRequest request) || request.Encounter != _encounter)
            {
                return; // another message kind, or a request for a different encounter
            }

            if (!IsMember(sender))
            {
                _log?.Invoke($"Dropped a hit request from non-member {sender} for {_encounter}.");
                return;
            }

            var candidate = request.DamageCandidate;
            if (float.IsNaN(candidate) || float.IsInfinity(candidate) || candidate <= 0f)
            {
                return; // no evidence to act on
            }

            var clamped = Math.Min(candidate, _maxDamagePerHit);
            _log?.Invoke($"Client {sender} hit seq={request.RequestSequence}: candidate {candidate:0.##} "
                + $"-> applying {clamped:0.##} (host clamps; the simulation decides the result).");
            _applyDamage(clamped);
        }

        private bool IsMember(SessionPeerId sender)
        {
            var members = _roster.Members;
            for (var i = 0; i < members.Count; i++)
            {
                if (members[i] == sender)
                {
                    return true;
                }
            }

            return false;
        }
    }
}
