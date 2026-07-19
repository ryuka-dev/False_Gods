using System;
using System.Collections.Generic;
using System.Linq;
using FalseGods.Application.Replication;
using FalseGods.Core.Simulation;
using FalseGods.Protocol.Wire;
using FalseGods.RuntimeContracts.Multiplayer;
using FalseGods.RuntimeContracts.Transport;
using Xunit;

namespace FalseGods.ApplicationTests
{
    /// <summary>
    /// The host replication driver: boss-stream sequences are stamped once and monotonically, snapshots follow
    /// events, and every newly-seen peer gets exactly one <c>EncounterBaseline</c> — before that tick's events —
    /// while the local host peer never gets one (Docs/OriginalBossNetworkingArchitecture.md §9.6/§9.8).
    /// </summary>
    public sealed class EncounterHostReplicationTests
    {
        private const int HostPeer = 0;
        private const int ClientPeer = 1;

        private readonly FakeChannel _channel = new FakeChannel();
        private readonly BossFixture _fixture = new BossFixture();

        internal static Protocol.Arena.ArenaManifest TestManifest()
        {
            var bytes = new byte[Protocol.Arena.ContentHash.Sha256Length];
            for (var i = 0; i < bytes.Length; i++)
            {
                bytes[i] = (byte)(i + 1);
            }

            return new Protocol.Arena.ArenaManifest(
                "false_gods.arena.none",
                ArenaVersion: 0,
                new Protocol.Arena.ContentHashSchemaVersion(1),
                new Protocol.Arena.ContentHash(bytes),
                ProtocolVersion.Current.Value,
                BundleVersion: "test.bundle");
        }

        private EncounterHostReplication CreateDriver(FakeRoster roster) => new EncounterHostReplication(
            new ReplicationSender(_channel, new FakeSession(SessionRole.Host, HostPeer)),
            new FakeSession(SessionRole.Host, HostPeer),
            roster,
            new EncounterId(3),
            new DefinitionId(9),
            TestManifest());

        private static SimulationTick Tick(long value) => new SimulationTick(value);

        [Fact]
        public void PublishBroadcastsEventsReliablyThenSnapshotUnreliablyWithMonotonicSequences()
        {
            var driver = CreateDriver(new FakeRoster(HostPeer));
            _fixture.Boss.Spawn(new SimVector2(0f, 0f));
            var spawnEvents = _fixture.Boss.DrainEvents();
            Assert.NotEmpty(spawnEvents);

            driver.Publish(_fixture.Boss, spawnEvents, Tick(1));
            var moreEvents = _fixture.Step(2f); // idle elapses -> a telegraph event
            Assert.NotEmpty(moreEvents);
            driver.Publish(_fixture.Boss, moreEvents, Tick(2));

            var decoded = _channel.Sent.Select(s => (Message: EncounterCodec.Decode(s.Payload), s.Delivery, s.Target)).ToList();

            var events = decoded.Where(d => d.Message.Kind == ReplicationKind.BossEvent).ToList();
            Assert.Equal(spawnEvents.Count + moreEvents.Count, events.Count);
            Assert.All(events, e =>
            {
                Assert.Equal(MessageDelivery.ReliableOrdered, e.Delivery);
                Assert.Equal(MessageTargetKind.AllClients, e.Target.Kind);
            });
            var sequences = events.Select(e => ((IBossWireEvent)e.Message.Value).Sequence.Value).ToList();
            Assert.Equal(Enumerable.Range(0, events.Count).Select(i => (long)i), sequences);

            var snapshots = decoded.Where(d => d.Message.Kind == ReplicationKind.BossSnapshot).ToList();
            Assert.Equal(2, snapshots.Count);
            Assert.All(snapshots, s =>
            {
                Assert.Equal(MessageDelivery.Unreliable, s.Delivery);
                Assert.Equal(MessageTargetKind.AllClients, s.Target.Kind);
            });

            // The final snapshot acknowledges every event stamped so far.
            var last = (BossSnapshot)snapshots.Last().Message.Value;
            Assert.Equal(events.Count - 1, last.LastProcessedBossEventSequence.Value);
            Assert.Equal(driver.LastBossSequence, last.LastProcessedBossEventSequence);
        }

        [Fact]
        public void NewPeerGetsExactlyOneBaselineBeforeThatTicksEvents()
        {
            var roster = new FakeRoster(HostPeer);
            var driver = CreateDriver(roster);
            _fixture.Boss.Spawn(new SimVector2(0f, 0f));
            driver.Publish(_fixture.Boss, _fixture.Boss.DrainEvents(), Tick(1));
            Assert.DoesNotContain(_channel.Sent, s => EncounterCodec.Decode(s.Payload).Kind == ReplicationKind.EncounterBaseline);

            roster.Add(ClientPeer);
            var joinTickEvents = _fixture.Step(2f);
            Assert.NotEmpty(joinTickEvents);
            var sentBefore = _channel.Sent.Count;
            driver.Publish(_fixture.Boss, joinTickEvents, Tick(2));

            var joinTickSends = _channel.Sent.Skip(sentBefore)
                .Select(s => (Message: EncounterCodec.Decode(s.Payload), s.Delivery, s.Target)).ToList();

            // The baseline is the FIRST send of the tick, reliable, addressed to the joining peer only.
            var first = joinTickSends.First();
            Assert.Equal(ReplicationKind.EncounterBaseline, first.Message.Kind);
            Assert.Equal(MessageDelivery.ReliableOrdered, first.Delivery);
            Assert.Equal(MessageTargetKind.SpecificPeer, first.Target.Kind);
            Assert.Equal(new SessionPeerId(ClientPeer), first.Target.Peer);

            // Its boss floor is the last sequence emitted BEFORE this tick, so the tick's own events survive it.
            var baseline = (EncounterBaseline)first.Message.Value;
            var firstEventThisTick = joinTickSends
                .Where(d => d.Message.Kind == ReplicationKind.BossEvent)
                .Select(d => ((IBossWireEvent)d.Message.Value).Sequence)
                .First();
            Assert.True(baseline.LastProcessedBossEventSequence < firstEventThisTick);

            // Later ticks do not re-send it.
            driver.Publish(_fixture.Boss, _fixture.Step(0.1f), Tick(3));
            var baselines = _channel.Sent.Count(s => EncounterCodec.Decode(s.Payload).Kind == ReplicationKind.EncounterBaseline);
            Assert.Equal(1, baselines);
        }

