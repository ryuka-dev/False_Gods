using System;
using System.Linq;
using FalseGods.Application.Replication;
using FalseGods.Core.Arena;
using FalseGods.Core.Encounters;
using FalseGods.Core.Simulation;
using FalseGods.Protocol.Arena;
using FalseGods.Protocol.Wire;
using FalseGods.RuntimeContracts.Multiplayer;
using FalseGods.RuntimeContracts.Transport;
using Xunit;

namespace FalseGods.ApplicationTests
{
    /// <summary>
    /// The arena half of host replication (ADR-005: an independent, sequenced stream beside the boss stream)
    /// and the receiver's untrusted-input validation (sender, decode, protocol version — DependencyRules §12).
    /// </summary>
    public sealed class ArenaReplicationTests
    {
        private const int HostPeer = 0;
        private const int ClientPeer = 1;

        private readonly FakeChannel _channel = new FakeChannel();
        private readonly BossFixture _fixture = new BossFixture();

        private EncounterHostReplication CreateDriver(FakeRoster roster) => new EncounterHostReplication(
            new ReplicationSender(_channel, new FakeSession(SessionRole.Host, HostPeer)),
            new FakeSession(SessionRole.Host, HostPeer),
            roster,
            new EncounterId(3),
            new DefinitionId(9),
            EncounterHostReplicationTests.TestManifest());

        private static SimulationTick Tick(long value) => new SimulationTick(value);

        [Fact]
        public void Arena_events_ride_their_own_sequence_space()
        {
            var driver = CreateDriver(new FakeRoster(HostPeer));
            var arena = new ArenaSimulation();
            _fixture.Boss.Spawn(new SimVector2(0f, 0f));
            var bossEvents = _fixture.Boss.DrainEvents();
            arena.ActivateMechanismGroup(new MechanismGroupId("phase_2"));
            arena.UnlockExit();
            var arenaEvents = arena.DrainEvents();

            driver.Publish(_fixture.Boss, bossEvents, arena, arenaEvents, EncounterPhase.Fighting, Tick(1));

            var decoded = _channel.Sent.Select(s => (Message: EncounterCodec.Decode(s.Payload), s.Delivery)).ToList();
            var wireArenaEvents = decoded
                .Where(d => d.Message.Kind == ReplicationKind.ArenaEvent)
                .Select(d => (IArenaWireEvent)d.Message.Value)
                .ToList();

            // Both streams start at sequence 0 — one authority each, never a shared counter.
            Assert.Equal(new long[] { 0, 1 }, wireArenaEvents.Select(e => e.Sequence.Value));
            Assert.Equal(0, decoded
                .Where(d => d.Message.Kind == ReplicationKind.BossEvent)
                .Select(d => ((IBossWireEvent)d.Message.Value).Sequence.Value)
                .First());
            Assert.All(
                decoded.Where(d => d.Message.Kind == ReplicationKind.ArenaEvent),
                d => Assert.Equal(MessageDelivery.ReliableOrdered, d.Delivery));

            // The arena snapshot goes out unreliably and acknowledges the stamped arena events.
            var arenaSnapshot = (ArenaSnapshot)decoded.Single(d => d.Message.Kind == ReplicationKind.ArenaSnapshot).Message.Value;
            Assert.Equal(driver.LastArenaSequence, arenaSnapshot.LastProcessedArenaEventSequence);
            Assert.True(arenaSnapshot.ExitUnlocked);
            Assert.Contains(new MechanismGroupId("phase_2"), arenaSnapshot.ActiveMechanismGroups);
        }

