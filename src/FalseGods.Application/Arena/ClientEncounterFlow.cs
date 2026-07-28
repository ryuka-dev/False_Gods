using System;
using FalseGods.Application.Replication;
using FalseGods.Protocol.Arena;
using FalseGods.Protocol.Wire;
using FalseGods.RuntimeContracts.Multiplayer;
using FalseGods.RuntimeContracts.Transport;

namespace FalseGods.Application.Arena
{
    /// <summary>The composition's answer to an <c>EnterArena</c>: the locally validated manifest on success, or
    /// the reason to report in <c>ArenaLoadFailed</c>.</summary>
    /// <param name="NotYet">
    /// The composition cannot answer <i>right now</i> but expects to be able to shortly, so nothing is reported to
    /// the host yet. The case it exists for: this peer is on its way into the same level and its arena is still
    /// being built as the announcement arrives. Answering then would make it load a second copy of the content it
    /// is about to be standing in — which does not merely waste a load, it fails, because a standing arena holds
    /// its bundle open (Docs/BossEncounterRunbook.md §3.12).
    /// </param>
    public sealed record ClientLoadOutcome(ArenaManifest? Manifest, string? FailureReason, bool NotYet = false)
    {
        public static ClientLoadOutcome Ready(ArenaManifest manifest) => new ClientLoadOutcome(manifest, null);

        public static ClientLoadOutcome Failed(string reason) => new ClientLoadOutcome(null, reason);

        /// <summary>Ask again shortly. <paramref name="reason"/> is what gets reported if the wait runs out, so it
        /// has to stand on its own as a failure.</summary>
        public static ClientLoadOutcome Deferred(string reason) => new ClientLoadOutcome(null, reason, NotYet: true);
    }

    /// <summary>
    /// The client's side of the encounter control choreography (Docs/MultiplayerLoadingContract.md §5.3 steps
    /// 2–4, §5.3.1, §5.11): react to the host's <c>EnterArena</c> by running the local load (a callback the
    /// composition supplies), answer with <c>ArenaReady</c> or <c>ArenaLoadFailed</c>, and surface
    /// <c>EncounterAborted</c> / <c>EncounterEnded</c> so the composition tears down.
    /// </summary>
    /// <remarks>
    /// Untrusted input (Docs/DependencyRules.md §12): a control payload whose sender is not the session host is
    /// dropped; one that does not decode is dropped; an <c>EnterArena</c> whose origin is not finite is refused
    /// with <c>ArenaLoadFailed</c> rather than realized somewhere absurd. Replication kinds are ignored here —
    /// the <see cref="ReplicationReceiver"/> owns them. Callbacks fire on the channel's delivery thread (the
    /// game's main thread, per the channel contract), so the composition may do Unity work inside them.
    /// </remarks>
    public sealed class ClientEncounterFlow : IDisposable
    {
        /// <summary>
        /// How long a deferred load may keep saying "not yet" before it is reported as a failure.
        /// </summary>
        /// <remarks>
        /// Comfortably inside the host's own gate timeout, so a peer that really cannot get into the arena tells
        /// the host <i>why</i> instead of going silent and being timed out — a named reason is the difference
        /// between a diagnosable abort and a mysterious one.
        /// </remarks>
        private const float DeferralLimitSeconds = 25f;

        private readonly IEncounterChannel _channel;
        private readonly IMultiplayerSession _session;

        private EnterArena? _waiting;
        private float _waited;

        public ClientEncounterFlow(IEncounterChannel channel, IMultiplayerSession session)
        {
            _channel = channel ?? throw new ArgumentNullException(nameof(channel));
            _session = session ?? throw new ArgumentNullException(nameof(session));
            _channel.Received += OnReceived;
        }

        /// <summary>Run the local load for the announced arena; return the manifest to report, or the failure.
        /// Unset means "not composed yet" and every EnterArena is answered with a load failure.</summary>
        public Func<EnterArena, ClientLoadOutcome>? OnEnterArena { get; set; }

        /// <summary>The host aborted the encounter before it started — tear the local arena down.</summary>
        public Action<EncounterAborted>? OnAborted { get; set; }

        /// <summary>The encounter is over — discard everything local to it.</summary>
        public Action<EncounterEnded>? OnEnded { get; set; }

        /// <summary>The boss's attack hit this client's player — apply the host-decided damage to the local player.</summary>
        public Action<BossHitPlayer>? OnBossHitPlayer { get; set; }

        /// <summary>
        /// Retry an announcement the composition was not ready for, and give up on one it has been holding too
        /// long. A no-op when there is nothing waiting, which is almost always.
        /// </summary>
        public void Tick(float deltaSeconds)
        {
            var waiting = _waiting;
            if (waiting is null)
            {
                return;
            }

            _waited += deltaSeconds;
            Answer(waiting, Load(waiting), giveUp: _waited >= DeferralLimitSeconds);
        }

        public void Dispose() => _channel.Received -= OnReceived;

        private void OnReceived(SessionPeerId sender, EncodedPayload payload)
        {
            // Only the authoritative host drives the encounter control flow.
            if (sender != _session.HostPeer)
            {
                return;
            }

            DecodedMessage message;
            try
            {
                message = EncounterCodec.Decode(payload);
            }
            catch (Exception)
            {
                return;
            }

            switch (message.Value)
            {
                case EnterArena enter:
                    HandleEnterArena(enter);
                    break;
                case EncounterAborted aborted:
                    OnAborted?.Invoke(aborted);
                    break;
                case EncounterEnded ended:
                    OnEnded?.Invoke(ended);
                    break;
                case BossHitPlayer hit:
                    OnBossHitPlayer?.Invoke(hit);
                    break;
            }
        }

        private void HandleEnterArena(EnterArena enter)
        {
            // A fresh announcement replaces whatever was being waited on: the host has moved on.
            _waiting = null;
            _waited = 0f;
            Answer(enter, Load(enter), giveUp: false);
        }

        private ClientLoadOutcome Load(EnterArena enter)
        {
            if (!IsFinite(enter.Origin))
            {
                return ClientLoadOutcome.Failed("EnterArena carried a non-finite origin");
            }

            if (OnEnterArena is null)
            {
                return ClientLoadOutcome.Failed("client has no arena composition to load with");
            }

            return OnEnterArena(enter);
        }

        /// <summary>Report the outcome to the host, or hold the announcement for another go. Nothing is said out
        /// loud while a load is deferred — the host is waiting at its gate either way, and a peer that answered
        /// "failed" would abort an encounter it is seconds away from being ready for.</summary>
        private void Answer(EnterArena enter, ClientLoadOutcome outcome, bool giveUp)
        {
            if (outcome.NotYet && !giveUp)
            {
                _waiting = enter;
                return;
            }

            _waiting = null;
            _waited = 0f;

            if (outcome.Manifest != null)
            {
                Reply(EncounterCodec.Encode(new ArenaReady(enter.Encounter, outcome.Manifest)));
                return;
            }

            Reply(EncounterCodec.Encode(new ArenaLoadFailed(
                enter.Encounter, outcome.FailureReason ?? "unspecified load failure")));
        }

        private void Reply(EncodedPayload payload) =>
            _channel.Send(payload, MessageDelivery.ReliableOrdered, MessageTarget.Host);

        private static bool IsFinite(WorldPosition p) =>
            !float.IsNaN(p.X) && !float.IsInfinity(p.X)
            && !float.IsNaN(p.Y) && !float.IsInfinity(p.Y)
            && !float.IsNaN(p.Z) && !float.IsInfinity(p.Z);
    }
}
