using System;
using System.Collections.Generic;
using System.IO;
using FalseGods.Core.Arena;
using FalseGods.Core.Bosses;
using FalseGods.Core.Simulation;
using FalseGods.Protocol.Arena;

namespace FalseGods.Protocol.Wire
{
    /// <summary>
    /// The serialization contract for the replication wire DTOs: each snapshot, event, and baseline to and from a
    /// self-contained <c>byte[]</c> (Docs/Architecture.md §3 — Protocol owns the serialization contract).
    /// </summary>
    /// <remarks>
    /// It produces a plain <c>byte[]</c> and never an <c>EncodedPayload</c>: that carrier lives in
    /// <c>FalseGods.RuntimeContracts</c>, which Protocol does not reference (FG-ARCH-007). <c>FalseGods.Application</c>
    /// wraps this output into an <c>EncodedPayload</c> with a <c>MessageDelivery</c> for <c>IEncounterChannel</c>
    /// (§5.10). Every <c>Deserialize*</c> treats its buffer as untrusted input: the bounds-checked
    /// <see cref="WireReader"/> throws on truncation, and each call asserts it consumed the whole payload.
    /// </remarks>
    public static class WireCodec
    {
        // Boss event stream tags. Never reused or reordered — a tag is part of the wire contract.
        private const byte BossAppearedTag = 1;
        private const byte BossTelegraphedTag = 2;
        private const byte BossCommittedTag = 3;
        private const byte BossPhaseChangedTag = 4;
        private const byte BossWeakPointTag = 5;
        private const byte BossDamagedTag = 6;
        private const byte BossDefeatedTag = 7;

        // Arena event stream tags, an independent tag space.
        private const byte ArenaMechanismActivatedTag = 1;
        private const byte ArenaExitUnlockedTag = 2;

        // ---------------------------------------------------------------- snapshots

        public static byte[] Serialize(BossSnapshot snapshot)
        {
            var w = new WireWriter();
            WriteBossSnapshot(w, snapshot);
            return w.ToArray();
        }

        public static BossSnapshot DeserializeBossSnapshot(byte[] payload)
        {
            var r = new WireReader(payload);
            var snapshot = ReadBossSnapshot(r);
            RequireEnd(r);
            return snapshot;
        }

        public static byte[] Serialize(ArenaSnapshot snapshot)
        {
            var w = new WireWriter();
            WriteArenaSnapshot(w, snapshot);
            return w.ToArray();
        }

        public static ArenaSnapshot DeserializeArenaSnapshot(byte[] payload)
        {
            var r = new WireReader(payload);
            var snapshot = ReadArenaSnapshot(r);
            RequireEnd(r);
            return snapshot;
        }

        public static byte[] Serialize(EncounterBaseline baseline)
        {
            var w = new WireWriter();
            WriteInt(w, baseline.Encounter.Value);
            WriteInt(w, baseline.ProtocolVersion.Value);
            w.WriteString(baseline.ArenaId);
            WriteInt(w, baseline.ArenaVersion);
            WriteContentHash(w, baseline.ContentHash);
            w.WriteInt64(baseline.Tick.Value);
            WriteInt(w, baseline.EncounterPhaseId);
            WriteBossSnapshot(w, baseline.Boss);
            WriteArenaSnapshot(w, baseline.Arena);
            w.WriteInt64(baseline.LastProcessedBossEventSequence.Value);
            w.WriteInt64(baseline.LastProcessedArenaEventSequence.Value);
            return w.ToArray();
        }

        public static EncounterBaseline DeserializeBaseline(byte[] payload)
        {
            var r = new WireReader(payload);
            var baseline = new EncounterBaseline(
                new EncounterId(r.ReadInt32()),
                new ProtocolVersion(r.ReadInt32()),
                r.ReadString(),
                r.ReadInt32(),
                ReadContentHash(r),
                new SimulationTick(r.ReadInt64()),
                r.ReadInt32(),
                ReadBossSnapshot(r),
                ReadArenaSnapshot(r),
                new Sequence(r.ReadInt64()),
                new Sequence(r.ReadInt64()));
            RequireEnd(r);
            return baseline;
        }

