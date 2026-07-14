// This renderer is heavily Unity-interop and UnityEngine's API carries no nullable annotations, so the
// nullable-reference context adds noise without safety here (lazily-created objects like the telegraph, and the
// optional logger, are guarded by explicit null checks / _hasState). Disable it for this file only, the same
// rationale the throwaway probe renderer used; the rest of FalseGods.UnityRuntime keeps the repo default.
#nullable disable

using System;
using System.Collections.Generic;
using FalseGods.Core.Bosses;
using FalseGods.RuntimeContracts.Presentation;
using UnityEngine;

// Disambiguate from UnityEngine.ILogger — the boss uses the project-owned diagnostics seam.
using ILogger = FalseGods.RuntimeContracts.Diagnostics.ILogger;

namespace FalseGods.UnityRuntime.Presentation
{
    /// <summary>
    /// The production implementation of the <see cref="IEncounterPresentation"/> seam: a minimal billboard boss
    /// (body, weak point, muzzle, health bar, ground telegraph) driven <b>only</b> by <see cref="Apply"/> (continuous
    /// state) and <see cref="Handle"/> (discrete cues), with a per-frame <see cref="Render"/> visual step the driver
    /// calls each frame.
    /// </summary>
    /// <remarks>
    /// This is the single presentation entry point for single-player, host, and client (Docs/Architecture.md §7): each
    /// source is mapped to <see cref="PresentationState"/> / <see cref="IPresentationEvent"/> by
    /// <c>FalseGods.Application</c> and enters here identically. It references no <c>FalseGods.Protocol</c> type — a
    /// wire DTO never appears in these signatures — and decides <b>nothing</b> authoritative: not damage, phase,
    /// death, target, or attack outcome. Calling none of the three methods leaves the stage frozen (RiskList
    /// R16/R27).
    ///
    /// <para>
    /// Every visual fact here was measured in-game at B0 (Docs/MinimalProofOfConceptPlan.md §7.6.3, 2026-07-14) and is
    /// carried over from the throwaway probe renderer unchanged:
    /// <list type="bullet">
    /// <item>The unlit shader for flat coloured quads is <c>Sprites/Default</c> (the game ships no resident
    /// <c>Universal Render Pipeline/Lit</c>, so our bundle copies render pink).</item>
    /// <item>Facing follows SULFUR's own billboard model (Docs/Decompiled <c>NpcUpdateManager.UpdateFromPOV</c>): a
    /// sprite turns toward the camera <em>position</em>, not its look direction — so rotating the view never spins the
    /// boss, only moving does. <see cref="FacingMode"/> selects between the three strategies.</item>
    /// <item>Physical collision is a solid <c>CapsuleCollider</c> on the <c>Entities</c> layer (where
    /// <c>Npc.mainCollider</c> lives) plus a kinematic <c>Rigidbody</c>; the thin billboard boxes stay triggers used
    /// only by an aim ray.</item>
    /// </list>
    /// </para>
    /// </remarks>
    public sealed class BossPresentation : IEncounterPresentation, IDisposable
    {
        private const float BodyHeight = 2.4f;
        private const float BodyWidth = 1.6f;
        private const float WeakPointSize = 0.55f;
        private const float MuzzleForward = 0.9f;
        private const float HealthBarHeight = 0.18f;
        private const float HealthBarWidth = 1.8f;
        private const float HealthBarLift = 0.5f;   // above the body top

        // Local -Z is "the sprite's visible front" (a Unity Quad shows its -Z face once the pivot turns it to the
        // viewer), so these small negative offsets keep the weak point and health-bar fill in front, not z-fighting.
        private const float WeakPointDepth = -0.06f;
        private const float HealthFillDepth = -0.05f;

        private static readonly Color PhaseOneColor = new Color(0.20f, 0.70f, 0.65f);
        private static readonly Color PhaseTwoColor = new Color(0.85f, 0.35f, 0.18f);
        private static readonly Color WeakExposedColor = new Color(1.00f, 0.90f, 0.20f);
        private static readonly Color WeakHiddenColor = new Color(0.25f, 0.25f, 0.28f);
        private static readonly Color DeadColor = new Color(0.20f, 0.20f, 0.22f);
        private static readonly Color TelegraphProjectileColor = new Color(0.95f, 0.25f, 0.20f);
        private static readonly Color TelegraphAreaColor = new Color(0.95f, 0.55f, 0.10f);
        private static readonly Color HealthFillColor = new Color(0.30f, 0.80f, 0.35f);

