using System.IO;
using System.Linq;
using FalseGods.Core.Arena;
using FalseGods.Core.Bosses;
using FalseGods.Core.Simulation;
using FalseGods.Protocol.Arena;
using FalseGods.Protocol.Wire;
using Xunit;

namespace FalseGods.ProtocolTests
{
    public sealed class WireCodecTests
    {
        private static BossSnapshot SampleBoss(bool withOptionals = true) => new BossSnapshot(
            new EncounterId(42),
            new BossInstanceId(7),
            new DefinitionId(3),
            ProtocolVersion.Current,
            new SimulationTick(100),
            PhaseId: 2,
            StateId: 3,
            StateStartTick: new SimulationTick(90),
            ActiveAttack: withOptionals ? new AttackInstanceId(5) : (AttackInstanceId?)null,
            ActiveAttackDefinitionId: withOptionals ? 11 : (int?)null,
            Target: withOptionals ? new ParticipantId(2) : (ParticipantId?)null,
            Position: new SimVector2(1.5f, -2.5f),
            Facing: new SimVector2(0f, 1f),
            Health: 40,
            MaxHealth: 100,
            WeakPointExposed: true,
            LastProcessedBossEventSequence: new Sequence(9));

        private static ArenaSnapshot SampleArena() => new ArenaSnapshot(
            new EncounterId(42),
            "false_gods.arena.poc",
            ArenaVersion: 1,
            ProtocolVersion.Current,
            new SimulationTick(100),
            new[] { new MechanismGroupId("phase_2"), new MechanismGroupId("hazards") },
            ExitUnlocked: true,
            new Sequence(4));

        private static ContentHash SampleHash()
        {
            var bytes = new byte[ContentHash.Sha256Length];
            for (var i = 0; i < bytes.Length; i++)
            {
                bytes[i] = (byte)(i * 7);
            }

            return new ContentHash(bytes);
        }

        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public void BossSnapshot_round_trips(bool withOptionals)
        {
            var original = SampleBoss(withOptionals);
            var decoded = WireCodec.DeserializeBossSnapshot(WireCodec.Serialize(original));
            Assert.Equal(original, decoded); // record value equality over all value-type fields
        }

        [Fact]
        public void ArenaSnapshot_round_trips()
        {
            var original = SampleArena();
            var decoded = WireCodec.DeserializeArenaSnapshot(WireCodec.Serialize(original));

            Assert.Equal(original.Encounter, decoded.Encounter);
            Assert.Equal(original.ArenaId, decoded.ArenaId);
            Assert.Equal(original.ArenaVersion, decoded.ArenaVersion);
            Assert.Equal(original.Tick, decoded.Tick);
            Assert.True(original.ActiveMechanismGroups.SequenceEqual(decoded.ActiveMechanismGroups));
            Assert.Equal(original.ExitUnlocked, decoded.ExitUnlocked);
            Assert.Equal(original.LastProcessedArenaEventSequence, decoded.LastProcessedArenaEventSequence);
        }

        [Fact]
        public void ArenaSnapshot_with_no_active_groups_round_trips()
        {
            var original = new ArenaSnapshot(
                new EncounterId(1), "a", 1, ProtocolVersion.Current, new SimulationTick(0),
                new MechanismGroupId[0], false, new Sequence(0));

            var decoded = WireCodec.DeserializeArenaSnapshot(WireCodec.Serialize(original));

            Assert.Empty(decoded.ActiveMechanismGroups);
            Assert.False(decoded.ExitUnlocked);
        }

