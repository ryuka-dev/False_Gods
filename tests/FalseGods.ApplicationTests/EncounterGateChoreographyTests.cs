using System.Collections.Generic;
using System.Linq;
using FalseGods.Application.Arena;
using FalseGods.Application.ReadyGate;
using FalseGods.Application.Replication;
using FalseGods.Core.Simulation;
using FalseGods.Protocol.Arena;
using FalseGods.Protocol.Wire;
using FalseGods.RuntimeContracts.Multiplayer;
using FalseGods.RuntimeContracts.Transport;
using Xunit;

namespace FalseGods.ApplicationTests
{
    /// <summary>
    /// The wire choreography around the ready gate (Docs/MultiplayerLoadingContract.md §5.3/§5.3.1): the host
    /// driver broadcasts EnterArena, collects replies into the fail-closed gate, times out silent peers, and
    /// aborts exactly once; the client flow answers only the authenticated host and reports ready or failure.
    /// </summary>
    public sealed class EncounterGateChoreographyTests
    {
        private const int HostPeer = 0;
        private const int ClientPeer = 1;

        private static readonly EncounterId Enc = new EncounterId(5);
        private static readonly WorldPosition Origin = new WorldPosition(10f, 2f, 20f);

        private static ArenaManifest Manifest(byte fill = 1) =>
            new ArenaManifest(
                "false_gods.arena.poc", 1, new ContentHashSchemaVersion(1),
                new ContentHash(Enumerable.Repeat(fill, ContentHash.Sha256Length).ToArray()),
                ProtocolVersion.Current.Value, "bundle.1");

        private sealed class HostRig
        {
            public readonly FakeChannel Channel = new FakeChannel();
            public readonly FakeRoster Roster = new FakeRoster(HostPeer, ClientPeer);
            public readonly HostEncounterGate Gate;

            public HostRig(float timeoutSeconds = 30f)
            {
                var session = new FakeSession(SessionRole.Host, HostPeer);
                Gate = new HostEncounterGate(
                    Channel, session, Roster, new ReplicationSender(Channel, session),
                    Enc, Manifest(), Origin, timeoutSeconds);
            }

            public List<object> SentValues() =>
                Channel.Sent.Select(s => EncounterCodec.Decode(s.Payload).Value).ToList();
        }

        [Fact]
        public void Open_broadcasts_EnterArena_and_records_the_hosts_own_readiness()
        {
            var rig = new HostRig();

            rig.Gate.Open();

            var enter = Assert.IsType<EnterArena>(Assert.Single(rig.SentValues()));
            Assert.Equal(Enc, enter.Encounter);
            Assert.Equal(Origin, enter.Origin);
            Assert.Equal(GateStatus.Waiting, rig.Gate.Status); // the client is still outstanding
        }

        [Fact]
        public void A_matching_client_ready_resolves_and_nothing_is_aborted()
        {
            var rig = new HostRig();
            rig.Gate.Open();

            rig.Channel.Deliver(new SessionPeerId(ClientPeer),
                EncounterCodec.Encode(new ArenaReady(Enc, Manifest())));
            rig.Gate.Tick(0.1f);

            Assert.Equal(GateStatus.Resolved, rig.Gate.Status);
            Assert.DoesNotContain(rig.SentValues(), v => v is EncounterAborted);
        }

        [Fact]
        public void A_content_mismatch_aborts_and_broadcasts_exactly_once()
        {
            var rig = new HostRig();
            rig.Gate.Open();

            rig.Channel.Deliver(new SessionPeerId(ClientPeer),
                EncounterCodec.Encode(new ArenaReady(Enc, Manifest(fill: 9))));
            rig.Gate.Tick(0.1f);
            rig.Gate.Tick(0.1f); // a second tick must not re-broadcast

            Assert.Equal(GateStatus.Aborted, rig.Gate.Status);
            Assert.Equal(GateAbortReason.ContentMismatch, rig.Gate.AbortReason);
            var aborted = Assert.IsType<EncounterAborted>(Assert.Single(rig.SentValues(), v => v is EncounterAborted));
            Assert.Equal(EncounterAbortReason.ContentMismatch, aborted.Reason);
        }

        [Fact]
        public void A_client_load_failure_aborts()
        {
            var rig = new HostRig();
            rig.Gate.Open();

            rig.Channel.Deliver(new SessionPeerId(ClientPeer),
                EncounterCodec.Encode(new ArenaLoadFailed(Enc, "bundle missing")));
            rig.Gate.Tick(0.1f);

            Assert.Equal(GateAbortReason.LoadFailed, rig.Gate.AbortReason);
            Assert.Contains(rig.SentValues(), v => v is EncounterAborted);
        }

        [Fact]
        public void A_silent_peer_times_out_into_an_abort()
        {
            var rig = new HostRig(timeoutSeconds: 5f);
            rig.Gate.Open();

            rig.Gate.Tick(4.9f);
            Assert.Equal(GateStatus.Waiting, rig.Gate.Status);
            rig.Gate.Tick(0.2f);

            Assert.Equal(GateAbortReason.Timeout, rig.Gate.AbortReason);
            var aborted = (EncounterAborted)rig.SentValues().Single(v => v is EncounterAborted);
            Assert.Equal(EncounterAbortReason.Timeout, aborted.Reason);
        }

