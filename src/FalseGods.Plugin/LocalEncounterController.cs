using System;
using System.Collections.Generic;
using System.IO;
using FalseGods.Application.Arena;
using FalseGods.Application.Combat;
using FalseGods.Application.Presentation;
using FalseGods.Application.ReadyGate;
using FalseGods.Application.Replication;
using FalseGods.Core.Arena;
using FalseGods.Core.Bosses;
using FalseGods.Core.Encounters;
using FalseGods.Core.Simulation;
using FalseGods.Integration.Sulfur.Combat;
using FalseGods.Integration.Sulfur.Navigation;
using FalseGods.Integration.Sulfur.Simulation;
using FalseGods.Protocol.Arena;
using FalseGods.Protocol.Wire;
using FalseGods.RuntimeContracts.Arena;
using FalseGods.RuntimeContracts.Integration;
using FalseGods.RuntimeContracts.Multiplayer;
using FalseGods.RuntimeContracts.Transport;
using FalseGods.UnityRuntime.Arena;
using FalseGods.UnityRuntime.Presentation;
using UnityEngine;
using ILogger = FalseGods.RuntimeContracts.Diagnostics.ILogger;
using Vector3 = UnityEngine.Vector3; // Protocol.Arena also declares a Vector3 (authoring type)

namespace FalseGods.Plugin
{
    /// <summary>
    /// The single-player / host encounter composition: one raise runs the canonical sequence — load the shipped
    /// arena content, realize it around the player, pass the fail-closed ready gate, then start the boss on the
    /// arena's authoritative floor — and one drop tears everything down in reverse
    /// (Docs/MultiplayerLoadingContract.md §5.3, Docs/ArenaLoadingProposal.md §2.4).
    /// </summary>
    /// <remarks>
    /// One sequence, two required sets. Single-player's required set is the one local peer, so the real
    /// <see cref="EncounterReadyGate"/> resolves the instant the local report validates and the boss starts in
    /// the same frame. A multiplayer host realizes locally the same way, then opens a
    /// <see cref="HostEncounterGate"/> — <c>EnterArena</c> broadcast, every roster peer's <c>ArenaReady</c>
    /// collected, silent peers timed out — and the boss starts only when that gate resolves; an abort tears the
    /// local arena down and the clients get one <c>EncounterAborted</c>. Players are never placed before the
    /// gate passes (§5.3): the arena walls seal the space the players are already standing in.
    ///
    /// <para>
    /// The arena is placed so its authored player-spawn marker lands at the host player's feet (the measured P4
    /// pattern; the game's own seal/teleport is not yet bridged), and the boss spawns at the authored
    /// enemy-spawn marker on the arena floor. On drop, a hosting controller broadcasts <c>EncounterEnded</c> so
    /// clients discard their puppet and arena (§5.11). Each tick drains each simulation exactly once and fans
    /// the same lists to presentation, to the <see cref="EncounterCoordinator"/>, and to replication.
    /// </para>
    /// </remarks>
    internal sealed class LocalEncounterController
    {
        internal const float EyeToFootDrop = 1.6f;

        internal const string BundleFileName = "falsegods-poc-room.bundle";
        internal const string ArtifactFileName = "arena-content-PocRoom.artifact";
        internal const string ArenaPrefabName = "PocRoom";

        /// <summary>Default sanity ceiling on a single client-reported hit — bounds a forged message, not a
        /// substitute for rate limiting; set generously above any legitimate single weapon hit.</summary>
        internal const float DefaultMaxClientHitDamage = 1000f;

        private const string PhaseTwoGroup = "phase_2";
        private const float GateTimeoutSeconds = 30f;

        private readonly ILogger _logger;
        private readonly ISimulationClock _clock;
        private readonly IEncounterParticipantQuery _participants;
        private readonly string _contentDirectory;
        private readonly float _maxClientHitDamage;

        private BossSimulation? _boss;
        private BossPresenter? _presenter;
        private BossPresentation? _presentation;
        private ArenaSimulation? _arena;
        private EncounterCoordinator? _coordinator;
        private ArenaPresentation? _arenaPresentation;
        private BundleArenaRealization? _realization;
        private ArenaLoadFlow? _flow;
        private EncounterHostReplication? _replication;
        private IDisposable? _damageBinding;
        private HostHitIntake? _hitIntake;

