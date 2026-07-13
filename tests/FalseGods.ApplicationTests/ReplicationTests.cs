using System;
using System.IO;
using FalseGods.Application.Replication;
using FalseGods.Core.Arena;
using FalseGods.Core.Bosses;
using FalseGods.Core.Simulation;
using FalseGods.Protocol.Arena;
using FalseGods.Protocol.Wire;
using FalseGods.RuntimeContracts.Multiplayer;
using FalseGods.RuntimeContracts.Transport;
using Xunit;

namespace FalseGods.ApplicationTests
{
    public sealed class ReplicationTests
    {
        private static readonly EncounterId Enc = new EncounterId(1);

        private static BossSnapshot BossSnap(long tick, long lastSeq = 0) => new BossSnapshot(
            Enc, new BossInstanceId(7), new DefinitionId(1), ProtocolVersion.Current, new SimulationTick(tick),
            PhaseId: 1, StateId: 0, StateStartTick: new SimulationTick(tick), ActiveAttack: null,
            ActiveAttackDefinitionId: null, Target: null, Position: new SimVector2(1f, 2f),
            Facing: SimVector2.Zero, Health: 100, MaxHealth: 100, WeakPointExposed: false,
            LastProcessedBossEventSequence: new Sequence(lastSeq));

        private static ArenaSnapshot ArenaSnap(long tick, long lastSeq = 0) => new ArenaSnapshot(
            Enc, "a", 1, ProtocolVersion.Current, new SimulationTick(tick),
            new MechanismGroupId[0], false, new Sequence(lastSeq));

        private static IBossWireEvent BossEvt(long seq) => new BossPhaseChangedEvent(new Sequence(seq), new SimulationTick(seq), 2);

        private static IArenaWireEvent ArenaEvt(long seq) => new ArenaExitUnlockedEvent(new Sequence(seq), new SimulationTick(seq));

        // ---------------------------------------------------------------- codec

        [Fact]
        public void EncounterCodec_tags_and_round_trips_each_kind()
        {
            AssertKind(ReplicationKind.BossSnapshot, EncounterCodec.Decode(EncounterCodec.Encode(BossSnap(5))));
            AssertKind(ReplicationKind.ArenaSnapshot, EncounterCodec.Decode(EncounterCodec.Encode(ArenaSnap(5))));
            AssertKind(ReplicationKind.BossEvent, EncounterCodec.Decode(EncounterCodec.Encode(BossEvt(0))));
            AssertKind(ReplicationKind.ArenaEvent, EncounterCodec.Decode(EncounterCodec.Encode(ArenaEvt(0))));

            var boss = BossSnap(9);
            var decoded = EncounterCodec.Decode(EncounterCodec.Encode(boss));
            Assert.Equal(boss, Assert.IsType<BossSnapshot>(decoded.Value));
        }

        [Fact]
        public void EncounterCodec_rejects_an_empty_payload()
        {
            Assert.Throws<InvalidDataException>(() => EncounterCodec.Decode(new EncodedPayload(Array.Empty<byte>())));
        }

        private static void AssertKind(ReplicationKind expected, DecodedMessage message) => Assert.Equal(expected, message.Kind);

        // ---------------------------------------------------------------- sender

        [Fact]
        public void The_host_broadcasts_snapshots_unreliably_and_events_reliably()
        {
            var channel = new FakeChannel();
            var sender = new ReplicationSender(channel, new FakeSession(SessionRole.Host));

            sender.BroadcastBossSnapshot(BossSnap(1));
            sender.BroadcastBossEvent(BossEvt(0));
            sender.SendBaseline(Baseline(), new SessionPeerId(3));

            Assert.Equal(MessageDelivery.Unreliable, channel.Sent[0].Delivery);
            Assert.Equal(MessageTargetKind.AllClients, channel.Sent[0].Target.Kind);
            Assert.Equal(MessageDelivery.ReliableOrdered, channel.Sent[1].Delivery);
            Assert.Equal(MessageTargetKind.SpecificPeer, channel.Sent[2].Target.Kind);
            Assert.Equal(new SessionPeerId(3), channel.Sent[2].Target.Peer);
        }

        [Fact]
        public void A_client_must_not_broadcast()
        {
            var sender = new ReplicationSender(new FakeChannel(), new FakeSession(SessionRole.Client));
            Assert.Throws<InvalidOperationException>(() => sender.BroadcastBossSnapshot(BossSnap(1)));
        }

