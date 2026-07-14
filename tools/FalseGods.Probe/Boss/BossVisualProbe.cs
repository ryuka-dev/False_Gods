using System;
using FalseGods.Application.Presentation;
using FalseGods.Core.Bosses;
using FalseGods.Core.Simulation;
using UnityEngine;

namespace FalseGods.Probe.Boss
{
    /// <summary>
    /// PoC step B0 (single-player half) / RiskList R15, R16, R27: drives the <b>real</b> boss stack in-game — a
    /// live <see cref="BossSimulation"/> (Core) fed by probe ports, mapped through the real
    /// <see cref="BossPresenter"/> + <see cref="BossPresentationMapping"/> (Application), rendered by a minimal
    /// probe-local <see cref="BossPresentationRenderer"/> implementing the real <see cref="Core"/>-side presentation
    /// seam. No production composition, no Plugin/Integration.Sulfur, no networking — just first light on the boss.
    /// </summary>
    /// <remarks>
    /// The point of going probe-first (Docs/DefinitionOfDone.md §3, the vertical-slice order) is to get eyes on the
    /// boss after the smallest possible step: this reuses the same load/toggle discipline as P3/P4 but builds no
    /// bundle — the boss is procedural primitives — so it needs only a level to stand in. You watch the sim's own
    /// idle -> telegraph -> commit -> recover cycle drive the renderer entirely through
    /// <see cref="PresentationState"/> / <see cref="IPresentationEvent"/>, and you damage the boss (which is the one
    /// authoritative combat decision, made by <see cref="BossSimulation.ApplyDamage"/>, exactly as a host would) to
    /// see phase two, the weak point, and death.
    ///
    /// <para>
    /// It mutates no game state: it reads the camera as the sole participant, spawns its own objects, and destroys
    /// them on teardown. The boss's colliders are triggers, so they never block the player; they exist only so an
    /// aimed ray can tell you whether your shot lands on the body or the exposed weak point — the R15 hitbox check.
    /// </para>
    /// </remarks>
    internal sealed class BossVisualProbe
    {
        private const int DamagePerHit = 15;
        private const float SpawnDistance = 7f;
        private const float EyeToFootDrop = 1.6f;

        private readonly ProbeSimulationClock _clock = new ProbeSimulationClock();
        private BossSimulation _boss;
        private BossPresenter _presenter;
        private FalseGods.UnityRuntime.Presentation.BossPresentation _renderer;

        public bool IsUp => _renderer != null;

        /// <summary>
        /// Push the sprite facing strategy to the renderer. Called each frame from config so the mode (and pitch
        /// lock) can be changed live in-game, the same way P9's client mode is.
        /// </summary>
        public void SetFacing(BossFacingMode mode, bool lockPitch)
        {
            if (_renderer != null)
            {
                _renderer.FacingMode = MapFacing(mode);
                _renderer.LockPitch = lockPitch;
            }
        }

        /// <summary>Map the probe's config enum onto the production presentation enum (same three members).</summary>
        private static FalseGods.UnityRuntime.Presentation.BossFacingMode MapFacing(BossFacingMode mode)
        {
            switch (mode)
            {
                case BossFacingMode.Fixed:
                    return FalseGods.UnityRuntime.Presentation.BossFacingMode.Fixed;
                case BossFacingMode.NearestPlayer:
                    return FalseGods.UnityRuntime.Presentation.BossFacingMode.NearestPlayer;
                case BossFacingMode.LocalBillboard:
                default:
                    return FalseGods.UnityRuntime.Presentation.BossFacingMode.LocalBillboard;
            }
        }

        /// <summary>
        /// Raise the boss in front of the camera and run one present so it is visible immediately. Returns false
        /// (with a note in the report) when there is no level camera to stand in.
        /// </summary>
        public bool Raise(ProbeReport report)
        {
            report.Section("B0 — boss first light (real BossSimulation -> BossPresenter -> renderer)");

            var camera = Camera.main;
            if (camera == null)
            {
                report.Line("  Skipped: no Camera.main. Enter a level and stand in it, then press the key again.");
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
                new ProbeAuthoritativeRandom(seed: Environment.TickCount),
                new CameraParticipantQuery());

            _renderer = new FalseGods.UnityRuntime.Presentation.BossPresentation(new ProbeLogger(report), spawn);
            _presenter = new BossPresenter(_renderer);

            _boss.Spawn(new SimVector2(spawn.x, spawn.z));
            Present();
            _renderer.Render(0f);

            report.Value("boss spawned at", spawn);
            report.Value("max health / phase-two at", $"{definition.MaxHealth} / {definition.PhaseTwoHealthThreshold}");
            report.Line("  Watch it idle -> telegraph -> commit -> recover. Aim at it and press the damage key to");
            report.Line("  hit it; hit it during the weak-point (recover) window for amplified damage, drop it to");
            report.Line("  half for phase two, and to zero for death. Press the boss key again to tear it down.");
            return true;
        }

