using System;
using System.Collections.Generic;
using FalseGods.Core.Arena;
using FalseGods.Core.Arena.Events;
using FalseGods.Core.Bosses;
using FalseGods.Core.Bosses.Events;
using FalseGods.Core.Encounters;
using FalseGods.Core.Simulation;
using FalseGods.Protocol.Arena;
using FalseGods.Protocol.Wire;
using FalseGods.Application.Wire;
using FalseGods.RuntimeContracts.Multiplayer;
using FalseGods.RuntimeContracts.Transport;

namespace FalseGods.Application.Replication
{
    /// <summary>
    /// The host's per-tick replication driver: stamps the tick's drained boss and arena domain events with their
    /// own per-stream sequences, broadcasts them and fresh snapshots through the <see cref="ReplicationSender"/>,
    /// and sends one <see cref="EncounterBaseline"/> to every session peer that has not received one — the
    /// join-in-progress path (Docs/OriginalBossNetworkingArchitecture.md §9.6/§9.8).
    /// </summary>
    /// <remarks>
    /// This is what "the host <b>adds replication</b> to the single-player composition" means concretely
    /// (Architecture §4.3): the Composition Root drains the simulations' events once per tick, feeds them to the
    /// presenter <i>and</i> to this driver, and the boss/arena implementations never change. The driver owns the
    /// boss-stream and arena-stream <see cref="Sequence"/> counters (one authority each, never shared — ADR-005)
    /// and the record of which peers already hold a baseline; it makes no gameplay decision.
    ///
    /// <para>
    /// Ordering per tick: baselines to newly-seen peers go first (carrying both streams' floors <i>before</i>
    /// this tick's events), then the tick's events, then the snapshots — so a joining peer never has an event
    /// applied ahead of its baseline on the reliable-ordered channel. A peer that leaves the roster is
    /// forgotten, so a rejoin under the same id is re-baselined (§9.8 recovery). The local host peer never
    /// receives a baseline.
    /// </para>
    ///
    /// <para>
    /// The <see cref="ArenaManifest"/> given at construction supplies the arena identity and the real
    /// <c>ContentHash</c> the baseline carries. The no-arena <see cref="Publish(BossSimulation, IReadOnlyList{IBossDomainEvent}, SimulationTick)"/>
    /// overload remains for the boss-only dev composition: it broadcasts no arena stream and baselines an empty
    /// arena snapshot — the composition switches to the full overload when the arena joins it.
    /// </para>
    /// </remarks>
    public sealed class EncounterHostReplication
    {
        /// <summary>The floor meaning "no event on this stream has been processed yet" (matches the receiver).</summary>
        private static readonly Sequence NoEvents = new Sequence(-1);

        private readonly ReplicationSender _sender;
        private readonly IMultiplayerSession _session;
        private readonly IPlayerRoster _roster;
        private readonly EncounterId _encounter;
        private readonly DefinitionId _definition;
        private readonly ArenaManifest _manifest;
        private readonly WorldPosition _arenaOrigin;
        private readonly HashSet<SessionPeerId> _baselinedPeers = new HashSet<SessionPeerId>();

        private Sequence _lastBossSequence = NoEvents;
        private Sequence _lastArenaSequence = NoEvents;

        public EncounterHostReplication(
            ReplicationSender sender,
            IMultiplayerSession session,
            IPlayerRoster roster,
            EncounterId encounter,
            DefinitionId definition,
            ArenaManifest manifest,
            WorldPosition arenaOrigin = default)
        {
            _sender = sender ?? throw new ArgumentNullException(nameof(sender));
            _session = session ?? throw new ArgumentNullException(nameof(session));
            _roster = roster ?? throw new ArgumentNullException(nameof(roster));
            _encounter = encounter;
            _definition = definition;
            _manifest = manifest ?? throw new ArgumentNullException(nameof(manifest));
            _arenaOrigin = arenaOrigin;
        }

        /// <summary>The last boss-stream sequence stamped so far (<c>seq:-1</c> before the first event).</summary>
        public Sequence LastBossSequence => _lastBossSequence;

        /// <summary>The last arena-stream sequence stamped so far (<c>seq:-1</c> before the first event).</summary>
        public Sequence LastArenaSequence => _lastArenaSequence;

        /// <summary>Boss-only replication for the composition without a live arena (the dev slice). The baseline
        /// carries an empty arena snapshot; no arena stream is broadcast.</summary>
        public void Publish(BossSimulation boss, IReadOnlyList<IBossDomainEvent> drainedEvents, SimulationTick tick) =>
            PublishCore(boss, drainedEvents, arena: null, Array.Empty<IArenaDomainEvent>(), phase: null, tick);

