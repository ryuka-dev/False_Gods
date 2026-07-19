using System;
using FalseGods.Application.Combat;
using FalseGods.Application.Presentation;
using FalseGods.Application.Replication;
using FalseGods.Core.Bosses;
using FalseGods.Core.Simulation;
using FalseGods.Integration.Sulfur.Combat;
using FalseGods.Integration.Sulfur.Simulation;
using FalseGods.Protocol.Wire;
using FalseGods.UnityRuntime.Presentation;
using UnityEngine;
using ILogger = FalseGods.RuntimeContracts.Diagnostics.ILogger;

namespace FalseGods.Plugin
{
    /// <summary>
    /// The single-player boss composition: it constructs the real boss stack — a Core <see cref="BossSimulation"/>
    /// driven by the game-facing ports, mapped through <see cref="BossPresenter"/>, rendered by
    /// <see cref="BossPresentation"/> — raises it in front of the player, advances it each frame, and tears it down.
    /// </summary>
    /// <remarks>
    /// This is the production equivalent of the throwaway B0 probe, on the real Composition Root: the same
    /// <see cref="BossSimulation"/> rules that a multiplayer host would run, the same single presentation entry point,
    /// wired to the actual game through <see cref="SulfurSimulationClock"/> / <see cref="SeededAuthoritativeRandom"/>
    /// / <see cref="SulfurParticipantQuery"/>. There is no networking, no arena, and no ST here (Architecture §4.3,
    /// single-player composition); replication and the ready gate are simply absent.
    ///
    /// <para>
    /// The boss is placed on the live level's floor in front of the player using the main camera, which is generic
    /// Unity, so no game type crosses into the Composition Root — reading the game's players stays inside
    /// <see cref="SulfurParticipantQuery"/>.
    /// </para>
    ///
    /// <para>
    /// This same controller <b>is</b> the multiplayer-host composition: the host adds replication to the
    /// single-player composition rather than swapping in a different boss implementation (Architecture §4.3). When
    /// the Composition Root attaches an <see cref="EncounterHostReplication"/> via <see cref="SetReplication"/>,
    /// every tick's drained events — the same list the presenter gets — and the boss state are also published to
    /// the encounter channel; with none attached, replication is simply absent.
    /// </para>
    /// </remarks>
    internal sealed class SinglePlayerBossController
    {
        internal const float EyeToFootDrop = 1.6f;

        private const float SpawnDistance = 7f;

        private readonly ILogger _logger;
        private readonly ISimulationClock _clock;
        private readonly IEncounterParticipantQuery _participants;

        private BossSimulation? _boss;
        private BossPresenter? _presenter;
        private BossPresentation? _presentation;
        private EncounterHostReplication? _replication;
        private IDisposable? _damageBinding;

        public SinglePlayerBossController(ILogger logger)
        {
            _logger = logger;
            // The single-player Core-port bundle. Clock and roster are stateless and shared across raises; the RNG is
            // reseeded per raise so successive fights vary.
            _clock = new SulfurSimulationClock();
            _participants = new SulfurParticipantQuery();
        }

        public bool IsUp => _presentation != null;

        /// <summary>Whether a host replication driver is currently attached.</summary>
        public bool HasReplication => _replication != null;

        /// <summary>
        /// Attach (or, with <c>null</c>, detach) the host replication driver. The Composition Root attaches one
        /// per encounter when this peer is the session host and detaches it when the role or session goes away —
        /// the simulation and presentation are unaffected either way.
        /// </summary>
        public void SetReplication(EncounterHostReplication? replication)
        {
            if (!ReferenceEquals(_replication, replication))
            {
                _replication = replication;
                _logger?.Log(replication != null
                    ? "Host replication attached: boss state and events now broadcast to the session."
                    : "Host replication detached: boss continues locally only.");
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
        /// Raise the boss on the level floor in front of the player and run one present so it is visible immediately.
        /// Returns false (with a note) when there is no level camera to place it against.
        /// </summary>
        public bool Raise()
        {
            var camera = Camera.main;
            if (camera == null)
            {
                _logger?.LogWarning("Cannot raise the boss: no main camera. Load a level and stand in it first.");
                return false;
            }

            var forward = camera.transform.forward;
            forward.y = 0f;
            forward = forward.sqrMagnitude > 1e-4f ? forward.normalized : Vector3.forward;
            var foot = camera.transform.position - Vector3.up * EyeToFootDrop;
            var spawn = foot + forward * SpawnDistance;

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

            _presentation = new BossPresentation(_logger, spawn);
            _presenter = new BossPresenter(_presentation);

            // Real weapon damage: the game's projectile/melee systems strike the Hitbox-layer capsule, find the
            // receiver on it, and deliver each hit's final damage; the sim then applies its own
            // weak-point/phase/death rules.
            _damageBinding = BossWeaponDamage.Bind(
                _presentation.HitCollider.gameObject, new WeaponSink(this), _logger);

            _boss.Spawn(new SimVector2(spawn.x, spawn.z));
            Present();
            _presentation.Render(0f);

            _logger?.Log($"Boss raised at ({spawn.x:0.0}, {spawn.y:0.0}, {spawn.z:0.0}); health {definition.MaxHealth}, "
                + $"phase two at {definition.PhaseTwoHealthThreshold}. It idles -> telegraphs -> commits -> recovers; "
                + "it faces and approaches the real player. Shoot or melee it with real weapons; hits during the "
                + "weak-point window are amplified.");
            return true;
        }

        /// <summary>
        /// Advance one frame: advance the boss on host simulation time, drain its events through the presenter, and
        /// render. A no-op when not raised.
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

        /// <summary>Tear the boss down; nothing from the encounter remains in the level.</summary>
        public void Drop()
        {
            _damageBinding?.Dispose();
            _damageBinding = null;
            _presentation?.Dispose();
            _presentation = null;
            _presenter = null;
            _boss = null;
            _replication = null; // the driver is per-encounter; the next raise gets a fresh one
            _logger?.Log("Boss torn down; nothing remains.");
        }

        /// <summary>The controller's own <see cref="IBossDamageSink"/> — a thin forwarder, so the binding holds no
        /// direct simulation reference.</summary>
        private sealed class WeaponSink : IBossDamageSink
        {
            private readonly SinglePlayerBossController _owner;

            public WeaponSink(SinglePlayerBossController owner) => _owner = owner;

            public void ApplyWeaponDamage(float amount) => _owner.OnWeaponDamage(amount);
        }

        /// <summary>
        /// Drain the simulation's events exactly once and fan them out: first to the presenter (the local view),
        /// then — when the host driver is attached — to replication, on the same host simulation tick.
        /// </summary>
        private void Present()
        {
            if (_boss is null || _presenter is null)
            {
                return;
            }

            var events = _boss.DrainEvents();
            _presenter.Present(_boss, events);
            _replication?.Publish(_boss, events, new SimulationTick(_clock.Tick));
        }
    }
}