        [Fact]
        public void Baseline_carries_real_arena_state_hash_and_encounter_phase()
        {
            var roster = new FakeRoster(HostPeer);
            var driver = CreateDriver(roster);
            var arena = new ArenaSimulation();
            _fixture.Boss.Spawn(new SimVector2(0f, 0f));
            driver.Publish(_fixture.Boss, _fixture.Boss.DrainEvents(), arena, arena.DrainEvents(), EncounterPhase.Fighting, Tick(1));

            arena.ActivateMechanismGroup(new MechanismGroupId("phase_2"));
            var arenaEvents = arena.DrainEvents();
            driver.Publish(_fixture.Boss, Array.Empty<FalseGods.Core.Bosses.Events.IBossDomainEvent>(), arena, arenaEvents, EncounterPhase.Fighting, Tick(2));

            roster.Add(ClientPeer);
            driver.Publish(_fixture.Boss, Array.Empty<FalseGods.Core.Bosses.Events.IBossDomainEvent>(), arena, Array.Empty<FalseGods.Core.Arena.Events.IArenaDomainEvent>(), EncounterPhase.Fighting, Tick(3));

            var baseline = (EncounterBaseline)_channel.Sent
                .Select(s => EncounterCodec.Decode(s.Payload))
                .Single(m => m.Kind == ReplicationKind.EncounterBaseline).Value;

            var manifest = EncounterHostReplicationTests.TestManifest();
            Assert.Equal(manifest.ContentHash, baseline.ContentHash);
            Assert.Equal(manifest.ArenaId, baseline.ArenaId);
            Assert.Equal((int)EncounterPhase.Fighting, baseline.EncounterPhaseId);
            Assert.Contains(new MechanismGroupId("phase_2"), baseline.Arena.ActiveMechanismGroups);
            // The arena floor covers the mechanism event already reflected in the baseline's snapshot.
            Assert.Equal(driver.LastArenaSequence, baseline.LastProcessedArenaEventSequence);
        }

        // ---------------------------------------------------------------- receiver validation

        private EncodedPayload HostSnapshotPayload()
        {
            var driver = CreateDriver(new FakeRoster(HostPeer));
            _fixture.Boss.Spawn(new SimVector2(0f, 0f));
            driver.Publish(_fixture.Boss, _fixture.Boss.DrainEvents(), Tick(1));
            return _channel.Sent.First(s => EncounterCodec.Decode(s.Payload).Kind == ReplicationKind.BossSnapshot).Payload;
        }

        [Fact]
        public void A_payload_from_a_non_host_sender_is_dropped()
        {
            var channel = new FakeChannel();
            var receiver = new ReplicationReceiver(channel, new FakeSession(SessionRole.Client, localPeer: 1, hostPeer: 0));
            var payload = HostSnapshotPayload();

            channel.Deliver(new SessionPeerId(7), payload); // an impostor peer replays host state

            Assert.Null(receiver.LatestBossSnapshot);
            Assert.Equal(1, receiver.DroppedFromNonHost);

            channel.Deliver(new SessionPeerId(HostPeer), payload);
            Assert.NotNull(receiver.LatestBossSnapshot);
        }

        [Fact]
        public void A_malformed_payload_is_dropped_not_thrown()
        {
            var channel = new FakeChannel();
            var receiver = new ReplicationReceiver(channel, new FakeSession(SessionRole.Client, localPeer: 1, hostPeer: 0));

            channel.Deliver(new SessionPeerId(HostPeer), new EncodedPayload(new byte[] { 1, 2, 3 }));

            Assert.Equal(1, receiver.DroppedMalformed);
            Assert.Null(receiver.LatestBossSnapshot);
        }

        [Fact]
        public void A_foreign_protocol_version_is_dropped()
        {
            var receiver = new ReplicationReceiver(new FakeChannel(), new FakeSession(SessionRole.Client));
            var foreign = new ArenaSnapshot(
                new EncounterId(3), "a", 1, new ProtocolVersion(99), Tick(1),
                Array.Empty<MechanismGroupId>(), ExitUnlocked: false, new Sequence(-1));

            receiver.Apply(EncounterCodec.Encode(foreign));

            Assert.Null(receiver.LatestArenaSnapshot);
            Assert.Equal(1, receiver.DroppedVersionMismatch);
        }

        [Fact]
        public void Control_messages_on_the_channel_are_ignored_by_the_replication_receiver()
        {
            var receiver = new ReplicationReceiver(new FakeChannel(), new FakeSession(SessionRole.Client));

            receiver.Apply(EncounterCodec.Encode(new EncounterEnded(new EncounterId(3), Tick(9))));
            receiver.Apply(EncounterCodec.Encode(new ArenaLoadFailed(new EncounterId(3), "reason")));

            Assert.False(receiver.HasBaseline);
            Assert.Empty(receiver.AppliedBossEvents);
            Assert.Empty(receiver.AppliedArenaEvents);
            Assert.Equal(0, receiver.DroppedMalformed);
        }
    }
}