        private readonly ILogger _logger;
        private readonly float _floorY;
        private readonly Shader _shader;
        private readonly string _shaderName;
        private readonly Vector3 _fixedForward;   // Mode 1 facing; captured toward the player at spawn

        private readonly GameObject _root;
        private readonly GameObject _collisionBody;  // solid capsule on the Entities layer; physical presence
        private readonly Transform _bodyBillboard; // body + weak point; obeys FacingMode. Pivot at the body centre.
        private readonly Transform _aimPivot;      // gameplay-space yaw toward the target; holds the muzzle
        private readonly Transform _body;
        private readonly Material _bodyMat;
        private readonly Transform _weakPoint;
        private readonly Material _weakMat;
        private readonly Transform _muzzle;
        private readonly Transform _healthBar;     // own camera-facing billboard, so the bar is always readable
        private readonly Transform _healthFill;
        private readonly Material _healthFillMat;

        private GameObject _telegraph;
        private Material _telegraphMat;

        private readonly List<Transient> _transients = new List<Transient>();

        private float _time;
        private PresentationState _state;
        private bool _hasState;
        private float _flashTimer;
        private bool _flashWeakPoint;
        private float _phasePulseTimer;
        private float _appearPulseTimer;
        private bool _dead;

        private bool _telegraphActive;
        private AttackVisualKind _telegraphKind;
        private Vector3 _telegraphAim;
        private float _telegraphStart;
        private float _telegraphSeconds;

        /// <param name="logger">Optional diagnostics sink; when null, the renderer logs nothing (logging is a pure
        /// diagnostic concern, never required for correct behaviour).</param>
        /// <param name="origin">World position of the boss's feet; its Y is the arena floor level.</param>
        public BossPresentation(ILogger logger, Vector3 origin)
        {
            _logger = logger;
            _floorY = origin.y;
            _shader = ResolveShader(logger, out _shaderName);

            _root = new GameObject("FalseGodsBoss");
            _root.transform.position = new Vector3(origin.x, _floorY, origin.z);

            _fixedForward = ComputeSpawnFacing(_root.transform.position);

            // Body pivot at the body centre, so a pitch (Mode 2) tips the sprite about its middle, not its feet.
            _bodyBillboard = new GameObject("BodyBillboard").transform;
            _bodyBillboard.SetParent(_root.transform, worldPositionStays: false);
            _bodyBillboard.localPosition = new Vector3(0f, BodyHeight * 0.5f, 0f);

            _aimPivot = new GameObject("AimPivot").transform;
            _aimPivot.SetParent(_root.transform, worldPositionStays: false);

            var body = CreateQuad("Body", _bodyBillboard, PhaseOneColor, out _bodyMat);
            _body = body.transform;
            _body.localPosition = Vector3.zero;
            _body.localScale = new Vector3(BodyWidth, BodyHeight, 1f);
            BodyCollider = AddBox(body);

            // Weak point near the top, slightly toward the viewer, with its OWN collider so R15's "hitbox detached
            // from the visible part" is directly checkable — the collider is where you see it.
            var weak = CreateQuad("WeakPoint", _bodyBillboard, WeakHiddenColor, out _weakMat);
            _weakPoint = weak.transform;
            _weakPoint.localPosition = new Vector3(0f, BodyHeight * 0.28f, WeakPointDepth);
            _weakPoint.localScale = new Vector3(WeakPointSize, WeakPointSize, 1f);
            WeakPointCollider = AddBox(weak);

            // Muzzle: an invisible transform on the aim pivot (world-space toward the target); projectiles leave here.
            _muzzle = new GameObject("Muzzle").transform;
            _muzzle.SetParent(_aimPivot, worldPositionStays: false);
            _muzzle.localPosition = new Vector3(0f, BodyHeight * 0.5f, MuzzleForward);

            // Health bar: its own transform under the root with its own camera-facing billboard, so it stays readable
            // regardless of which way the body is facing.
            var bg = CreateQuad("HealthBarBg", _root.transform, new Color(0.05f, 0.05f, 0.06f), out _);
            _healthBar = bg.transform;
            _healthBar.localPosition = new Vector3(0f, BodyHeight + HealthBarLift, 0f);
            _healthBar.localScale = new Vector3(HealthBarWidth, HealthBarHeight, 1f);
            var fill = CreateQuad("HealthBarFill", _healthBar, HealthFillColor, out _healthFillMat);
            _healthFill = fill.transform;
            _healthFill.localPosition = new Vector3(0f, 0f, HealthFillDepth);
            _healthFill.localScale = Vector3.one;
            _healthFillMat.renderQueue = _healthFillMat.renderQueue + 1;

            // Physical collision body: an upright capsule on the "Entities" layer — where SULFUR's NPCs live
            // (Npc.mainCollider is a CapsuleCollider on this layer) — so the player bumps into the boss exactly as it
            // does a vanilla NPC. Solid (not a trigger), with a kinematic Rigidbody so a moving obstacle blocks
            // cleanly without being shoved by forces. It sits on its own non-rotating child, separate from the thin
            // billboard hitbox boxes (which stay triggers, used only by the aimed-damage ray).
            _collisionBody = new GameObject("CollisionBody");
            _collisionBody.transform.SetParent(_root.transform, worldPositionStays: false);
            var entitiesLayer = LayerMask.NameToLayer("Entities");
            if (entitiesLayer >= 0)
            {
                _collisionBody.layer = entitiesLayer;
            }
            else
            {
                _logger?.LogWarning("'Entities' layer not found; boss collision left on the Default layer.");
            }

            var capsule = _collisionBody.AddComponent<CapsuleCollider>();
            capsule.direction = 1; // Y — upright
            capsule.height = BodyHeight;
            capsule.radius = BodyWidth * 0.45f;
            capsule.center = new Vector3(0f, BodyHeight * 0.5f, 0f);
            CollisionCollider = capsule;

            var rigidbody = _collisionBody.AddComponent<Rigidbody>();
            rigidbody.isKinematic = true;   // moved by the sim's position, never by physics forces
            rigidbody.useGravity = false;

            _logger?.Log($"boss renderer shader: {_shaderName} (supported={(_shader != null && _shader.isSupported)})");
            _logger?.Log($"boss renderer floor y: {_floorY}");
            _logger?.Log($"boss collision: capsule h={BodyHeight} r={BodyWidth * 0.45f:0.00} on layer "
                + $"'{(entitiesLayer >= 0 ? "Entities" : "Default")}' (kinematic Rigidbody)");
        }