        // ---------------------------------------------------------------- event streams

        public static byte[] Serialize(IBossWireEvent bossEvent)
        {
            var w = new WireWriter();
            WriteBossEvent(w, bossEvent);
            return w.ToArray();
        }

        public static IBossWireEvent DeserializeBossEvent(byte[] payload)
        {
            var r = new WireReader(payload);
            var bossEvent = ReadBossEvent(r);
            RequireEnd(r);
            return bossEvent;
        }

        public static byte[] Serialize(IArenaWireEvent arenaEvent)
        {
            var w = new WireWriter();
            WriteArenaEvent(w, arenaEvent);
            return w.ToArray();
        }

        public static IArenaWireEvent DeserializeArenaEvent(byte[] payload)
        {
            var r = new WireReader(payload);
            var arenaEvent = ReadArenaEvent(r);
            RequireEnd(r);
            return arenaEvent;
        }

        // ---------------------------------------------------------------- snapshot composition

        private static void WriteBossSnapshot(WireWriter w, BossSnapshot s)
        {
            WriteInt(w, s.Encounter.Value);
            WriteInt(w, s.Boss.Value);
            WriteInt(w, s.Definition.Value);
            WriteInt(w, s.ProtocolVersion.Value);
            w.WriteInt64(s.Tick.Value);
            WriteInt(w, s.PhaseId);
            WriteInt(w, s.StateId);
            w.WriteInt64(s.StateStartTick.Value);
            WriteNullableAttack(w, s.ActiveAttack);
            WriteNullableInt(w, s.ActiveAttackDefinitionId);
            WriteNullableParticipant(w, s.Target);
            WriteVector(w, s.Position);
            WriteVector(w, s.Facing);
            WriteInt(w, s.Health);
            WriteInt(w, s.MaxHealth);
            w.WriteBool(s.WeakPointExposed);
            w.WriteInt64(s.LastProcessedBossEventSequence.Value);
        }

        private static BossSnapshot ReadBossSnapshot(WireReader r) => new BossSnapshot(
            new EncounterId(r.ReadInt32()),
            new BossInstanceId(r.ReadInt32()),
            new DefinitionId(r.ReadInt32()),
            new ProtocolVersion(r.ReadInt32()),
            new SimulationTick(r.ReadInt64()),
            r.ReadInt32(),
            r.ReadInt32(),
            new SimulationTick(r.ReadInt64()),
            ReadNullableAttack(r),
            ReadNullableInt(r),
            ReadNullableParticipant(r),
            ReadVector(r),
            ReadVector(r),
            r.ReadInt32(),
            r.ReadInt32(),
            r.ReadBool(),
            new Sequence(r.ReadInt64()));

        private static void WriteArenaSnapshot(WireWriter w, ArenaSnapshot s)
        {
            WriteInt(w, s.Encounter.Value);
            w.WriteString(s.ArenaId);
            WriteInt(w, s.ArenaVersion);
            WriteInt(w, s.ProtocolVersion.Value);
            w.WriteInt64(s.Tick.Value);
            WriteInt(w, s.ActiveMechanismGroups.Count);
            for (var i = 0; i < s.ActiveMechanismGroups.Count; i++)
            {
                w.WriteString(s.ActiveMechanismGroups[i].Value);
            }

            w.WriteBool(s.ExitUnlocked);
            w.WriteInt64(s.LastProcessedArenaEventSequence.Value);
        }

        private static ArenaSnapshot ReadArenaSnapshot(WireReader r)
        {
            var encounter = new EncounterId(r.ReadInt32());
            var arenaId = r.ReadString();
            var arenaVersion = r.ReadInt32();
            var protocol = new ProtocolVersion(r.ReadInt32());
            var tick = new SimulationTick(r.ReadInt64());
            var count = r.ReadInt32();
            if (count < 0)
            {
                throw new InvalidDataException($"ArenaSnapshot mechanism-group count {count} is negative.");
            }

            var groups = new List<MechanismGroupId>(count);
            for (var i = 0; i < count; i++)
            {
                groups.Add(new MechanismGroupId(r.ReadString()));
            }

            var exitUnlocked = r.ReadBool();
            var lastProcessed = new Sequence(r.ReadInt64());
            return new ArenaSnapshot(encounter, arenaId, arenaVersion, protocol, tick, groups, exitUnlocked, lastProcessed);
        }

