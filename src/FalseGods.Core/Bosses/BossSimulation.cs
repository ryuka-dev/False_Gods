using System;
using System.Collections.Generic;
using FalseGods.Core.Bosses.Combat;
using FalseGods.Core.Bosses.Events;
using FalseGods.Core.Simulation;

namespace FalseGods.Core.Bosses
{
    /// <summary>
    /// The authoritative domain logic for the PoC test boss (Docs/MinimalProofOfConceptPlan.md §7.6.1).
    /// </summary>
    /// <remarks>
    /// This is the "one temporary <c>BossSimulation</c>" of the vertical slice (Docs/DefinitionOfDone.md §3). It is
    /// pure boss domain — a state machine over health, phase, activity, and a simple attack cycle — and it owns all
    /// of that state (Docs/Architecture.md §5, §9). It runs in single-player and on the host only; a multiplayer
    /// client never constructs one, it presents replicated results (Docs/ADRs/ADR-003).
    ///
    /// <para>
    /// It touches nothing outer. Time, randomness, and the participant roster arrive through the three Core ports
    /// (<see cref="ISimulationClock"/>, <see cref="IAuthoritativeRandom"/>, <see cref="IEncounterParticipantQuery"/>,
    /// Docs/Architecture.md §6). It never locates a Unity object, never calls an arena mechanism, and never inspects
    /// a transport — so it is fully unit-testable without Unity or a socket, which is the point of the boundary.
    /// </para>
    ///
    /// <para>
    /// It emits <see cref="IBossDomainEvent"/>s for every discrete authoritative decision; the caller
    /// (<c>EncounterCoordinator</c> today, <c>Application</c> replication/presentation mapping later) reads them with
    /// <see cref="DrainEvents"/>. Continuous state (<see cref="Position"/>, <see cref="Health"/>,
    /// <see cref="Activity"/>) is read directly.
    /// </para>
    /// </remarks>
    public sealed class BossSimulation
    {
        private readonly BossDefinition _definition;
        private readonly ISimulationClock _clock;
        private readonly IAuthoritativeRandom _random;
        private readonly IEncounterParticipantQuery _participants;
        private readonly List<IBossDomainEvent> _events = new List<IBossDomainEvent>();
        private readonly List<DamageRequest> _damageRequests = new List<DamageRequest>();

        private readonly IReadOnlyList<BossAnchor> _anchors;

        private bool _spawned;
        private float _activityEnteredTime;
        private float _lastAdvanceTime;
        private AttackInstanceId _lastAttackId;

        public BossSimulation(
            BossInstanceId id,
            BossDefinition definition,
            ISimulationClock clock,
            IAuthoritativeRandom random,
            IEncounterParticipantQuery participants,
            IReadOnlyList<BossAnchor>? anchors = null)
        {
            Id = id;
            _definition = definition ?? throw new ArgumentNullException(nameof(definition));
            _clock = clock ?? throw new ArgumentNullException(nameof(clock));
            _random = random ?? throw new ArgumentNullException(nameof(random));
            _participants = participants ?? throw new ArgumentNullException(nameof(participants));
            _anchors = anchors ?? Array.Empty<BossAnchor>();
            _lastAttackId = new AttackInstanceId(0);
            Activity = BossActivity.Idle;
            Phase = BossPhase.One;
            StationIndex = -1;
        }

        /// <summary>This boss's stable identity, carried on every event it emits.</summary>
        public BossInstanceId Id { get; }

        /// <summary>Whether <see cref="Spawn"/> has been called and the boss is live in the encounter.</summary>
        public bool IsSpawned => _spawned;

        /// <summary>Current health. Zero once dead; never negative.</summary>
        public int Health { get; private set; }

        /// <summary>The boss's full health, from its definition. Constant for the encounter.</summary>
        public int MaxHealth => _definition.MaxHealth;

        /// <summary>Current phase (Docs/MinimalProofOfConceptPlan.md §7.6.1 — two phases).</summary>
        public BossPhase Phase { get; private set; }