        /// <summary>The sprite orientation strategy (see <see cref="BossFacingMode"/>). Settable live.</summary>
        public BossFacingMode FacingMode { get; set; } = BossFacingMode.LocalBillboard;

        /// <summary>In <see cref="BossFacingMode.LocalBillboard"/>: true = yaw only, false = yaw + elevation pitch.</summary>
        public bool LockPitch { get; set; }

        /// <summary>The boss body's world collider — an aim ray tests it to place a hit where you aim.</summary>
        public Collider BodyCollider { get; }

        /// <summary>The weak point's world collider, so an aimed hit can report whether it landed on the weak part.</summary>
        public Collider WeakPointCollider { get; }

        /// <summary>The solid physical capsule (Entities layer) — the boss's collision presence; classifies as a body hit.</summary>
        public Collider CollisionCollider { get; }

        // ── IEncounterPresentation ────────────────────────────────────────────────────────────────────────

        public void Apply(PresentationState state)
        {
            _state = state;
            _hasState = true;
        }

        public void Handle(IPresentationEvent presentationEvent)
        {
            switch (presentationEvent)
            {
                case BossAppeared _:
                    _appearPulseTimer = 0.4f;
                    _logger?.Log("[cue] BossAppeared");
                    break;
                case AttackTelegraphStarted e:
                    _telegraphActive = true;
                    _telegraphKind = e.Kind;
                    _telegraphAim = new Vector3(e.AimPoint.X, _floorY, e.AimPoint.Z);
                    _telegraphStart = _time;
                    _telegraphSeconds = Mathf.Max(0.05f, e.TelegraphSeconds);
                    _logger?.Log($"[cue] AttackTelegraphStarted {e.Kind} attack={e.Attack.Value} aim=({e.AimPoint.X:0.0},{e.AimPoint.Z:0.0})");
                    break;
                case AttackLanded e:
                    _telegraphActive = false;
                    HideTelegraph();
                    SpawnImpact(e.Kind, new Vector3(e.AimPoint.X, _floorY, e.AimPoint.Z));
                    _logger?.Log($"[cue] AttackLanded {e.Kind} attack={e.Attack.Value}");
                    break;
                case WeakPointVisibilityChanged e:
                    // Continuous glow is driven by state; the event is a one-shot emphasis pulse.
                    if (e.Exposed)
                    {
                        _flashTimer = 0.15f;
                        _flashWeakPoint = true;
                    }
                    break;
                case PhaseTransition e:
                    _phasePulseTimer = 0.5f;
                    _logger?.Log($"[cue] PhaseTransition -> phaseVisual={e.PhaseVisualId}");
                    break;
                case BossHit e:
                    _flashTimer = 0.12f;
                    _flashWeakPoint = e.WeakPointHit;
                    break;
                case BossDefeated _:
                    _dead = true;
                    // Death can arrive mid-telegraph (the sim just stops; it emits no attack-cancel event), so clear
                    // the live telegraph here or it would blink on over the corpse.
                    _telegraphActive = false;
                    HideTelegraph();
                    _logger?.Log("[cue] BossDefeated");
                    break;
            }
        }