        // Host-gate state, present only while raised (or raising) as a session host.
        private IFalseGodsIntegration? _hostIntegration;
        private ReplicationSender? _hostSender;
        private HostEncounterGate? _hostGate;
        private ArenaRealizeResult? _pendingStart;

        private EncounterId _encounter;
        private WorldPosition _originWire;

        public LocalEncounterController(ILogger logger, float maxClientHitDamage = DefaultMaxClientHitDamage)
        {
            _logger = logger;
            _maxClientHitDamage = maxClientHitDamage;
            // The single-player Core-port bundle. Clock and roster are stateless and shared across raises; the RNG is
            // reseeded per raise so successive fights vary.
            _clock = new SulfurSimulationClock();
            _participants = new SulfurParticipantQuery();
            _contentDirectory = Path.GetDirectoryName(typeof(LocalEncounterController).Assembly.Location) ?? ".";
        }

        public bool IsUp => _presentation != null;

        /// <summary>Whether the controller owns a live encounter attempt — fighting, or still gating.</summary>
        public bool IsActiveEncounter => IsUp || _hostGate != null;

        /// <summary>Whether a host replication driver is currently attached.</summary>
        public bool HasReplication => _replication != null;

        /// <summary>This encounter's validated manifest (for a mid-fight host attach), or null before the gate.</summary>
        public ArenaManifest? CurrentManifest => _flow?.Manifest;

        /// <summary>The realized arena's host-chosen origin, for a mid-fight replication attach.</summary>
        public WorldPosition CurrentOrigin => _originWire;

        /// <summary>
        /// Attach (or, with <c>null</c>, detach) the host replication driver mid-encounter — the session can start
        /// or end while a fight is running. The simulations and presentation are unaffected either way.
        /// </summary>
        public void SetReplication(EncounterHostReplication? replication)
        {
            if (!ReferenceEquals(_replication, replication))
            {
                _replication = replication;
                _logger?.Log(replication != null
                    ? "Host replication attached: encounter state and events now broadcast to the session."
                    : "Host replication detached: encounter continues locally only.");
            }
        }

        /// <summary>Push the live sprite-facing choice to the renderer (changeable in-game via config).</summary>
        public void SetFacing(BossFacingMode mode, bool lockPitch)
        {
            if (_presentation != null)
            {
                _presentation.FacingMode = mode;
                _presentation.LockPitch = lockPitch;
            }
        }