        /// <summary>What the boss is doing right now.</summary>
        public BossActivity Activity { get; private set; }

        /// <summary><c>true</c> once <see cref="Health"/> reaches zero.</summary>
        public bool IsDead => Activity == BossActivity.Dead;

        /// <summary>
        /// <c>true</c> during post-attack recovery, when a hit is amplified by
        /// <see cref="BossDefinition.WeakPointDamageMultiplier"/>.
        /// </summary>
        public bool IsWeakPointExposed => Activity == BossActivity.Recovering;

        /// <summary>The boss's current position on the arena's ground plane.</summary>
        public SimVector2 Position { get; private set; }

        /// <summary>
        /// The height the boss currently stands at — the height of the anchor it occupies. Not a simulated
        /// quantity: the boss never moves vertically, it is <i>placed</i> somewhere that has a height.
        /// </summary>
        public float PositionHeight { get; private set; }

        /// <summary>Which step of its itinerary the boss is at, or -1 when it has none (or has not spawned).</summary>
        public int StationIndex { get; private set; }

        /// <summary>The unit direction the boss faces — toward its current target, or <see cref="SimVector2.Zero"/>.</summary>
        public SimVector2 Facing { get; private set; }

        /// <summary>The attack currently in flight (telegraphing or committing), or <c>null</c> when idle/recovering/dead.</summary>
        public AttackInstanceId? CurrentAttack { get; private set; }

        /// <summary>The kind of the attack currently in flight, valid only while <see cref="CurrentAttack"/> is set.</summary>
        public BossAttackKind CurrentAttackKind { get; private set; }

        /// <summary>Where the attack currently in flight is aimed, valid only while <see cref="CurrentAttack"/> is set.</summary>
        public SimVector2 CurrentAttackAimPoint { get; private set; }

        /// <summary>
        /// Bring the boss into the encounter at full health in phase one. A boss with an itinerary starts at its
        /// first station and ignores <paramref name="startPosition"/>; one without stands where it is told.
        /// Idempotent by contract: calling it again after the first spawn does nothing.
        /// </summary>
        /// <param name="startPosition">Where a boss with no itinerary stands.</param>
        /// <param name="startHeight">The height a boss with no itinerary stands at.</param>
        public void Spawn(SimVector2 startPosition, float startHeight = 0f)
        {
            if (_spawned)
            {
                return;
            }

            _spawned = true;
            Health = _definition.MaxHealth;
            Phase = BossPhase.One;
            Activity = BossActivity.Idle;
            Position = startPosition;
            PositionHeight = startHeight;
            Facing = SimVector2.Zero;
            StationIndex = -1;

            // The first station is the boss's starting place, so entering it is part of spawning rather than a
            // relocation: the spawn event already says "the boss is here".
            if (TryEnterStation(0))
            {
                StationIndex = 0;
            }

            _activityEnteredTime = _clock.Time;
            _lastAdvanceTime = _clock.Time;
            _events.Add(new BossSpawned(Id, Phase, Health));
        }

        /// <summary>
        /// Advance the boss one host tick: move toward the target while idle, and run the attack cycle
        /// (idle → telegraph → commit → recover → idle) off <see cref="ISimulationClock.Time"/>. A no-op before
        /// <see cref="Spawn"/> and after death.
        /// </summary>
        public void Advance()
        {
            if (!_spawned || IsDead)
            {
                return;
            }

            var now = _clock.Time;
            var frameSeconds = Math.Max(0f, now - _lastAdvanceTime);
            _lastAdvanceTime = now;

            var hasTarget = TryGetNearestTarget(out _, out var targetPosition);
            if (hasTarget)
            {
                Facing = Position.DirectionTo(targetPosition);
            }

            // A boss with an itinerary never walks — its stations decide where it stands, and the definition
            // refuses to be both at once.
            if (Activity == BossActivity.Idle && hasTarget && _definition.MoveSpeed > 0f)
            {
                Position = Position.MoveToward(targetPosition, _definition.MoveSpeed * frameSeconds);
            }

            var elapsed = Math.Max(0f, now - _activityEnteredTime);
            switch (Activity)
            {
                case BossActivity.Idle:
                    // Idle time only accrues while there is someone to attack; an empty arena pauses the cycle
                    // rather than firing an attack at nothing (Docs/DependencyRules.md §3 — observe the roster).
                    if (!hasTarget)
                    {
                        _activityEnteredTime = now;
                    }
                    else if (elapsed >= _definition.IdleSeconds)
                    {
                        SelectAttack(now, targetPosition);
                    }

                    break;

                case BossActivity.Telegraphing:
                    if (elapsed >= _definition.TelegraphSeconds)
                    {
                        CommitAttack(now);
                    }

                    break;

                case BossActivity.Committing:
                    if (elapsed >= _definition.CommitSeconds)
                    {
                        BeginRecovery(now);
                    }

                    break;

                case BossActivity.Recovering:
                    if (elapsed >= _definition.RecoverSeconds)
                    {
                        EndRecovery(now);
                    }

                    break;
            }
        }