        // ---------------------------------------------------------------- event composition

        private static void WriteBossEvent(WireWriter w, IBossWireEvent e)
        {
            switch (e)
            {
                case BossAppearedEvent x:
                    WriteBossHeader(w, BossAppearedTag, x.Sequence, x.Tick);
                    WriteInt(w, x.PhaseId);
                    break;
                case BossAttackTelegraphedEvent x:
                    WriteBossHeader(w, BossTelegraphedTag, x.Sequence, x.Tick);
                    w.WriteInt64(x.Attack.Value);
                    WriteInt(w, x.AttackDefinitionId);
                    WriteVector(w, x.AimPoint);
                    w.WriteSingle(x.TelegraphSeconds);
                    break;
                case BossAttackCommittedEvent x:
                    WriteBossHeader(w, BossCommittedTag, x.Sequence, x.Tick);
                    w.WriteInt64(x.Attack.Value);
                    WriteInt(w, x.AttackDefinitionId);
                    WriteVector(w, x.AimPoint);
                    break;
                case BossPhaseChangedEvent x:
                    WriteBossHeader(w, BossPhaseChangedTag, x.Sequence, x.Tick);
                    WriteInt(w, x.PhaseId);
                    break;
                case BossWeakPointChangedEvent x:
                    WriteBossHeader(w, BossWeakPointTag, x.Sequence, x.Tick);
                    w.WriteBool(x.Exposed);
                    break;
                case BossDamagedEvent x:
                    WriteBossHeader(w, BossDamagedTag, x.Sequence, x.Tick);
                    WriteInt(w, x.Amount);
                    WriteInt(w, x.RemainingHealth);
                    w.WriteBool(x.WeakPointHit);
                    break;
                case BossDefeatedEvent x:
                    WriteBossHeader(w, BossDefeatedTag, x.Sequence, x.Tick);
                    break;
                case null:
                    throw new ArgumentNullException(nameof(e));
                default:
                    throw new ArgumentOutOfRangeException(nameof(e), e.GetType().Name, "No wire encoding for this boss event.");
            }
        }

        private static IBossWireEvent ReadBossEvent(WireReader r)
        {
            var tag = r.ReadBytes(1)[0];
            var sequence = new Sequence(r.ReadInt64());
            var tick = new SimulationTick(r.ReadInt64());
            switch (tag)
            {
                case BossAppearedTag:
                    return new BossAppearedEvent(sequence, tick, r.ReadInt32());
                case BossTelegraphedTag:
                    return new BossAttackTelegraphedEvent(
                        sequence, tick, new AttackInstanceId(r.ReadInt64()), r.ReadInt32(), ReadVector(r), r.ReadSingle());
                case BossCommittedTag:
                    return new BossAttackCommittedEvent(
                        sequence, tick, new AttackInstanceId(r.ReadInt64()), r.ReadInt32(), ReadVector(r));
                case BossPhaseChangedTag:
                    return new BossPhaseChangedEvent(sequence, tick, r.ReadInt32());
                case BossWeakPointTag:
                    return new BossWeakPointChangedEvent(sequence, tick, r.ReadBool());
                case BossDamagedTag:
                    return new BossDamagedEvent(sequence, tick, r.ReadInt32(), r.ReadInt32(), r.ReadBool());
                case BossDefeatedTag:
                    return new BossDefeatedEvent(sequence, tick);
                default:
                    throw new InvalidDataException($"Unknown boss wire-event tag {tag}.");
            }
        }