        /// <summary>
        /// Run the canonical raise: prepare content → place the arena around the player → realize + navigation →
        /// ready gate → start the boss on the arena floor. With <paramref name="hostIntegration"/> the gate spans
        /// the whole roster and the boss starts from <see cref="Tick"/> when every peer proves matching content;
        /// without it the local gate resolves immediately. Fails closed at every step.
        /// </summary>
        public bool Raise(EncounterId encounter, IFalseGodsIntegration? hostIntegration)
        {
            if (IsActiveEncounter)
            {
                _logger?.LogWarning("An encounter is already up or gating; drop it first.");
                return false;
            }

            var camera = Camera.main;
            if (camera == null)
            {
                _logger?.LogWarning("Cannot start the encounter: no main camera. Load a level and stand in it first.");
                return false;
            }

            _encounter = encounter;
            _hostIntegration = hostIntegration;

            // ── LOAD (local): shipped bundle + artifact, parsed and hash-recomputed.
            _realization = new BundleArenaRealization(
                Path.Combine(_contentDirectory, BundleFileName),
                Path.Combine(_contentDirectory, ArtifactFileName),
                ArenaPrefabName,
                _logger);
            var realizationForNav = _realization;
            _flow = new ArenaLoadFlow(
                _realization,
                _realization,
                new AstarNavigationPort(() => realizationForNav.CurrentRoot, _logger));

            var prepared = _flow.Prepare();
            if (!prepared.Success || prepared.Artifact is null)
            {
                Abort($"arena content did not prepare: {prepared.FailureReason}");
                return false;
            }

            // ── PLACE: the authored player-spawn marker lands at the player's feet (P4 pattern).
            var eye = camera.transform.position;
            var foot = new ArenaWorldPoint(eye.x, eye.y - EyeToFootDrop, eye.z);
            ArenaWorldPoint origin;
            try
            {
                origin = ArenaPlacement.OriginForPlayerFoot(prepared.Artifact, foot);
            }
            catch (InvalidOperationException exception)
            {
                Abort($"arena placement refused: {exception.Message}");
                return false;
            }

            // ── REALIZE + NAVIGATION (the flow tears its own partials down on failure).
            var realized = _flow.Realize(origin);
            if (!realized.Success || realized.Manifest is null || realized.Arena is null)
            {
                Abort($"arena load failed: {realized.FailureReason}");
                return false;
            }

            _originWire = new WorldPosition(origin.X, origin.Y, origin.Z);

            if (hostIntegration != null)
            {
                // ── MULTIPLAYER GATE: EnterArena to every client; the boss starts from Tick when the whole
                // roster has proven matching content (§5.3 steps 1/4/5), or the attempt aborts (§5.3.1).
                _hostSender = new ReplicationSender(hostIntegration.Channel, hostIntegration.Session);
                _hostGate = new HostEncounterGate(
                    hostIntegration.Channel,
                    hostIntegration.Session,
                    hostIntegration.Roster,
                    _hostSender,
                    encounter,
                    realized.Manifest,
                    _originWire,
                    GateTimeoutSeconds);
                _pendingStart = realized;
                _hostGate.Open();
                _logger?.Log($"Encounter {encounter}: arena realized and EnterArena broadcast; waiting for every "
                    + $"session peer's ArenaReady (timeout {GateTimeoutSeconds:0}s).");
                return true;
            }

            // ── SINGLE-PLAYER GATE: the real gate over the one-member local roster — no second code path.
            var gate = new EncounterReadyGate(realized.Manifest, LocalRoster.Instance);
            var status = gate.SubmitReady(LocalRoster.LocalPeer, realized.Manifest);
            if (status != GateStatus.Resolved)
            {
                Abort($"ready gate did not resolve locally: {status}/{gate.AbortReason}");
                return false;
            }

            StartBoss(realized);
            return true;
        }

        /// <summary>
        /// Advance one frame. While gating: drive the gate (timeout clock, abort broadcast) and start the boss
        /// the moment it resolves. While fighting: advance the boss on host simulation time, drain both
        /// simulations once through presentation/coordinator/replication, and render. Otherwise a no-op.
        /// </summary>
        public void Tick(float deltaSeconds)
        {
            if (_hostGate != null && _pendingStart != null)
            {
                _hostGate.Tick(deltaSeconds);
                switch (_hostGate.Status)
                {
                    case GateStatus.Resolved:
                        var pending = _pendingStart;
                        _pendingStart = null;
                        _hostGate.Dispose(); // stops listening; the sender stays for replication + Ended
                        _hostGate = null;
                        _logger?.Log("Ready gate resolved for every session peer; starting the encounter.");
                        StartBoss(pending);
                        break;
                    case GateStatus.Aborted:
                        _logger?.LogWarning($"Encounter {_encounter} aborted at the gate: {_hostGate.AbortReason} "
                            + $"(outstanding: [{_hostGate.DescribeOutstanding()}]). Clients were told; tearing the "
                            + "local arena down.");
                        CleanupGate();
                        _flow?.Teardown();
                        _flow = null;
                        _realization = null;
                        break;
                }

                return;
            }

            if (_boss is null || _presenter is null || _presentation is null)
            {
                return;
            }

            _boss.Advance();
            Present();
            _presentation.Render(deltaSeconds);
        }