        /// <summary>
        /// Apply <paramref name="rawAmount"/> points of incoming damage — the one authoritative combat decision the
        /// host makes. The boss amplifies a hit that lands on the exposed weak point, reduces health, and may cross
        /// into phase two or die as a result. A non-positive amount, or a hit on an unspawned or dead boss, is
        /// ignored. Clients never call this; they receive its <see cref="BossDamaged"/> result.
        /// </summary>
        public void ApplyDamage(int rawAmount)
        {
            if (!_spawned || IsDead || rawAmount <= 0)
            {
                return;
            }

            var weakPointHit = IsWeakPointExposed;
            var amount = weakPointHit ? rawAmount * _definition.WeakPointDamageMultiplier : rawAmount;
            Health = Math.Max(0, Health - amount);
            _events.Add(new BossDamaged(Id, amount, Health, weakPointHit));

            if (Health == 0)
            {
                Activity = BossActivity.Dead;
                CurrentAttack = null;
                Facing = SimVector2.Zero;
                _events.Add(new BossDied(Id));
                return;
            }

            AdvanceStations();

            if (Phase == BossPhase.One && Health <= _definition.PhaseTwoHealthThreshold)
            {
                Phase = BossPhase.Two;
                _events.Add(new BossPhaseChanged(Id, Phase));
            }
        }

        /// <summary>
        /// Take the events accumulated since the last drain, clearing the internal buffer. The caller owns the
        /// returned events; the simulation keeps no reference to them.
        /// </summary>
        public IReadOnlyList<IBossDomainEvent> DrainEvents()
        {
            if (_events.Count == 0)
            {
                return Array.Empty<IBossDomainEvent>();
            }

            var drained = _events.ToArray();
            _events.Clear();
            return drained;
        }

        /// <summary>
        /// Take the outbound damage requests accumulated since the last drain, clearing the buffer. Kept separate
        /// from <see cref="DrainEvents"/> because these are commands to damage players, not boss-state facts to
        /// render or replicate (see <see cref="DamageRequest"/>). The caller owns the returned list.
        /// </summary>
        public IReadOnlyList<DamageRequest> DrainDamageRequests()
        {
            if (_damageRequests.Count == 0)
            {
                return Array.Empty<DamageRequest>();
            }

            var drained = _damageRequests.ToArray();
            _damageRequests.Clear();
            return drained;
        }

        /// <summary>
        /// Walk the itinerary forward to the last station this health level has reached, and put the boss there.
        /// One hit big enough to cross several stations lands at the last of them and reports once — a boss does
        /// not visit places the players never saw it in.
        /// </summary>
        private void AdvanceStations()
        {
            var stations = _definition.Stations;
            if (StationIndex < 0 || stations.Count == 0)
            {
                return;
            }

            var healthFraction = Health / (float)_definition.MaxHealth;
            var next = StationIndex;
            while (next + 1 < stations.Count && healthFraction <= stations[next + 1].EnterAtHealthFraction)
            {
                next++;
            }

            if (next == StationIndex || !TryEnterStation(next))
            {
                return;
            }

            StationIndex = next;
            _events.Add(new BossRelocated(
                Id, next, stations[next].AnchorIndex, Position, PositionHeight));
        }