        [Fact]
        public void EncounterBaseline_round_trips_including_both_snapshots_and_the_content_hash()
        {
            var original = new EncounterBaseline(
                new EncounterId(42),
                ProtocolVersion.Current,
                "false_gods.arena.poc",
                1,
                SampleHash(),
                new WorldPosition(102.5f, -3f, 88.25f),
                new SimulationTick(100),
                EncounterPhaseId: 1,
                Boss: SampleBoss(),
                Arena: SampleArena(),
                LastProcessedBossEventSequence: new Sequence(9),
                LastProcessedArenaEventSequence: new Sequence(4));

            var decoded = WireCodec.DeserializeBaseline(WireCodec.Serialize(original));

            Assert.Equal(original.Encounter, decoded.Encounter);
            Assert.Equal(original.ArenaId, decoded.ArenaId);
            Assert.Equal(original.ContentHash, decoded.ContentHash);
            Assert.Equal(original.EncounterPhaseId, decoded.EncounterPhaseId);
            Assert.Equal(original.Boss, decoded.Boss); // BossSnapshot value equality
            Assert.True(original.Arena.ActiveMechanismGroups.SequenceEqual(decoded.Arena.ActiveMechanismGroups));
            Assert.Equal(original.LastProcessedBossEventSequence, decoded.LastProcessedBossEventSequence);
            Assert.Equal(original.LastProcessedArenaEventSequence, decoded.LastProcessedArenaEventSequence);
        }

        public static object[][] BossEvents() => new[]
        {
            new object[] { new BossAppearedEvent(new Sequence(0), new SimulationTick(1), 1) },
            new object[] { new BossAttackTelegraphedEvent(new Sequence(1), new SimulationTick(2), new AttackInstanceId(3), 5, new SimVector2(2f, 3f), 1.25f) },
            new object[] { new BossAttackCommittedEvent(new Sequence(2), new SimulationTick(3), new AttackInstanceId(3), 5, new SimVector2(2f, 3f)) },
            new object[] { new BossPhaseChangedEvent(new Sequence(3), new SimulationTick(4), 2) },
            new object[] { new BossWeakPointChangedEvent(new Sequence(4), new SimulationTick(5), true) },
            new object[] { new BossDamagedEvent(new Sequence(5), new SimulationTick(6), 30, 70, true) },
            new object[] { new BossDefeatedEvent(new Sequence(6), new SimulationTick(7)) },
        };

        [Theory]
        [MemberData(nameof(BossEvents))]
        public void Every_boss_event_round_trips(IBossWireEvent original)
        {
            var decoded = WireCodec.DeserializeBossEvent(WireCodec.Serialize(original));
            Assert.Equal(original, decoded);
        }

        public static object[][] ArenaEvents() => new[]
        {
            new object[] { new ArenaMechanismGroupActivatedEvent(new Sequence(0), new SimulationTick(1), new MechanismGroupId("phase_2")) },
            new object[] { new ArenaExitUnlockedEvent(new Sequence(1), new SimulationTick(2)) },
        };

        [Theory]
        [MemberData(nameof(ArenaEvents))]
        public void Every_arena_event_round_trips(IArenaWireEvent original)
        {
            var decoded = WireCodec.DeserializeArenaEvent(WireCodec.Serialize(original));
            Assert.Equal(original, decoded);
        }

        [Fact]
        public void A_truncated_payload_throws_rather_than_over_reading()
        {
            Assert.Throws<EndOfStreamException>(() => WireCodec.DeserializeBossSnapshot(new byte[3]));
        }

        [Fact]
        public void Trailing_bytes_after_a_value_are_rejected()
        {
            var payload = WireCodec.Serialize(SampleBoss()).Concat(new byte[] { 0xFF }).ToArray();
            Assert.Throws<InvalidDataException>(() => WireCodec.DeserializeBossSnapshot(payload));
        }

        [Fact]
        public void An_unknown_boss_event_tag_is_rejected()
        {
            var payload = WireCodec.Serialize(new BossDefeatedEvent(new Sequence(0), new SimulationTick(0)));
            payload[0] = 0x7F; // clobber the tag
            Assert.Throws<InvalidDataException>(() => WireCodec.DeserializeBossEvent(payload));
        }
    }
}