        /// <summary>Tear the encounter down in reverse: tell the clients (when hosting), then boss visuals and
        /// damage seam, then the arena — navigation restored to the level's baseline, hierarchy destroyed,
        /// bundle released.</summary>
        public void Drop()
        {
            BroadcastEndedIfHosting();
            CleanupGate();
            _hitIntake?.Dispose();
            _hitIntake = null;
            _damageBinding?.Dispose();
            _damageBinding = null;
            _presentation?.Dispose();
            _presentation = null;
            _presenter = null;
            _boss = null;
            _arenaPresentation = null;
            _coordinator?.BeginExit();
            _coordinator = null;
            _arena = null;
            _flow?.Teardown();
            _flow = null;
            _realization = null;
            _replication = null; // the driver is per-encounter; the next raise gets a fresh one
            _hostIntegration = null;
            _logger?.Log("Encounter torn down; arena navigation restored and nothing remains.");
        }

        /// <summary>Everything after the gate: encounter domain, boss on the arena's authoritative floor, and —
        /// when hosting — the replication driver, attached before the boss spawns so the spawn events replicate.</summary>
        private void StartBoss(ArenaRealizeResult realized)
        {
            var manifest = realized.Manifest!;
            var arena = realized.Arena!;

            _arena = new ArenaSimulation();
            _coordinator = new EncounterCoordinator(_encounter, _arena, new MechanismGroupId(PhaseTwoGroup));

            var definition = new BossDefinition(
                maxHealth: 100,
                phaseTwoHealthFraction: 0.5f,
                moveSpeed: 1.5f,
                idleSeconds: 2.0f,
                telegraphSeconds: 1.5f,
                commitSeconds: 0.4f,
                recoverSeconds: 2.0f,
                weakPointDamageMultiplier: 3);

            _boss = new BossSimulation(
                new BossInstanceId(1),
                definition,
                _clock,
                new SeededAuthoritativeRandom(Environment.TickCount),
                _participants);

            var bossSpawn = arena.BossSpawn;
            _presentation = new BossPresentation(_logger, new Vector3(bossSpawn.X, bossSpawn.Y, bossSpawn.Z));
            _presenter = new BossPresenter(_presentation);
            _arenaPresentation = new ArenaPresentation(_realization!, _logger);

            if (_hostIntegration != null && _hostSender != null)
            {
                SetReplication(new EncounterHostReplication(
                    _hostSender,
                    _hostIntegration.Session,
                    _hostIntegration.Roster,
                    _encounter,
                    new DefinitionId(1),
                    manifest,
                    _originWire));

                // Accept client hit intents for this encounter: validated (member + live encounter), clamped, and
                // routed through the same authoritative damage path a local weapon hit uses (§5.6). The result
                // replicates back as an ordinary BossDamaged event — the client never decides damage.
                _hitIntake = new HostHitIntake(
                    _hostIntegration.Channel,
                    _hostIntegration.Roster,
                    _encounter,
                    _maxClientHitDamage,
                    OnWeaponDamage,
                    message => _logger?.Log(message));
            }

            // Real weapon damage: the game's projectile/melee systems strike the Hitbox-layer capsule, find the
            // receiver on it, and deliver each hit's final damage; the sim then applies its own
            // weak-point/phase/death rules.
            _damageBinding = BossWeaponDamage.Bind(
                _presentation.HitCollider.gameObject, new WeaponSink(this), _logger);

            _coordinator.Begin();
            _boss.Spawn(new SimVector2(bossSpawn.X, bossSpawn.Z));
            Present();
            _presentation.Render(0f);

            _logger?.Log($"Encounter {_encounter} started: arena '{manifest.ArenaId}' at "
                + $"({_originWire.X:0.0}, {_originWire.Y:0.0}, {_originWire.Z:0.0}), {arena.NavWalkableNodes} "
                + $"walkable nav node(s), boss at ({bossSpawn.X:0.0}, {bossSpawn.Y:0.0}, {bossSpawn.Z:0.0}) on "
                + "the arena floor. Shoot or melee it; weak-window hits are amplified.");
        }