        /// <summary>
        /// Replicate one full tick: baseline newly-seen peers, broadcast the drained boss and arena events
        /// (stamped in order on their own streams), then broadcast both snapshots. The caller drains each
        /// simulation exactly once per tick and hands the same lists to presentation and to this method.
        /// </summary>
        public void Publish(
            BossSimulation boss,
            IReadOnlyList<IBossDomainEvent> drainedBossEvents,
            ArenaSimulation arena,
            IReadOnlyList<IArenaDomainEvent> drainedArenaEvents,
            EncounterPhase phase,
            SimulationTick tick)
        {
            if (arena is null)
            {
                throw new ArgumentNullException(nameof(arena));
            }

            PublishCore(boss, drainedBossEvents, arena, drainedArenaEvents, phase, tick);
        }

        private void PublishCore(
            BossSimulation boss,
            IReadOnlyList<IBossDomainEvent> drainedBossEvents,
            ArenaSimulation? arena,
            IReadOnlyList<IArenaDomainEvent> drainedArenaEvents,
            EncounterPhase? phase,
            SimulationTick tick)
        {
            if (boss is null)
            {
                throw new ArgumentNullException(nameof(boss));
            }

            if (drainedBossEvents is null)
            {
                throw new ArgumentNullException(nameof(drainedBossEvents));
            }

            if (drainedArenaEvents is null)
            {
                throw new ArgumentNullException(nameof(drainedArenaEvents));
            }

            BaselineNewPeers(boss, arena, phase, tick);

            for (var i = 0; i < drainedBossEvents.Count; i++)
            {
                _lastBossSequence = _lastBossSequence.Next();
                _sender.BroadcastBossEvent(BossWireMapping.ToWireEvent(drainedBossEvents[i], _lastBossSequence, tick));
            }

            for (var i = 0; i < drainedArenaEvents.Count; i++)
            {
                _lastArenaSequence = _lastArenaSequence.Next();
                _sender.BroadcastArenaEvent(ArenaWireMapping.ToWireEvent(drainedArenaEvents[i], _lastArenaSequence, tick));
            }

            _sender.BroadcastBossSnapshot(
                BossWireMapping.ToSnapshot(boss, _encounter, _definition, tick, _lastBossSequence));

            if (arena != null)
            {
                _sender.BroadcastArenaSnapshot(BuildArenaSnapshot(arena, tick));
            }
        }

        private void BaselineNewPeers(BossSimulation boss, ArenaSimulation? arena, EncounterPhase? phase, SimulationTick tick)
        {
            var members = _roster.Members;

            // Forget peers that left, so a rejoin under the same id gets a fresh baseline.
            _baselinedPeers.RemoveWhere(peer => !Contains(members, peer));

            for (var i = 0; i < members.Count; i++)
            {
                var peer = members[i];
                if (peer == _session.LocalPeer || _baselinedPeers.Contains(peer))
                {
                    continue;
                }

                _sender.SendBaseline(BuildBaseline(boss, arena, phase, tick), peer);
                _baselinedPeers.Add(peer);
            }
        }

        private EncounterBaseline BuildBaseline(BossSimulation boss, ArenaSimulation? arena, EncounterPhase? phase, SimulationTick tick)
        {
            var arenaSnapshot = arena != null
                ? BuildArenaSnapshot(arena, tick)
                : new ArenaSnapshot(
                    _encounter,
                    _manifest.ArenaId,
                    _manifest.ArenaVersion,
                    ProtocolVersion.Current,
                    tick,
                    Array.Empty<MechanismGroupId>(),
                    ExitUnlocked: false,
                    NoEvents);

            var effectivePhase = phase ?? (boss.IsDead ? EncounterPhase.Defeated : EncounterPhase.Fighting);

            return new EncounterBaseline(
                _encounter,
                ProtocolVersion.Current,
                _manifest.ArenaId,
                _manifest.ArenaVersion,
                _manifest.ContentHash,
                _arenaOrigin,
                tick,
                (int)effectivePhase,
                BossWireMapping.ToSnapshot(boss, _encounter, _definition, tick, _lastBossSequence),
                arenaSnapshot,
                _lastBossSequence,
                _lastArenaSequence);
        }

        private ArenaSnapshot BuildArenaSnapshot(ArenaSimulation arena, SimulationTick tick) =>
            ArenaWireMapping.ToSnapshot(arena, _encounter, _manifest.ArenaId, _manifest.ArenaVersion, tick, _lastArenaSequence);

        private static bool Contains(IReadOnlyList<SessionPeerId> members, SessionPeerId peer)
        {
            for (var i = 0; i < members.Count; i++)
            {
                if (members[i] == peer)
                {
                    return true;
                }
            }

            return false;
        }
    }
}