        private static void WriteArenaEvent(WireWriter w, IArenaWireEvent e)
        {
            switch (e)
            {
                case ArenaMechanismGroupActivatedEvent x:
                    WriteArenaHeader(w, ArenaMechanismActivatedTag, x.Sequence, x.Tick);
                    w.WriteString(x.Group.Value);
                    break;
                case ArenaExitUnlockedEvent x:
                    WriteArenaHeader(w, ArenaExitUnlockedTag, x.Sequence, x.Tick);
                    break;
                case null:
                    throw new ArgumentNullException(nameof(e));
                default:
                    throw new ArgumentOutOfRangeException(nameof(e), e.GetType().Name, "No wire encoding for this arena event.");
            }
        }

        private static IArenaWireEvent ReadArenaEvent(WireReader r)
        {
            var tag = r.ReadBytes(1)[0];
            var sequence = new Sequence(r.ReadInt64());
            var tick = new SimulationTick(r.ReadInt64());
            switch (tag)
            {
                case ArenaMechanismActivatedTag:
                    return new ArenaMechanismGroupActivatedEvent(sequence, tick, new MechanismGroupId(r.ReadString()));
                case ArenaExitUnlockedTag:
                    return new ArenaExitUnlockedEvent(sequence, tick);
                default:
                    throw new InvalidDataException($"Unknown arena wire-event tag {tag}.");
            }
        }

        // ---------------------------------------------------------------- primitives & helpers

        private static void WriteBossHeader(WireWriter w, byte tag, Sequence sequence, SimulationTick tick)
        {
            w.WriteBytes(new[] { tag });
            w.WriteInt64(sequence.Value);
            w.WriteInt64(tick.Value);
        }

        private static void WriteArenaHeader(WireWriter w, byte tag, Sequence sequence, SimulationTick tick)
        {
            w.WriteBytes(new[] { tag });
            w.WriteInt64(sequence.Value);
            w.WriteInt64(tick.Value);
        }

        private static void WriteInt(WireWriter w, int value) => w.WriteInt32(value);

        private static void WriteVector(WireWriter w, SimVector2 v)
        {
            w.WriteSingle(v.X);
            w.WriteSingle(v.Z);
        }

        private static SimVector2 ReadVector(WireReader r) => new SimVector2(r.ReadSingle(), r.ReadSingle());

        private static void WriteNullableAttack(WireWriter w, AttackInstanceId? value)
        {
            w.WriteBool(value.HasValue);
            if (value.HasValue)
            {
                w.WriteInt64(value.Value.Value);
            }
        }

        private static AttackInstanceId? ReadNullableAttack(WireReader r) =>
            r.ReadBool() ? new AttackInstanceId(r.ReadInt64()) : (AttackInstanceId?)null;

        private static void WriteNullableParticipant(WireWriter w, ParticipantId? value)
        {
            w.WriteBool(value.HasValue);
            if (value.HasValue)
            {
                w.WriteInt32(value.Value.Value);
            }
        }

        private static ParticipantId? ReadNullableParticipant(WireReader r) =>
            r.ReadBool() ? new ParticipantId(r.ReadInt32()) : (ParticipantId?)null;

        private static void WriteNullableInt(WireWriter w, int? value)
        {
            w.WriteBool(value.HasValue);
            if (value.HasValue)
            {
                w.WriteInt32(value.Value);
            }
        }

        private static int? ReadNullableInt(WireReader r) => r.ReadBool() ? r.ReadInt32() : (int?)null;

        private static void WriteContentHash(WireWriter w, ContentHash hash)
        {
            var present = hash.Length == ContentHash.Sha256Length;
            w.WriteBool(present);
            if (present)
            {
                w.WriteBytes(hash.ToArray());
            }
        }

        private static ContentHash ReadContentHash(WireReader r) =>
            r.ReadBool() ? new ContentHash(r.ReadBytes(ContentHash.Sha256Length)) : default;

        private static void RequireEnd(WireReader r)
        {
            if (!r.AtEnd)
            {
                throw new InvalidDataException("Wire payload has trailing bytes after the decoded value.");
            }
        }
    }
}