        /// <summary>
        /// One real weapon hit delivered by the <see cref="BossWeaponDamage"/> receiver. The game's final damage
        /// number is converted to simulation points and applied to the authoritative
        /// <see cref="BossSimulation.ApplyDamage"/> — the sim, not the weapon path, decides weak-point
        /// amplification, the phase-two crossing, and death. Presenting immediately keeps the hit reaction (and,
        /// on a host, its replication) on the same tick as the decision.
        /// </summary>
        private void OnWeaponDamage(float amount)
        {
            if (_boss is null || _boss.IsDead)
            {
                return;
            }

            var raw = WeaponDamage.ToSimAmount(amount);
            if (raw == 0)
            {
                return;
            }

            var healthBefore = _boss.Health;
            var phaseBefore = _boss.Phase;
            var weakWindow = _boss.IsWeakPointExposed;

            _boss.ApplyDamage(raw);
            Present();

            _logger?.Log(
                $"[weapon-damage] raw={raw} (game {amount:0.##}) weakWindow={weakWindow} "
                + $"health {healthBefore}->{_boss.Health} phase {phaseBefore}->{_boss.Phase}{(_boss.IsDead ? " DEAD" : string.Empty)}");
        }

        /// <summary>
        /// Drain each simulation exactly once and fan the results out: boss events to the presenter (the local
        /// view) and to the <see cref="EncounterCoordinator"/> (which drives the arena), the arena's resulting
        /// events to the arena presentation, and — when the host driver is attached — both streams to
        /// replication, on the same host simulation tick.
        /// </summary>
        private void Present()
        {
            if (_boss is null || _presenter is null || _arena is null || _coordinator is null)
            {
                return;
            }

            var bossEvents = _boss.DrainEvents();
            _presenter.Present(_boss, bossEvents);
            _coordinator.Process(bossEvents);

            var arenaEvents = _arena.DrainEvents();
            for (var i = 0; i < arenaEvents.Count; i++)
            {
                _arenaPresentation?.Handle(ArenaPresentationMapping.ToEvent(arenaEvents[i]));
            }

            _replication?.Publish(
                _boss, bossEvents, _arena, arenaEvents, _coordinator.Phase, new SimulationTick(_clock.Tick));
        }

        /// <summary>A failed raise leaves nothing behind: the flow's teardown is idempotent at any stage.</summary>
        private void Abort(string reason)
        {
            _logger?.LogWarning($"Encounter not started ({reason}). Nothing was placed; the fail-closed path "
                + "tore down whatever had been acquired.");
            CleanupGate();
            _flow?.Teardown();
            _flow = null;
            _realization = null;
            _arena = null;
            _coordinator = null;
            _hostIntegration = null;
        }

        /// <summary>Tell the clients the encounter is over (§5.11) — covers both a fight in progress and a
        /// gate still waiting (clients may hold a realized arena either way). Best-effort: a dead session
        /// cannot and need not be told.</summary>
        private void BroadcastEndedIfHosting()
        {
            if (_hostSender is null || _hostIntegration is null)
            {
                return;
            }

            try
            {
                if (_hostIntegration.Session.IsActive && _hostIntegration.Session.Role == SessionRole.Host)
                {
                    _hostSender.BroadcastEnded(new EncounterEnded(_encounter, new SimulationTick(_clock.Tick)));
                }
            }
            catch (Exception exception)
            {
                _logger?.LogWarning($"EncounterEnded broadcast failed ({exception.Message}); clients will fall "
                    + "back to session-end cleanup.");
            }
        }

        private void CleanupGate()
        {
            _hostGate?.Dispose();
            _hostGate = null;
            _hostSender = null;
            _pendingStart = null;
        }

        /// <summary>The single-player required set: exactly the one local peer (§5.3's degenerate case), run
        /// through the identical gate code path as multiplayer.</summary>
        private sealed class LocalRoster : IPlayerRoster
        {
            public static readonly LocalRoster Instance = new LocalRoster();

            public static readonly SessionPeerId LocalPeer = new SessionPeerId(0);

            private readonly IReadOnlyList<SessionPeerId> _members = new[] { LocalPeer };

            public IReadOnlyList<SessionPeerId> Members => _members;
        }

        /// <summary>The controller's own <see cref="IBossDamageSink"/> — a thin forwarder, so the binding holds no
        /// direct simulation reference.</summary>
        private sealed class WeaponSink : IBossDamageSink
        {
            private readonly LocalEncounterController _owner;

            public WeaponSink(LocalEncounterController owner) => _owner = owner;

            public void ApplyWeaponDamage(float amount) => _owner.OnWeaponDamage(amount);
        }
    }
}
