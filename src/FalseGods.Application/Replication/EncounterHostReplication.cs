using System;
using System.Collections.Generic;
using FalseGods.Core.Arena;
using FalseGods.Core.Bosses;
using FalseGods.Core.Bosses.Events;
using FalseGods.Core.Encounters;
using FalseGods.Core.Simulation;
using FalseGods.Protocol.Wire;
using FalseGods.Application.Wire;
using FalseGods.RuntimeContracts.Multiplayer;
using FalseGods.RuntimeContracts.Transport;

namespace FalseGods.Application.Replication
{
    /// <summary>
    /// The host's per-tick replication driver: stamps the tick's drained boss domain events with the boss-stream
    /// sequence, broadcasts them and a fresh <see cref="BossSnapshot"/> through the <see cref="ReplicationSender"/>,
    /// and sends one <see cref="EncounterBaseline"/> to every session peer that has not received one — the
    /// join-in-progress path (Docs/OriginalBossNetworkingArchitecture.md §9.6/§9.8).
    /// </summary>
    /// <remarks>
    /// This is what "the host <b>adds replication</b> to the single-player composition" means concretely
    /// (Architecture §4.3): the Composition Root drains the simulation's events once per tick, feeds them to the
    /// presenter <i>and</i> to this driver, and the boss implementation itself never changes. The driver owns the
    /// boss-stream <see cref="Sequence"/> counter (one authority for it) and the record of which peers already hold
    /// a baseline; it makes no gameplay decision.
    ///
    /// <para>
    /// Ordering per tick: baselines to newly-seen peers go first (carrying the stream floor <i>before</i> this
    /// tick's events), then the tick's events, then the snapshot — so a joining peer never has an event applied
    /// ahead of its baseline on the reliable-ordered channel. A peer that leaves the roster is forgotten, so a
    /// rejoin under the same id is re-baselined (§9.8 recovery). The local host peer never receives a baseline.
    /// </para>
    ///
    /// <para>
    /// <b>Arena placeholder.</b> Until the arena pipeline is productionised there is no live
    /// <c>ArenaSimulation</c>, so the baseline carries an empty <see cref="ArenaSnapshot"/> (no mechanism groups,
    /// exit locked, no arena events processed) and an unset <c>ContentHash</c>, and no arena events are ever
    /// broadcast. That is a documented stand-in: when the arena joins the composition, this driver gains the arena
    /// stream alongside the boss stream (Docs/MinimalProofOfConceptPlan.md §7.6.3 B6/B7 are validated then).
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
        private readonly string _arenaId;
        private readonly int _arenaVersion;
        private readonly HashSet<SessionPeerId> _baselinedPeers = new HashSet<SessionPeerId>();

        private Sequence _lastBossSequence = NoEvents;

        public EncounterHostReplication(
            ReplicationSender sender,
            IMultiplayerSession session,
            IPlayerRoster roster,
            EncounterId encounter,
            DefinitionId definition,
            string arenaId,
            int arenaVersion)
        {
            _sender = sender ?? throw new ArgumentNullException(nameof(sender));
            _session = session ?? throw new ArgumentNullException(nameof(session));
            _roster = roster ?? throw new ArgumentNullException(nameof(roster));
            _encounter = encounter;
            _definition = definition;
            _arenaId = arenaId ?? throw new ArgumentNullException(nameof(arenaId));
            _arenaVersion = arenaVersion;
        }

        /// <summary>The last boss-stream sequence stamped so far (<c>seq:-1</c> before the first event).</summary>
        public Sequence LastBossSequence => _lastBossSequence;

        /// <summary>
        /// Replicate one tick: baseline newly-seen peers, broadcast <paramref name="drainedEvents"/> (stamped in
        /// order) on the boss stream, then broadcast the boss snapshot. The caller drains the simulation exactly
        /// once per tick and hands the same list to the presenter and to this method.
        /// </summary>
        public void Publish(BossSimulation boss, IReadOnlyList<IBossDomainEvent> drainedEvents, SimulationTick tick)
        {
            if (boss is null)
            {
                throw new ArgumentNullException(nameof(boss));
            }

            if (drainedEvents is null)
            {
                throw new ArgumentNullException(nameof(drainedEvents));
            }

            BaselineNewPeers(boss, tick);

            for (var i = 0; i < drainedEvents.Count; i++)
            {
                _lastBossSequence = _lastBossSequence.Next();
                _sender.BroadcastBossEvent(BossWireMapping.ToWireEvent(drainedEvents[i], _lastBossSequence, tick));
            }

            _sender.BroadcastBossSnapshot(
                BossWireMapping.ToSnapshot(boss, _encounter, _definition, tick, _lastBossSequence));
        }

        private void BaselineNewPeers(BossSimulation boss, SimulationTick tick)
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

                _sender.SendBaseline(BuildBaseline(boss, tick), peer);
                _baselinedPeers.Add(peer);
            }
        }

        private EncounterBaseline BuildBaseline(BossSimulation boss, SimulationTick tick)
        {
            var arena = new ArenaSnapshot(
                _encounter,
                _arenaId,
                _arenaVersion,
                ProtocolVersion.Current,
                tick,
                Array.Empty<MechanismGroupId>(),
                ExitUnlocked: false,
                NoEvents);

            var phase = boss.IsDead ? EncounterPhase.Defeated : EncounterPhase.Fighting;

            return new EncounterBaseline(
                _encounter,
                ProtocolVersion.Current,
                _arenaId,
                _arenaVersion,
                ContentHash: default, // arena placeholder: no realized arena content yet (see class remarks)
                tick,
                (int)phase,
                BossWireMapping.ToSnapshot(boss, _encounter, _definition, tick, _lastBossSequence),
                arena,
                _lastBossSequence,
                NoEvents);
        }

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
