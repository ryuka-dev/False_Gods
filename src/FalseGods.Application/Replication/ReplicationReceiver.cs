using System;
using System.Collections.Generic;
using FalseGods.Protocol.Wire;
using FalseGods.RuntimeContracts.Multiplayer;
using FalseGods.RuntimeContracts.Transport;

namespace FalseGods.Application.Replication
{
    /// <summary>
    /// The client side of replication: decodes messages from the opaque channel and applies them <b>idempotently</b>
    /// (Docs/OriginalBossNetworkingArchitecture.md §9.8–§9.9, ADR-005, PoC B5/B6/B8).
    /// </summary>
    /// <remarks>
    /// The three robustness properties this exists to guarantee:
    /// <list type="bullet">
    /// <item>Reliable events are idempotent by (encounter, stream, <see cref="Sequence"/>): a retransmitted event
    /// is dropped, and boss attack <em>effects</em> are additionally idempotent by <c>AttackInstanceId</c>, so a
    /// duplicate never lands a second projectile.</item>
    /// <item>The boss and arena streams are independent — a dropped arena event never stalls the boss stream,
    /// because each is applied on receipt against its own sequence set (no gap-waiting).</item>
    /// <item>A late join applies exactly one <see cref="EncounterBaseline"/>: it sets each stream's floor to the
    /// sequence the baseline already reflects, so events at or below it are dropped and processing resumes cleanly
    /// (B8).</item>
    /// </list>
    /// Continuous snapshots are separate from discrete events (invariant 7): losing a snapshot never cancels or
    /// repeats an attack. This class tracks applied state for the composition to project to presentation; it makes
    /// no authoritative decision.
    /// </remarks>
    public sealed class ReplicationReceiver
    {
        private readonly HashSet<long> _appliedBossSequences = new HashSet<long>();
        private readonly HashSet<long> _appliedArenaSequences = new HashSet<long>();
        private readonly HashSet<long> _committedAttacks = new HashSet<long>();
        private readonly List<IBossWireEvent> _appliedBossEvents = new List<IBossWireEvent>();
        private readonly List<IArenaWireEvent> _appliedArenaEvents = new List<IArenaWireEvent>();

        private long _bossFloor = -1;
        private long _arenaFloor = -1;
        private long _lastBossSnapshotTick = long.MinValue;
        private long _lastArenaSnapshotTick = long.MinValue;

        public ReplicationReceiver(IEncounterChannel channel)
        {
            if (channel is null)
            {
                throw new ArgumentNullException(nameof(channel));
            }

            channel.Received += OnReceived;
        }

        /// <summary>Boss discrete events applied, in application order, with duplicates already dropped.</summary>
        public IReadOnlyList<IBossWireEvent> AppliedBossEvents => _appliedBossEvents;

        /// <summary>Arena discrete events applied, with duplicates already dropped.</summary>
        public IReadOnlyList<IArenaWireEvent> AppliedArenaEvents => _appliedArenaEvents;

        /// <summary>Distinct boss attack landings applied — deduplicated by <c>AttackInstanceId</c>.</summary>
        public int CommittedAttackCount => _committedAttacks.Count;

        /// <summary>The most recent boss snapshot applied (latest by tick), or null.</summary>
        public BossSnapshot? LatestBossSnapshot { get; private set; }

        /// <summary>The most recent arena snapshot applied (latest by tick), or null.</summary>
        public ArenaSnapshot? LatestArenaSnapshot { get; private set; }

        /// <summary>Whether the one-time baseline has been applied.</summary>
        public bool HasBaseline { get; private set; }

        /// <summary>Decode and apply one payload. Exposed so tests can feed messages without a live channel.</summary>
        public void Apply(EncodedPayload payload)
        {
            var message = EncounterCodec.Decode(payload);
            switch (message.Value)
            {
                case BossSnapshot snapshot:
                    ApplyBossSnapshot(snapshot);
                    break;
                case ArenaSnapshot snapshot:
                    ApplyArenaSnapshot(snapshot);
                    break;
                case IBossWireEvent bossEvent:
                    ApplyBossEvent(bossEvent);
                    break;
                case IArenaWireEvent arenaEvent:
                    ApplyArenaEvent(arenaEvent);
                    break;
                case EncounterBaseline baseline:
                    ApplyBaseline(baseline);
                    break;
                default:
                    throw new InvalidOperationException($"Unhandled replication value {message.Value?.GetType().Name ?? "null"}.");
            }
        }

        private void OnReceived(SessionPeerId sender, EncodedPayload payload) => Apply(payload);

        private void ApplyBossSnapshot(BossSnapshot snapshot)
        {
            if (snapshot.Tick.Value >= _lastBossSnapshotTick)
            {
                LatestBossSnapshot = snapshot;
                _lastBossSnapshotTick = snapshot.Tick.Value;
            }
        }

        private void ApplyArenaSnapshot(ArenaSnapshot snapshot)
        {
            if (snapshot.Tick.Value >= _lastArenaSnapshotTick)
            {
                LatestArenaSnapshot = snapshot;
                _lastArenaSnapshotTick = snapshot.Tick.Value;
            }
        }

        private void ApplyBossEvent(IBossWireEvent bossEvent)
        {
            var sequence = bossEvent.Sequence.Value;
            if (sequence <= _bossFloor || !_appliedBossSequences.Add(sequence))
            {
                return; // already reflected in the baseline, or a duplicate
            }

            // Attack landings are additionally idempotent by AttackInstanceId (§9.9): even a distinct event that
            // re-commits the same attack lands its effect only once.
            if (bossEvent is BossAttackCommittedEvent committed)
            {
                _committedAttacks.Add(committed.Attack.Value);
            }

            _appliedBossEvents.Add(bossEvent);
        }

        private void ApplyArenaEvent(IArenaWireEvent arenaEvent)
        {
            var sequence = arenaEvent.Sequence.Value;
            if (sequence <= _arenaFloor || !_appliedArenaSequences.Add(sequence))
            {
                return;
            }

            _appliedArenaEvents.Add(arenaEvent);
        }

        private void ApplyBaseline(EncounterBaseline baseline)
        {
            if (HasBaseline)
            {
                return; // exactly one baseline per join
            }

            HasBaseline = true;
            _bossFloor = baseline.LastProcessedBossEventSequence.Value;
            _arenaFloor = baseline.LastProcessedArenaEventSequence.Value;
            LatestBossSnapshot = baseline.Boss;
            LatestArenaSnapshot = baseline.Arena;
            _lastBossSnapshotTick = baseline.Boss.Tick.Value;
            _lastArenaSnapshotTick = baseline.Arena.Tick.Value;
        }
    }
}
