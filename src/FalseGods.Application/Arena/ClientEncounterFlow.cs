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
    public sealed record ClientLoadOutcome(ArenaManifest? Manifest, string? FailureReason)
    {
        public static ClientLoadOutcome Ready(ArenaManifest manifest) => new ClientLoadOutcome(manifest, null);

        public static ClientLoadOutcome Failed(string reason) => new ClientLoadOutcome(null, reason);
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
        private readonly IEncounterChannel _channel;
        private readonly IMultiplayerSession _session;

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
            }
        }

        private void HandleEnterArena(EnterArena enter)
        {
            ClientLoadOutcome outcome;
            if (!IsFinite(enter.Origin))
            {
                outcome = ClientLoadOutcome.Failed("EnterArena carried a non-finite origin");
            }
            else if (OnEnterArena is null)
            {
                outcome = ClientLoadOutcome.Failed("client has no arena composition to load with");
            }
            else
            {
                outcome = OnEnterArena(enter);
            }

            if (outcome.Manifest != null)
            {
                Reply(EncounterCodec.Encode(new ArenaReady(enter.Encounter, outcome.Manifest)));
            }
            else
            {
                Reply(EncounterCodec.Encode(new ArenaLoadFailed(
                    enter.Encounter, outcome.FailureReason ?? "unspecified load failure")));
            }
        }

        private void Reply(EncodedPayload payload) =>
            _channel.Send(payload, MessageDelivery.ReliableOrdered, MessageTarget.Host);

        private static bool IsFinite(WorldPosition p) =>
            !float.IsNaN(p.X) && !float.IsInfinity(p.X)
            && !float.IsNaN(p.Y) && !float.IsInfinity(p.Y)
            && !float.IsNaN(p.Z) && !float.IsInfinity(p.Z);
    }
}