        // ── presentation-internal visual step (advances no boss state) ────────────────────────────────────

        /// <summary>
        /// Advance the visuals by <paramref name="deltaSeconds"/>: place the boss from the last applied state, orient
        /// the sprite per <see cref="FacingMode"/>, animate the live telegraph and transient impacts, and decay
        /// flashes. Reads only the camera transform; it decides nothing and mutates no state outside this renderer.
        /// </summary>
        public void Render(float deltaSeconds)
        {
            if (deltaSeconds > 0f)
            {
                _time += deltaSeconds;
            }

            var camera = Camera.main;

            if (_hasState)
            {
                _root.transform.position = new Vector3(_state.Position.X, _floorY, _state.Position.Z);

                // Aim pivot yaws toward the boss's facing so the muzzle points at the target (gameplay space).
                if (Math.Abs(_state.Facing.X) > 1e-4f || Math.Abs(_state.Facing.Z) > 1e-4f)
                {
                    _aimPivot.rotation = Quaternion.LookRotation(new Vector3(_state.Facing.X, 0f, _state.Facing.Z), Vector3.up);
                }

                UpdateBodyColor();
                UpdateWeakPoint();
                UpdateHealthBar(_state.HealthFraction);
            }

            OrientBody(camera);

            // The health bar always faces the camera (yaw + pitch) so it stays readable from any angle.
            if (camera != null)
            {
                _healthBar.rotation = FaceCameraRotation(_healthBar.position, camera.transform.position, lockPitch: false);
            }

            AnimateTelegraph();
            AnimateTransients(deltaSeconds);

            if (_flashTimer > 0f)
            {
                _flashTimer -= deltaSeconds;
                if (_flashTimer <= 0f)
                {
                    UpdateBodyColor();
                    UpdateWeakPoint();
                }
            }

            if (_phasePulseTimer > 0f)
            {
                _phasePulseTimer -= deltaSeconds;
            }

            if (_appearPulseTimer > 0f)
            {
                _appearPulseTimer -= deltaSeconds;
            }

            if (_dead)
            {
                _bodyBillboard.localScale = Vector3.Lerp(_bodyBillboard.localScale, new Vector3(1f, 0.15f, 1f), deltaSeconds * 2f);
            }
            else
            {
                var pulse = 1f + (_phasePulseTimer > 0f ? _phasePulseTimer * 0.5f : 0f)
                              + (_appearPulseTimer > 0f ? _appearPulseTimer * 0.4f : 0f);
                _bodyBillboard.localScale = new Vector3(pulse, pulse, pulse);
            }
        }

