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
        private readonly List<SummonRequest> _summonRequests = new List<SummonRequest>();

        private readonly IReadOnlyList<BossAnchor> _anchors;

        private bool _spawned;
        private int _pendingStation = -1; // a station reached but not yet moved to; see AdvanceStations
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

        /// <summary>How much of its health the boss has left, in [0, 1]. How far through the fight it is, which is
        /// what the itinerary's thresholds and the supply line's escalation are both read against.</summary>
        public float HealthFraction => _definition.MaxHealth > 0
            ? Health / (float)_definition.MaxHealth
            : 0f;

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

        /// <summary>
        /// A station health has already reached but the boss has not moved to yet, or -1 when there is none. It
        /// leaves between actions, so this is set the moment the threshold is crossed and consumed when the boss
        /// next goes idle — the gap between the two is where "why has it not moved?" is answered.
        /// </summary>
        public int PendingStationIndex => _pendingStation;

        /// <summary>
        /// Whether the boss is in the middle of changing where it stands — leaving, gone, or arriving. It takes
        /// no damage for the whole of it, exactly as the vanilla cave boss is invulnerable from the start of its
        /// submerge until its reappearance finishes.
        /// </summary>
        public bool IsRelocating =>
            Activity == BossActivity.Vanishing
            || Activity == BossActivity.Hidden
            || Activity == BossActivity.Appearing;

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
            if (hasTarget && !IsRelocating)
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
                case BossActivity.Vanishing:
                    if (elapsed >= _definition.VanishSeconds)
                    {
                        EnterHidden(now);
                    }

                    return; // a relocation suspends the attack cycle entirely

                case BossActivity.Hidden:
                    if (elapsed >= _definition.HiddenSeconds)
                    {
                        EnterAppearing(now);
                    }

                    return;

                case BossActivity.Appearing:
                    if (elapsed >= _definition.AppearSeconds)
                    {
                        FinishRelocation(now);
                    }

                    return;

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
            if (!_spawned || IsDead || rawAmount <= 0 || IsRelocating)
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
        /// Take the itinerary's next step if health has reached it, and leave <b>now</b>.
        /// </summary>
        /// <remarks>
        /// <para><b>One station at a time.</b> A burst that crosses three thresholds at once does not skip to the
        /// last of them: the boss goes to the next one, and the step after that is taken when it arrives
        /// (<see cref="FinishRelocation"/> asks again). Otherwise a strong enough weapon deletes the whole
        /// itinerary and the fight never has the shape it was designed with — measured in game, where a fast
        /// weapon crossed every remaining threshold inside a single attack cycle.</para>
        /// <para><b>And immediately.</b> An earlier version waited for the boss to be idle, so that it would not
        /// vanish out of its own telegraph. The same measurement showed why that cannot work: under real damage
        /// the boss is almost never idle, so it simply never moved. It now interrupts whatever it was doing — an
        /// attack that never commits also never lands, which is the boss paying for its retreat.</para>
        /// </remarks>
        private void AdvanceStations()
        {
            var stations = _definition.Stations;
            if (StationIndex < 0 || IsRelocating || _pendingStation >= 0)
            {
                return;
            }

            var next = StationIndex + 1;
            if (next >= stations.Count)
            {
                return;
            }

            if (HealthFraction > stations[next].EnterAtHealthFraction)
            {
                return;
            }

            _pendingStation = next;
            BeginRelocation(_clock.Time);
        }

        /// <summary>Start leaving: invulnerable and still in place, the way the vanilla cave boss is already
        /// invulnerable as it begins to submerge.</summary>
        private void BeginRelocation(float now)
        {
            // Interrupting the weak-point window closes it, and that has to be said: the flag is derived from the
            // activity, so a client left holding the last "exposed" event would keep the weak point lit on a boss
            // that no longer has one.
            if (Activity == BossActivity.Recovering)
            {
                _events.Add(new WeakPointExposed(Id, false));
            }

            CurrentAttack = null;
            Facing = SimVector2.Zero;
            Activity = BossActivity.Vanishing;
            _activityEnteredTime = now;
        }

        /// <summary>
        /// Out of sight — and therefore the moment to move. Doing it here is the whole point of the sequence:
        /// nobody sees the boss cross the room, they see it leave one place and arrive at another.
        /// </summary>
        private void EnterHidden(float now)
        {
            Activity = BossActivity.Hidden;
            _activityEnteredTime = now;

            var target = _pendingStation;
            _pendingStation = -1;
            if (target < 0 || !TryEnterStation(target))
            {
                return; // no anchor authored for it: stay where we are, and simply come back up
            }

            StationIndex = target;
            _events.Add(new BossRelocated(
                Id, target, _definition.Stations[target].AnchorIndex, Position, PositionHeight));
        }

        /// <summary>
        /// Arriving: visible again at the new station, still invulnerable until the window ends — and whatever it
        /// came here to do happens now. Summoning on arrival rather than on departure is the point of the trip:
        /// the minions appear with the boss, in the room it just moved to.
        /// </summary>
        private void EnterAppearing(float now)
        {
            Activity = BossActivity.Appearing;
            _activityEnteredTime = now;

            var stations = _definition.Stations;
            if (StationIndex >= 0 && StationIndex < stations.Count && stations[StationIndex].SummonCount > 0)
            {
                _summonRequests.Add(new SummonRequest(StationIndex, stations[StationIndex].SummonCount));
            }
        }

        /// <summary>Back in the fight — and straight out again if health has already reached the station after
        /// this one, which is how a burst gets walked one station at a time instead of skipping them.</summary>
        private void FinishRelocation(float now)
        {
            Activity = BossActivity.Idle;
            _activityEnteredTime = now;
            AdvanceStations();
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

        /// <summary>
        /// Take the summon commands accumulated since the last drain, clearing the buffer. Kept separate from
        /// <see cref="DrainEvents"/> for the same reason <see cref="DrainDamageRequests"/> is — see
        /// <see cref="SummonRequest"/>. The caller owns the returned list.
        /// </summary>
        public IReadOnlyList<SummonRequest> DrainSummonRequests()
        {
            if (_summonRequests.Count == 0)
            {
                return Array.Empty<SummonRequest>();
            }

            var drained = _summonRequests.ToArray();
            _summonRequests.Clear();
            return drained;
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
