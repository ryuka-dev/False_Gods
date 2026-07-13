using System;
using System.Collections.Generic;
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

        private bool _spawned;
        private float _activityEnteredTime;
        private float _lastAdvanceTime;
        private AttackInstanceId _lastAttackId;

        public BossSimulation(
            BossInstanceId id,
            BossDefinition definition,
            ISimulationClock clock,
            IAuthoritativeRandom random,
            IEncounterParticipantQuery participants)
        {
            Id = id;
            _definition = definition ?? throw new ArgumentNullException(nameof(definition));
            _clock = clock ?? throw new ArgumentNullException(nameof(clock));
            _random = random ?? throw new ArgumentNullException(nameof(random));
            _participants = participants ?? throw new ArgumentNullException(nameof(participants));
            _lastAttackId = new AttackInstanceId(0);
            Activity = BossActivity.Idle;
            Phase = BossPhase.One;
        }

        /// <summary>This boss's stable identity, carried on every event it emits.</summary>
        public BossInstanceId Id { get; }

        /// <summary>Whether <see cref="Spawn"/> has been called and the boss is live in the encounter.</summary>
        public bool IsSpawned => _spawned;

        /// <summary>Current health. Zero once dead; never negative.</summary>
        public int Health { get; private set; }

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

        /// <summary>The boss's current position on the arena floor.</summary>
        public SimVector2 Position { get; private set; }

        /// <summary>The unit direction the boss faces — toward its current target, or <see cref="SimVector2.Zero"/>.</summary>
        public SimVector2 Facing { get; private set; }

        /// <summary>The attack currently in flight (telegraphing or committing), or <c>null</c> when idle/recovering/dead.</summary>
        public AttackInstanceId? CurrentAttack { get; private set; }

        /// <summary>The kind of the attack currently in flight, valid only while <see cref="CurrentAttack"/> is set.</summary>
        public BossAttackKind CurrentAttackKind { get; private set; }

        /// <summary>Where the attack currently in flight is aimed, valid only while <see cref="CurrentAttack"/> is set.</summary>
        public SimVector2 CurrentAttackAimPoint { get; private set; }

        /// <summary>
        /// Bring the boss into the encounter at <paramref name="startPosition"/>, at full health in phase one.
        /// Idempotent by contract: calling it again after the first spawn does nothing.
        /// </summary>
        public void Spawn(SimVector2 startPosition)
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
            Facing = SimVector2.Zero;
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
