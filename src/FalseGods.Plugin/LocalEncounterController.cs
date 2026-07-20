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
    /// Single-player is the degenerate case of the same sequence: the required set is the one local peer, so the
    /// real <see cref="EncounterReadyGate"/> resolves the instant the local report validates — there is no second
    /// code path. The arena is placed so its authored player-spawn marker lands at the player's feet (the
    /// measured P4 pattern; the game's own seal/teleport is not yet bridged), the walls seal the space, and the
    /// boss spawns at the authored enemy-spawn marker on the arena floor — no more camera-derived floor height.
    ///
    /// <para>
    /// This same controller <b>is</b> the multiplayer-host composition: the host adds replication rather than
    /// swapping implementations (Architecture §4.3). The Composition Root supplies a replication factory; the
    /// controller invokes it once the gate has resolved (the manifest the driver needs exists only then) and
    /// before the boss spawns, so the boss's own spawn events are already replicated. Each tick drains each
    /// simulation exactly once and fans the same lists to presentation, to the <see cref="EncounterCoordinator"/>,
    /// and to replication.
    /// </para>
    /// </remarks>
    internal sealed class LocalEncounterController
    {
        internal const float EyeToFootDrop = 1.6f;

        private const string BundleFileName = "falsegods-poc-room.bundle";
        private const string ArtifactFileName = "arena-content-PocRoom.artifact";
        private const string ArenaPrefabName = "PocRoom";
        private const string PhaseTwoGroup = "phase_2";

        private readonly ILogger _logger;
        private readonly ISimulationClock _clock;
        private readonly IEncounterParticipantQuery _participants;
        private readonly string _contentDirectory;

        private BossSimulation? _boss;
        private BossPresenter? _presenter;
        private BossPresentation? _presentation;
        private ArenaSimulation? _arena;
        private EncounterCoordinator? _coordinator;
        private ArenaPresentation? _arenaPresentation;
        private BundleArenaRealization? _realization;
        private ArenaLoadFlow? _flow;
        private EncounterHostReplication? _replication;
        private Func<ArenaManifest, EncounterHostReplication>? _replicationFactory;
        private IDisposable? _damageBinding;

        public LocalEncounterController(ILogger logger)
        {
            _logger = logger;
            // The single-player Core-port bundle. Clock and roster are stateless and shared across raises; the RNG is
            // reseeded per raise so successive fights vary.
            _clock = new SulfurSimulationClock();
            _participants = new SulfurParticipantQuery();
            _contentDirectory = Path.GetDirectoryName(typeof(LocalEncounterController).Assembly.Location) ?? ".";
        }

        public bool IsUp => _presentation != null;

        /// <summary>Whether a host replication driver is currently attached.</summary>
        public bool HasReplication => _replication != null;

        /// <summary>This encounter's validated manifest (for a mid-fight host attach), or null before the gate.</summary>
        public ArenaManifest? CurrentManifest => _flow?.Manifest;

        /// <summary>
        /// Provide (or clear) the host replication factory for the <b>next</b> raise. The controller invokes it
        /// after the ready gate resolves — the manifest the driver carries exists only then — and before the boss
        /// spawns, so the spawn events themselves are replicated.
        /// </summary>
        public void SetReplicationFactory(Func<ArenaManifest, EncounterHostReplication>? factory) =>
            _replicationFactory = factory;

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
        /// ready gate → start the boss on the arena floor. Fails closed at every step — on failure nothing of the
        /// encounter remains and the log reports why.
        /// </summary>
        public bool Raise(EncounterId encounter)
        {
            if (IsUp)
            {
                _logger?.LogWarning("An encounter is already up; drop it first.");
                return false;
            }

            var camera = Camera.main;
            if (camera == null)
            {
                _logger?.LogWarning("Cannot start the encounter: no main camera. Load a level and stand in it first.");
                return false;
            }

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

            // ── READY GATE: the real gate over the one-member local roster (§5.3 — no second code path).
            var gate = new EncounterReadyGate(realized.Manifest, LocalRoster.Instance);
            var status = gate.SubmitReady(LocalRoster.LocalPeer, realized.Manifest);
            if (status != GateStatus.Resolved)
            {
                Abort($"ready gate did not resolve locally: {status}/{gate.AbortReason}");
                return false;
            }

            // ── START: encounter domain + boss on the arena's authoritative floor.
            _arena = new ArenaSimulation();
            _coordinator = new EncounterCoordinator(encounter, _arena, new MechanismGroupId(PhaseTwoGroup));

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

            var bossSpawn = realized.Arena.BossSpawn;
            _presentation = new BossPresentation(_logger, new Vector3(bossSpawn.X, bossSpawn.Y, bossSpawn.Z));
            _presenter = new BossPresenter(_presentation);
            _arenaPresentation = new ArenaPresentation(_realization, _logger);

            // The host driver attaches before the boss spawns so the spawn events themselves replicate; the
            // baseline it sends carries the real manifest hash.
            if (_replicationFactory != null)
            {
                SetReplication(_replicationFactory(realized.Manifest));
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

            _logger?.Log($"Encounter {encounter} started: arena '{realized.Manifest.ArenaId}' at "
                + $"({origin.X:0.0}, {origin.Y:0.0}, {origin.Z:0.0}), {realized.Arena.NavWalkableNodes} walkable "
                + $"nav node(s), boss at ({bossSpawn.X:0.0}, {bossSpawn.Y:0.0}, {bossSpawn.Z:0.0}) on the arena "
                + "floor. Gate resolved for the local peer. Shoot or melee it; weak-window hits are amplified.");
            return true;
        }

        /// <summary>
        /// Advance one frame: advance the boss on host simulation time, drain both simulations once through
        /// presentation/coordinator/replication, and render. A no-op when not raised.
        /// </summary>
        public void Tick(float deltaSeconds)
        {
            if (_boss is null || _presenter is null || _presentation is null)
            {
                return;
            }

            _boss.Advance();
            Present();
            _presentation.Render(deltaSeconds);
        }

        /// <summary>Tear the encounter down in reverse: boss visuals and damage seam first, then the arena —
        /// navigation restored to the level's baseline, hierarchy destroyed, bundle released.</summary>
        public void Drop()
        {
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
            _logger?.Log("Encounter torn down; arena navigation restored and nothing remains.");
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
            _flow?.Teardown();
            _flow = null;
            _realization = null;
            _arena = null;
            _coordinator = null;
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