        private void OrientBody(Camera camera)
        {
            switch (FacingMode)
            {
                case BossFacingMode.Fixed:
                    // Mode 1: a fixed world facing (SULFUR's disableBillboardRotation). Visible -Z faces _fixedForward.
                    _bodyBillboard.rotation = Quaternion.LookRotation(-_fixedForward, Vector3.up);
                    break;

                case BossFacingMode.NearestPlayer:
                {
                    // Mode 3: the authoritative facing the host computed toward the nearest participant — shared by
                    // every viewer. Yaw only (the domain facing is horizontal).
                    var facing = _hasState
                        ? new Vector3(_state.Facing.X, 0f, _state.Facing.Z)
                        : _fixedForward;
                    if (facing.sqrMagnitude < 1e-6f)
                    {
                        facing = _fixedForward;
                    }

                    _bodyBillboard.rotation = Quaternion.LookRotation(-facing.normalized, Vector3.up);
                    break;
                }

                case BossFacingMode.LocalBillboard:
                default:
                    // Mode 2: face the local camera position (SULFUR's default BillboardNpc), yaw + optional pitch.
                    if (camera != null)
                    {
                        _bodyBillboard.rotation = FaceCameraRotation(_bodyBillboard.position, camera.transform.position, LockPitch);
                    }

                    break;
            }
        }

        /// <summary>
        /// SULFUR's billboard math (NpcUpdateManager.UpdateFromPOV), adapted to a Unity Quad whose visible face is
        /// its local -Z: turn toward the camera <em>position</em> (so the view direction never spins the sprite),
        /// yaw only when <paramref name="lockPitch"/>, else yaw + elevation pitch with the overhead-degenerate guard.
        /// </summary>
        private Quaternion FaceCameraRotation(Vector3 spritePos, Vector3 cameraPos, bool lockPitch)
        {
            var toCamera = cameraPos - spritePos;
            if (toCamera.sqrMagnitude < 1e-6f)
            {
                return _bodyBillboard.rotation;
            }

            var n = toCamera.normalized;
            var flat = new Vector3(n.x, 0f, n.z);
            if (flat.sqrMagnitude < 1e-6f)
            {
                // Camera directly above/below: no meaningful yaw; keep the current horizontal facing.
                flat = _bodyBillboard.forward;
                flat.y = 0f;
                if (flat.sqrMagnitude < 1e-6f)
                {
                    flat = _fixedForward;
                }
            }

            flat = flat.normalized;
            if (lockPitch)
            {
                return Quaternion.LookRotation(-flat, Vector3.up);
            }

            // Full look toward the camera; forward is -n (the quad's front is -Z). Swap the up reference when the
            // camera is nearly overhead so LookRotation does not go degenerate (mirrors vanilla's guard on n).
            var up = Mathf.Abs(Vector3.Dot(Vector3.up, n)) > 0.999f ? Vector3.forward : Vector3.up;
            return Quaternion.LookRotation(-n, up);
        }

        private void UpdateBodyColor()
        {
            Color color;
            if (_dead)
            {
                color = DeadColor;
            }
            else if (_flashTimer > 0f && !_flashWeakPoint)
            {
                color = Color.white;
            }
            else
            {
                color = _hasState && _state.PhaseVisualId >= (int)BossPhase.Two
                    ? PhaseTwoColor
                    : PhaseOneColor;
            }

            SetColor(_bodyMat, color);
        }

        private void UpdateWeakPoint()
        {
            Color color;
            if (_flashTimer > 0f && _flashWeakPoint)
            {
                color = Color.white;
            }
            else
            {
                color = _hasState && _state.WeakPointExposed ? WeakExposedColor : WeakHiddenColor;
            }

            SetColor(_weakMat, color);
        }

        private void UpdateHealthBar(float fraction)
        {
            var f = Mathf.Clamp01(fraction);
            // Shrink the fill from the left: scale on X, then shift so its left edge stays put.
            _healthFill.localScale = new Vector3(f, 1f, 1f);
            _healthFill.localPosition = new Vector3(-(1f - f) * 0.5f, 0f, HealthFillDepth);
            SetColor(_healthFillMat, Color.Lerp(new Color(0.85f, 0.2f, 0.2f), HealthFillColor, f));
        }