        [Fact]
        public void PeerThatLeavesAndRejoinsIsBaselinedAgain()
        {
            var roster = new FakeRoster(HostPeer, ClientPeer);
            var driver = CreateDriver(roster);
            _fixture.Boss.Spawn(new SimVector2(0f, 0f));

            driver.Publish(_fixture.Boss, _fixture.Boss.DrainEvents(), Tick(1));
            roster.Remove(ClientPeer);
            driver.Publish(_fixture.Boss, Array.Empty<FalseGods.Core.Bosses.Events.IBossDomainEvent>(), Tick(2));
            roster.Add(ClientPeer);
            driver.Publish(_fixture.Boss, Array.Empty<FalseGods.Core.Bosses.Events.IBossDomainEvent>(), Tick(3));

            var baselines = _channel.Sent
                .Select(s => (Message: EncounterCodec.Decode(s.Payload), s.Target))
                .Where(d => d.Message.Kind == ReplicationKind.EncounterBaseline)
                .ToList();
            Assert.Equal(2, baselines.Count);
            Assert.All(baselines, b => Assert.Equal(new SessionPeerId(ClientPeer), b.Target.Peer));
        }

        [Fact]
        public void TheLocalHostPeerIsNeverBaselined()
        {
            var driver = CreateDriver(new FakeRoster(HostPeer));
            _fixture.Boss.Spawn(new SimVector2(0f, 0f));

            driver.Publish(_fixture.Boss, _fixture.Boss.DrainEvents(), Tick(1));
            driver.Publish(_fixture.Boss, _fixture.Step(0.1f), Tick(2));

            Assert.DoesNotContain(_channel.Sent, s => EncounterCodec.Decode(s.Payload).Kind == ReplicationKind.EncounterBaseline);
        }

        [Fact]
        public void ABaselinedReceiverResumesTheStreamWithoutDuplicatingPreBaselineEvents()
        {
            // End-to-end through real codec + receiver: what a late-joining client actually does with the
            // driver's output.
            var roster = new FakeRoster(HostPeer);
            var driver = CreateDriver(roster);
            _fixture.Boss.Spawn(new SimVector2(0f, 0f));
            driver.Publish(_fixture.Boss, _fixture.Boss.DrainEvents(), Tick(1));

            roster.Add(ClientPeer);
            var joinIndex = _channel.Sent.Count;
            driver.Publish(_fixture.Boss, _fixture.Step(2f), Tick(2));

            var receiver = new ReplicationReceiver(new FakeChannel(), new FakeSession(SessionRole.Client));
            foreach (var send in _channel.Sent.Skip(joinIndex)) // what the joining peer receives, in order
            {
                if (send.Target.Kind == MessageTargetKind.AllClients ||
                    (send.Target.Kind == MessageTargetKind.SpecificPeer && send.Target.Peer == new SessionPeerId(ClientPeer)))
                {
                    receiver.Apply(send.Payload);
                }
            }

            Assert.True(receiver.HasBaseline);
            Assert.NotNull(receiver.LatestBossSnapshot);

            // Only the join tick's events were applied; everything at or below the baseline's floor is covered
            // by the baseline itself.
            var baselineFloor = _channel.Sent.Skip(joinIndex)
                .Select(s => EncounterCodec.Decode(s.Payload))
                .Where(m => m.Kind == ReplicationKind.EncounterBaseline)
                .Select(m => ((EncounterBaseline)m.Value).LastProcessedBossEventSequence)
                .Single();
            Assert.NotEmpty(receiver.AppliedBossEvents);
            Assert.All(receiver.AppliedBossEvents, e => Assert.True(e.Sequence > baselineFloor));
        }

        [Fact]
        public void DisposedReceiverIgnoresLaterDeliveries()
        {
            var channel = new FakeChannel();
            var receiver = new ReplicationReceiver(channel, new FakeSession(SessionRole.Client));
            var driver = CreateDriver(new FakeRoster(HostPeer));
            _fixture.Boss.Spawn(new SimVector2(0f, 0f));
            driver.Publish(_fixture.Boss, _fixture.Boss.DrainEvents(), Tick(1));
            var payload = _channel.Sent.First(s => EncounterCodec.Decode(s.Payload).Kind == ReplicationKind.BossSnapshot).Payload;

            channel.Deliver(new SessionPeerId(HostPeer), payload);
            Assert.NotNull(receiver.LatestBossSnapshot);

            receiver.Dispose();
            var before = receiver.AppliedBossEvents.Count;
            var eventPayload = _channel.Sent.First(s => EncounterCodec.Decode(s.Payload).Kind == ReplicationKind.BossEvent).Payload;
            channel.Deliver(new SessionPeerId(HostPeer), eventPayload);

            Assert.Equal(before, receiver.AppliedBossEvents.Count);
        }
    }
}
