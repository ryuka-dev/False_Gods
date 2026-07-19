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
    ///
    /// <para>
    /// <b>Untrusted input (Docs/DependencyRules.md §12).</b> On the channel path, a payload is dropped — counted,
    /// never thrown — when its sender is not the session's host, or it does not decode, or a version-carrying DTO
    /// (snapshot/baseline) reports a <see cref="ProtocolVersion"/> other than <see cref="ProtocolVersion.Current"/>.
    /// Encounter <i>control</i> messages (EnterArena, ArenaReady, …) ride the same channel but are not replication:
    /// this receiver ignores them; the load-flow choreography owns them.
    /// </para>
    /// </remarks>
    public sealed class ReplicationReceiver : IDisposable
    {
        private readonly IEncounterChannel _channel;
        private readonly IMultiplayerSession _session;
        private readonly HashSet<long> _appliedBossSequences = new HashSet<long>();
        private readonly HashSet<long> _appliedArenaSequences = new HashSet<long>();
        private readonly HashSet<long> _committedAttacks = new HashSet<long>();
        private readonly List<IBossWireEvent> _appliedBossEvents = new List<IBossWireEvent>();
        private readonly List<IArenaWireEvent> _appliedArenaEvents = new List<IArenaWireEvent>();

        private long _bossFloor = -1;
        private long _arenaFloor = -1;
        private long _lastBossSnapshotTick = long.MinValue;
        private long _lastArenaSnapshotTick = long.MinValue;

        public ReplicationReceiver(IEncounterChannel channel, IMultiplayerSession session)
        {
            _channel = channel ?? throw new ArgumentNullException(nameof(channel));
            _session = session ?? throw new ArgumentNullException(nameof(session));
            _channel.Received += OnReceived;
        }

        /// <summary>Channel payloads dropped because the sender was not the session host.</summary>
        public int DroppedFromNonHost { get; private set; }

        /// <summary>Channel payloads dropped because they did not decode.</summary>
        public int DroppedMalformed { get; private set; }

        /// <summary>Payloads dropped because a snapshot/baseline carried a foreign protocol version.</summary>
        public int DroppedVersionMismatch { get; private set; }

        /// <summary>
        /// Unsubscribe from the channel. Part of encounter teardown (Docs/Architecture.md §9): after this, no
        /// arriving payload is applied. Idempotent; the applied state stays readable for the teardown code.
        /// </summary>
        public void Dispose() => _channel.Received -= OnReceived;

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

        /// <summary>Decode and apply one payload from a trusted source (a test, or composition-internal
        /// delivery). A malformed payload throws here; the live channel path catches instead.</summary>
        public void Apply(EncodedPayload payload) => ApplyDecoded(EncounterCodec.Decode(payload));

        private void OnReceived(SessionPeerId sender, EncodedPayload payload)
        {
            // Possession of the channel is not authority: only the session host replicates encounter state.
            if (sender != _session.HostPeer)
            {
                DroppedFromNonHost++;
                return;
            }

            DecodedMessage message;
            try
            {
                message = EncounterCodec.Decode(payload);
            }
            catch (Exception)
            {
                DroppedMalformed++;
                return;
            }

            ApplyDecoded(message);
        }

        private void ApplyDecoded(DecodedMessage message)
        {
            switch (message.Value)
            {
                case BossSnapshot snapshot:
                    if (RejectVersion(snapshot.ProtocolVersion))
                    {
                        return;
                    }

                    ApplyBossSnapshot(snapshot);
                    break;
                case ArenaSnapshot snapshot:
                    if (RejectVersion(snapshot.ProtocolVersion))
                    {
                        return;
                    }

                    ApplyArenaSnapshot(snapshot);
                    break;
                case IBossWireEvent bossEvent:
                    ApplyBossEvent(bossEvent);
                    break;
                case IArenaWireEvent arenaEvent:
                    ApplyArenaEvent(arenaEvent);
                    break;
                case EncounterBaseline baseline:
                    if (RejectVersion(baseline.ProtocolVersion))
                    {
                        return;
                    }

                    ApplyBaseline(baseline);
                    break;
                case EnterArena _:
                case ArenaReady _:
                case ArenaLoadFailed _:
                case EncounterAborted _:
                case EncounterEnded _:
                    break; // encounter control traffic — owned by the load-flow choreography, not replication
                default:
                    throw new InvalidOperationException($"Unhandled replication value {message.Value?.GetType().Name ?? "null"}.");
            }
        }

        private bool RejectVersion(ProtocolVersion version)
        {
            if (version != ProtocolVersion.Current)
            {
                DroppedVersionMismatch++;
                return true;
            }

            return false;
        }

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