        private void AnimateTelegraph()
        {
            if (!_telegraphActive)
            {
                return;
            }

            EnsureTelegraph();
            var progress = _telegraphSeconds > 0f ? Mathf.Clamp01((_time - _telegraphStart) / _telegraphSeconds) : 1f;

            _telegraph.transform.position = _telegraphAim + Vector3.up * 0.02f;
            _telegraph.transform.rotation = Quaternion.Euler(90f, 0f, 0f); // flat on the floor

            // Projectile telegraphs as a tightening ring; area telegraphs as a growing disc.
            var color = _telegraphKind == AttackVisualKind.Projectile ? TelegraphProjectileColor : TelegraphAreaColor;
            var size = _telegraphKind == AttackVisualKind.Projectile
                ? Mathf.Lerp(3.0f, 1.0f, progress)
                : Mathf.Lerp(0.3f, 3.0f, progress);
            _telegraph.transform.localScale = new Vector3(size, size, 1f);
            var blink = 0.5f + 0.5f * Mathf.Sin(_time * Mathf.Lerp(6f, 24f, progress));
            SetColor(_telegraphMat, color * (0.4f + 0.6f * blink));
        }

        private void SpawnImpact(AttackVisualKind kind, Vector3 aim)
        {
            if (kind == AttackVisualKind.Projectile)
            {
                var proj = CreateCube("Projectile", TelegraphProjectileColor);
                proj.position = _muzzle.position;
                proj.localScale = Vector3.one * 0.3f;
                _transients.Add(new Transient(proj.gameObject, from: _muzzle.position, to: aim, seconds: 0.28f, kind: TransientKind.Fly));
            }
            else
            {
                var burst = CreateQuad("AreaBurst", null, TelegraphAreaColor, out var mat);
                burst.transform.position = aim + Vector3.up * 0.03f;
                burst.transform.rotation = Quaternion.Euler(90f, 0f, 0f);
                burst.transform.localScale = new Vector3(3f, 3f, 1f);
                _transients.Add(new Transient(burst, from: aim, to: aim, seconds: 0.35f, kind: TransientKind.Fade, material: mat));
            }
        }

        private void AnimateTransients(float deltaSeconds)
        {
            for (var i = _transients.Count - 1; i >= 0; i--)
            {
                var t = _transients[i];
                t.Elapsed += deltaSeconds;
                var progress = t.Seconds > 0f ? Mathf.Clamp01(t.Elapsed / t.Seconds) : 1f;

                if (t.Object != null)
                {
                    if (t.Kind == TransientKind.Fly)
                    {
                        t.Object.transform.position = Vector3.Lerp(t.From, t.To, progress);
                    }
                    else if (t.Kind == TransientKind.Fade)
                    {
                        t.Object.transform.localScale = new Vector3(3f + progress * 2f, 3f + progress * 2f, 1f);
                    }
                }

                if (progress >= 1f)
                {
                    if (t.Object != null)
                    {
                        UnityEngine.Object.Destroy(t.Object);
                    }

                    _transients.RemoveAt(i);
                }
            }
        }

        public void Dispose()
        {
            foreach (var t in _transients)
            {
                if (t.Object != null)
                {
                    UnityEngine.Object.Destroy(t.Object);
                }
            }

            _transients.Clear();
            HideTelegraph();

            if (_telegraph != null)
            {
                UnityEngine.Object.Destroy(_telegraph);
                _telegraph = null;
            }

            if (_root != null)
            {
                UnityEngine.Object.Destroy(_root);
            }
        }

        // ── construction helpers ──────────────────────────────────────────────────────────────────────────

        /// <summary>Horizontal direction from the boss toward the camera at spawn, or world forward if no camera.</summary>
        private static Vector3 ComputeSpawnFacing(Vector3 bossPos)
        {
            var camera = Camera.main;
            if (camera == null)
            {
                return Vector3.forward;
            }

            var toCamera = camera.transform.position - bossPos;
            toCamera.y = 0f;
            return toCamera.sqrMagnitude > 1e-6f ? toCamera.normalized : Vector3.forward;
        }

        private GameObject CreateQuad(string name, Transform parent, Color color, out Material material)
        {
            var go = GameObject.CreatePrimitive(PrimitiveType.Quad);
            go.name = name;
            StripCollider(go); // primitives ship a MeshCollider; we add our own boxes only where a hitbox is wanted.
            if (parent != null)
            {
                go.transform.SetParent(parent, worldPositionStays: false);
            }

            material = new Material(_shader);
            SetColor(material, color);
            go.GetComponent<MeshRenderer>().sharedMaterial = material;
            return go;
        }