        [Fact]
        public void A_ready_for_another_encounter_or_from_a_non_member_changes_nothing()
        {
            var rig = new HostRig();
            rig.Gate.Open();

            rig.Channel.Deliver(new SessionPeerId(ClientPeer),
                EncounterCodec.Encode(new ArenaReady(new EncounterId(99), Manifest())));
            rig.Channel.Deliver(new SessionPeerId(7), // not a roster member
                EncounterCodec.Encode(new ArenaReady(Enc, Manifest())));
            rig.Channel.Deliver(new SessionPeerId(ClientPeer), new EncodedPayload(new byte[] { 250, 1 }));
            rig.Gate.Tick(0.1f);

            Assert.Equal(GateStatus.Waiting, rig.Gate.Status);
        }

        // ---------------------------------------------------------------- client flow

        private sealed class ClientRig
        {
            public readonly FakeChannel Channel = new FakeChannel();
            public readonly ClientEncounterFlow Flow;

            public ClientRig() => Flow = new ClientEncounterFlow(
                Channel, new FakeSession(SessionRole.Client, localPeer: ClientPeer, hostPeer: HostPeer));

            public List<(object Value, MessageTarget Target)> Replies() =>
                Channel.Sent.Select(s => (EncounterCodec.Decode(s.Payload).Value, s.Target)).ToList();
        }

        [Fact]
        public void EnterArena_from_the_host_runs_the_load_and_reports_ready_upstream()
        {
            var rig = new ClientRig();
            EnterArena? seen = null;
            rig.Flow.OnEnterArena = e =>
            {
                seen = e;
                return ClientLoadOutcome.Ready(Manifest());
            };

            rig.Channel.Deliver(new SessionPeerId(HostPeer),
                EncounterCodec.Encode(new EnterArena(Enc, Manifest(), Origin)));

            Assert.NotNull(seen);
            Assert.Equal(Origin, seen!.Origin);
            var (value, target) = Assert.Single(rig.Replies());
            var ready = Assert.IsType<ArenaReady>(value);
            Assert.Equal(Enc, ready.Encounter);
            Assert.Equal(MessageTargetKind.Host, target.Kind);
        }

        [Fact]
        public void A_failed_local_load_reports_ArenaLoadFailed()
        {
            var rig = new ClientRig();
            rig.Flow.OnEnterArena = _ => ClientLoadOutcome.Failed("artifact missing");

            rig.Channel.Deliver(new SessionPeerId(HostPeer),
                EncounterCodec.Encode(new EnterArena(Enc, Manifest(), Origin)));

            var failed = Assert.IsType<ArenaLoadFailed>(Assert.Single(rig.Replies()).Value);
            Assert.Equal("artifact missing", failed.Reason);
        }

        [Fact]
        public void A_non_finite_origin_is_refused_without_running_the_load()
        {
            var rig = new ClientRig();
            var loadRan = false;
            rig.Flow.OnEnterArena = _ =>
            {
                loadRan = true;
                return ClientLoadOutcome.Ready(Manifest());
            };

            rig.Channel.Deliver(new SessionPeerId(HostPeer),
                EncounterCodec.Encode(new EnterArena(Enc, Manifest(), new WorldPosition(float.NaN, 0f, 0f))));

            Assert.False(loadRan);
            Assert.IsType<ArenaLoadFailed>(Assert.Single(rig.Replies()).Value);
        }

        [Fact]
        public void Control_messages_from_a_non_host_sender_are_ignored()
        {
            var rig = new ClientRig();
            var called = false;
            rig.Flow.OnEnterArena = _ =>
            {
                called = true;
                return ClientLoadOutcome.Ready(Manifest());
            };

            rig.Channel.Deliver(new SessionPeerId(7),
                EncounterCodec.Encode(new EnterArena(Enc, Manifest(), Origin)));

            Assert.False(called);
            Assert.Empty(rig.Replies());
        }

        [Fact]
        public void Aborted_and_Ended_reach_their_callbacks_and_disposal_unsubscribes()
        {
            var rig = new ClientRig();
            EncounterAborted? aborted = null;
            EncounterEnded? ended = null;
            rig.Flow.OnAborted = a => aborted = a;
            rig.Flow.OnEnded = e => ended = e;

            rig.Channel.Deliver(new SessionPeerId(HostPeer),
                EncounterCodec.Encode(new EncounterAborted(Enc, EncounterAbortReason.Timeout)));
            rig.Channel.Deliver(new SessionPeerId(HostPeer),
                EncounterCodec.Encode(new EncounterEnded(Enc, new SimulationTick(9))));

            Assert.Equal(EncounterAbortReason.Timeout, aborted!.Reason);
            Assert.Equal(Enc, ended!.Encounter);

            rig.Flow.Dispose();
            ended = null;
            rig.Channel.Deliver(new SessionPeerId(HostPeer),
                EncounterCodec.Encode(new EncounterEnded(Enc, new SimulationTick(10))));
            Assert.Null(ended);
        }
    }
}