        /// <summary>
        /// Advance one frame: step the sim clock, advance the boss, drain its events through the presenter, and
        /// render. Called every frame by the plugin while the stage is up. A no-op if not raised.
        /// </summary>
        public void Tick(float deltaSeconds)
        {
            if (_renderer == null)
            {
                return;
            }

            _clock.Advance(deltaSeconds);
            _boss.Advance();
            Present();
            _renderer.Render(deltaSeconds);
        }

        /// <summary>
        /// Deal damage where the camera is aimed. Raycasts (including triggers) from the screen centre; only a hit on
        /// one of the boss's own colliders (capsule, body box, or weak-point box) applies damage — anything nearer (a
        /// wall) blocks the shot, and a clean miss does nothing. Damage goes to the real
        /// <see cref="BossSimulation.ApplyDamage"/>; the sim, not the probe, decides amplification, the phase-two
        /// crossing, and death.
        /// </summary>
        public void Damage(ProbeReport report)
        {
            if (_boss == null || _renderer == null)
            {
                return;
            }

            var camera = Camera.main;
            if (camera == null)
            {
                report.Line("  [damage] no camera.");
                return;
            }

            if (_boss.IsDead)
            {
                report.Line("  [damage] boss already dead.");
                return;
            }

            var ray = camera.ViewportPointToRay(new Vector3(0.5f, 0.5f, 0f));
            var hits = Physics.RaycastAll(ray, 200f, ~0, QueryTriggerInteraction.Collide);
            Collider nearest = null;
            var nearestDistance = float.MaxValue;
            foreach (var hit in hits)
            {
                if (hit.distance < nearestDistance)
                {
                    nearestDistance = hit.distance;
                    nearest = hit.collider;
                }
            }

            // Occlusion: the nearest thing along the aim must be one of the boss's own colliders (the solid capsule,
            // the body box, or the weak-point box). Anything nearer — a wall — blocks the shot.
            var onBoss = nearest != null &&
                (nearest == _renderer.CollisionCollider || nearest == _renderer.BodyCollider || nearest == _renderer.WeakPointCollider);
            if (!onBoss)
            {
                report.Line("  [damage] missed the boss (aim the screen centre at it; a wall between blocks the shot).");
                return;
            }

            // The weak-point box sits inside the solid capsule, so it is rarely the NEAREST hit; test it directly to
            // tell a head shot from a body shot (R15 — the hitbox is where the weak point is drawn).
            var onWeakPoint = _renderer.WeakPointCollider != null &&
                _renderer.WeakPointCollider.Raycast(ray, out _, 200f);

            var healthBefore = _boss.Health;
            var phaseBefore = _boss.Phase;
            var weakExposed = _boss.IsWeakPointExposed;

            _boss.ApplyDamage(DamagePerHit);
            // Fold the resulting BossDamaged/BossPhaseChanged/BossDied events into the presentation immediately.
            Present();

            report.Line(
                $"  [damage] hit {(onWeakPoint ? "WEAK POINT" : "body")} raw={DamagePerHit} " +
                $"weakExposed={weakExposed} health {healthBefore}->{_boss.Health} " +
                $"phase {phaseBefore}->{_boss.Phase}{(_boss.IsDead ? " DEAD" : string.Empty)}");
        }

        /// <summary>Tear the boss down; nothing from the probe remains in the level.</summary>
        public void Drop(ProbeReport report)
        {
            report.Section("B0 — boss teardown");
            _renderer?.Dispose();
            _renderer = null;
            _presenter = null;
            _boss = null;
            report.Line("  Boss removed. No probe objects remain.");
        }

        private void Present()
        {
            var events = _boss.DrainEvents();
            _presenter.Present(_boss, events);
        }
    }
}