        /// <summary>
        /// Stand the boss at the station's anchor. Fails — leaving the boss where it is — when the boss has no
        /// itinerary or the room authored no anchor for that station: a fight in a room that does not have the
        /// places this boss expects is worth continuing where it stands, not worth teleporting into nowhere.
        /// </summary>
        private bool TryEnterStation(int stationIndex)
        {
            var stations = _definition.Stations;
            if (stationIndex < 0 || stationIndex >= stations.Count)
            {
                return false;
            }

            var anchorIndex = stations[stationIndex].AnchorIndex;
            if (anchorIndex < 0 || anchorIndex >= _anchors.Count)
            {
                return false;
            }

            var anchor = _anchors[anchorIndex];
            Position = anchor.Ground;
            PositionHeight = anchor.Height;
            return true;
        }

        private void SelectAttack(float now, SimVector2 targetPosition)
        {
            var kind = _random.NextInt(0, 2) == 0 ? BossAttackKind.AimedProjectile : BossAttackKind.AreaTelegraph;
            _lastAttackId = _lastAttackId.Next();

            CurrentAttack = _lastAttackId;
            CurrentAttackKind = kind;
            CurrentAttackAimPoint = targetPosition;
            Activity = BossActivity.Telegraphing;
            _activityEnteredTime = now;
            _events.Add(new AttackTelegraphed(Id, _lastAttackId, kind, targetPosition, _definition.TelegraphSeconds));
        }

        private void CommitAttack(float now)
        {
            Activity = BossActivity.Committing;
            _activityEnteredTime = now;
            _events.Add(new AttackCommitted(Id, _lastAttackId, CurrentAttackKind, CurrentAttackAimPoint));
            ResolveAttackHits();
        }

        /// <summary>
        /// The landing decision: at commit, everyone currently within the attack's hit radius of its (telegraph-time)
        /// aim point takes damage — so a participant who left the danger zone during the telegraph is missed. Aimed
        /// attacks use a tight radius (a near-direct hit on where the target was); area attacks use a wide one. Each
        /// caught participant produces one <see cref="DamageRequest"/>; the outer layers apply it to the real player.
        /// </summary>
        private void ResolveAttackHits()
        {
            var radius = CurrentAttackKind == BossAttackKind.AimedProjectile
                ? _definition.AimedHitRadius
                : _definition.AreaHitRadius;

            var roster = _participants.Participants;
            for (var i = 0; i < roster.Count; i++)
            {
                var participant = roster[i];
                if (!_participants.TryGetPosition(participant, out var position))
                {
                    continue;
                }

                if (CurrentAttackAimPoint.DistanceTo(position) <= radius)
                {
                    _damageRequests.Add(new DamageRequest(participant, _definition.AttackDamage, _lastAttackId));
                }
            }
        }

        private void BeginRecovery(float now)
        {
            CurrentAttack = null;
            Activity = BossActivity.Recovering;
            _activityEnteredTime = now;
            _events.Add(new WeakPointExposed(Id, true));
        }

        private void EndRecovery(float now)
        {
            Activity = BossActivity.Idle;
            _activityEnteredTime = now;
            _events.Add(new WeakPointExposed(Id, false));
        }

        private bool TryGetNearestTarget(out ParticipantId nearest, out SimVector2 position)
        {
            nearest = default;
            position = default;
            var found = false;
            var bestDistance = float.MaxValue;

            var roster = _participants.Participants;
            for (var i = 0; i < roster.Count; i++)
            {
                var participant = roster[i];
                if (!_participants.TryGetPosition(participant, out var candidate))
                {
                    continue;
                }

                var distance = Position.DistanceTo(candidate);
                if (!found || distance < bestDistance)
                {
                    found = true;
                    bestDistance = distance;
                    nearest = participant;
                    position = candidate;
                }
            }

            return found;
        }
    }
}