        private Transform CreateCube(string name, Color color)
        {
            var go = GameObject.CreatePrimitive(PrimitiveType.Cube);
            go.name = name;
            StripCollider(go);
            var material = new Material(_shader);
            SetColor(material, color);
            go.GetComponent<MeshRenderer>().sharedMaterial = material;
            return go.transform;
        }

        private static Collider AddBox(GameObject go)
        {
            var box = go.AddComponent<BoxCollider>();
            box.isTrigger = true; // never blocks the player; it exists only so an aimed ray can identify the part.
            return box;
        }

        private static void StripCollider(GameObject go)
        {
            // Immediate, not deferred: the boss's own body/weak-point boxes are added right after this, and a raycast
            // in the same frame must not pick up a primitive's leftover MeshCollider instead of our box (R15 identity).
            var collider = go.GetComponent<Collider>();
            if (collider != null)
            {
                UnityEngine.Object.DestroyImmediate(collider);
            }
        }

        private void EnsureTelegraph()
        {
            if (_telegraph != null)
            {
                _telegraph.SetActive(true);
                return;
            }

            _telegraph = GameObject.CreatePrimitive(PrimitiveType.Quad);
            _telegraph.name = "Telegraph";
            StripCollider(_telegraph);
            _telegraphMat = new Material(_shader);
            SetColor(_telegraphMat, TelegraphAreaColor);
            _telegraph.GetComponent<MeshRenderer>().sharedMaterial = _telegraphMat;
        }

        private void HideTelegraph()
        {
            if (_telegraph != null)
            {
                _telegraph.SetActive(false);
            }
        }

        /// <summary>
        /// Find a working unlit-ish shader for flat coloured quads. The P3 probe measured that the game ships no
        /// resident stock <c>Universal Render Pipeline/Lit</c> (Shader.Find misses) and our bundle's copies render
        /// pink; so we try a chain of shaders the built-in/URP runtime is likely to keep resident, and log which one
        /// answered so the render verdict is traceable (RiskList R6/R15). Measured (2026-07-14): Sprites/Default.
        /// </summary>
        private static Shader ResolveShader(ILogger logger, out string chosenName)
        {
            string[] candidates =
            {
                "Universal Render Pipeline/Unlit",
                "Sprites/Default",
                "Unlit/Color",
                "Universal Render Pipeline/Lit",
                "Hidden/Internal-Colored",
            };

            foreach (var name in candidates)
            {
                var shader = Shader.Find(name);
                if (shader != null && shader.isSupported)
                {
                    chosenName = name;
                    return shader;
                }
            }

            // Last resort: the always-present error shader still renders (magenta), which is itself a visible,
            // honest signal that no colour shader resolved rather than a silent invisible boss.
            logger?.LogWarning("No unlit colour shader resolved; boss will render with the fallback (magenta).");
            var fallback = Shader.Find("Hidden/InternalErrorShader");
            chosenName = fallback != null ? fallback.name : "<none>";
            return fallback;
        }

        private static void SetColor(Material material, Color color)
        {
            if (material == null)
            {
                return;
            }

            // Cover both spellings a colour shader might use: URP Unlit/Lit uses _BaseColor, the built-in and
            // Sprites/Default shaders use _Color. Only fall back to material.color (which maps to _Color) when
            // neither property exists, to avoid a per-frame "material has no _Color" warning on exotic shaders.
            var hasBase = material.HasProperty("_BaseColor");
            var hasColor = material.HasProperty("_Color");
            if (hasBase)
            {
                material.SetColor("_BaseColor", color);
            }

            if (hasColor)
            {
                material.SetColor("_Color", color);
            }

            if (!hasBase && !hasColor)
            {
                material.color = color;
            }
        }

        private enum TransientKind
        {
            Fly,
            Fade,
        }

        private sealed class Transient
        {
            public Transient(GameObject obj, Vector3 from, Vector3 to, float seconds, TransientKind kind, Material material = null)
            {
                Object = obj;
                From = from;
                To = to;
                Seconds = seconds;
                Kind = kind;
                Material = material;
            }

            public GameObject Object { get; }
            public Vector3 From { get; }
            public Vector3 To { get; }
            public float Seconds { get; }
            public TransientKind Kind { get; }
            public Material Material { get; }
            public float Elapsed { get; set; }
        }
    }
}