        // ---------------------------------------------------------------- receiver idempotence

        [Fact]
        public void A_duplicate_boss_event_is_applied_once()
        {
            var receiver = new ReplicationReceiver(new FakeChannel());
            var payload = EncounterCodec.Encode(BossEvt(0));

            receiver.Apply(payload);
            receiver.Apply(payload);

            Assert.Single(receiver.AppliedBossEvents);
        }

        [Fact]
        public void A_dropped_arena_event_does_not_stall_the_boss_stream_and_the_streams_are_independent()
        {
            var receiver = new ReplicationReceiver(new FakeChannel());

            receiver.Apply(EncounterCodec.Encode(BossEvt(0)));
            receiver.Apply(EncounterCodec.Encode(ArenaEvt(0)));
            receiver.Apply(EncounterCodec.Encode(ArenaEvt(2))); // gap at arena seq 1 — no stall
            receiver.Apply(EncounterCodec.Encode(BossEvt(1)));

            Assert.Equal(2, receiver.AppliedBossEvents.Count);
            Assert.Equal(2, receiver.AppliedArenaEvents.Count);
        }

        [Fact]
        public void A_snapshot_applies_the_latest_by_tick_and_ignores_a_stale_one()
        {
            var receiver = new ReplicationReceiver(new FakeChannel());

            receiver.Apply(EncounterCodec.Encode(BossSnap(5)));
            receiver.Apply(EncounterCodec.Encode(BossSnap(3))); // stale
            Assert.Equal(5, receiver.LatestBossSnapshot!.Tick.Value);

            receiver.Apply(EncounterCodec.Encode(BossSnap(7)));
            Assert.Equal(7, receiver.LatestBossSnapshot!.Tick.Value);
        }

        [Fact]
        public void A_re_committed_attack_lands_its_effect_only_once_by_attack_instance_id()
        {
            var receiver = new ReplicationReceiver(new FakeChannel());
            var attack = new AttackInstanceId(3);

            receiver.Apply(EncounterCodec.Encode(new BossAttackCommittedEvent(new Sequence(0), new SimulationTick(0), attack, 1, SimVector2.Zero)));
            receiver.Apply(EncounterCodec.Encode(new BossAttackCommittedEvent(new Sequence(1), new SimulationTick(1), attack, 1, SimVector2.Zero)));

            Assert.Equal(2, receiver.AppliedBossEvents.Count); // distinct sequences, both applied
            Assert.Equal(1, receiver.CommittedAttackCount);    // but the landing effect is deduped by attack id
        }

        // ---------------------------------------------------------------- baseline (B8)

        [Fact]
        public void One_baseline_restores_state_and_resumes_each_stream_at_its_carried_sequence()
        {
            var channel = new FakeChannel();
            var receiver = new ReplicationReceiver(channel);

            channel.Deliver(new SessionPeerId(0), EncounterCodec.Encode(Baseline()));

            Assert.True(receiver.HasBaseline);
            Assert.Equal(BossSnap(4).Boss, receiver.LatestBossSnapshot!.Boss);

            // Events already reflected in the baseline (<= its LastProcessed sequences) are dropped; later resume.
            receiver.Apply(EncounterCodec.Encode(BossEvt(3))); // == boss floor, dropped
            receiver.Apply(EncounterCodec.Encode(BossEvt(4))); // > floor, applied
            Assert.Single(receiver.AppliedBossEvents);
            Assert.Equal(4, receiver.AppliedBossEvents[0].Sequence.Value);
        }

        [Fact]
        public void A_second_baseline_is_ignored()
        {
            var receiver = new ReplicationReceiver(new FakeChannel());
            receiver.Apply(EncounterCodec.Encode(Baseline()));
            receiver.Apply(EncounterCodec.Encode(Baseline(bossFloor: 99)));

            receiver.Apply(EncounterCodec.Encode(BossEvt(4)));
            Assert.Single(receiver.AppliedBossEvents); // floor stayed at 3, so seq 4 still applied
        }

        private static EncounterBaseline Baseline(long bossFloor = 3) => new EncounterBaseline(
            Enc, ProtocolVersion.Current, "a", 1, default, new SimulationTick(4), EncounterPhaseId: 1,
            Boss: BossSnap(4), Arena: ArenaSnap(4),
            LastProcessedBossEventSequence: new Sequence(bossFloor),
            LastProcessedArenaEventSequence: new Sequence(0));
    }
}
