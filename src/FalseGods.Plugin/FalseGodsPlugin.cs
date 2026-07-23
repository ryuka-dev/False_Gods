using System;
using System.IO;
using BepInEx;
using BepInEx.Configuration;
using FalseGods.Application.Arena;
using FalseGods.Application.Combat;
using FalseGods.Application.Replication;
using FalseGods.Core.Bosses.Combat;
using FalseGods.Core.Simulation;
using FalseGods.Integration.Sulfur.Arena;
using FalseGods.Integration.Sulfur.Combat;
using FalseGods.Plugin.Diagnostics;
using FalseGods.RuntimeContracts.Arena;
using FalseGods.RuntimeContracts.Integration;
using FalseGods.UnityRuntime.Presentation;
using UnityEngine;
using UnityEngine.InputSystem;

namespace FalseGods.Plugin
{
    /// <summary>
    /// The False Gods base plugin — the BepInEx entry point and Composition Root.
    /// </summary>
    /// <remarks>
    /// This is the only module that constructs concrete implementations and wires them through ports, and the
    /// <b>only permitted reader</b> of the <see cref="FalseGodsIntegrations"/> broker (Docs/Architecture.md §4,
    /// FG-ARCH-005). It holds no CLR dependency on <c>FalseGods.Integration.SulfurTogether</c> (FG-ARCH-002): the
    /// optional ST adapter is a separate companion plugin that self-registers through the broker after this
    /// plugin's <c>Awake</c> has subscribed (its hard <c>[BepInDependency]</c> on <see cref="PluginGuid"/> pins
    /// that order).
    ///
    /// <para>
    /// The three compositions (Architecture §4.3), re-evaluated every frame from the registered integration's
    /// live session state — a session starts and ends in-game, not at plugin load:
    /// <list type="bullet">
    /// <item><b>Single-player</b> (no integration, or no active session): the local
    /// <see cref="LocalEncounterController"/> stack; replication absent.</item>
    /// <item><b>Host</b>: the same controller with an <see cref="EncounterHostReplication"/> attached — the host
    /// adds replication, it does not swap boss implementations.</item>
    /// <item><b>Client</b>: a <see cref="ClientBossController"/> — presentation only, driven by the host's
    /// stream; the raise/damage keys are inert.</item>
    /// </list>
    /// When the adapter's registration token is disposed, the change event fires and the next frame falls back to
    /// the single-player composition (PoC B0).
    /// </para>
    ///
    /// <para>
    /// <see cref="PluginGuid"/> is stable because the ST adapter declares a <c>[BepInDependency]</c> on it. The
    /// raise key is a development affordance, not shipping gameplay; damage is the real weapon path (the game's
    /// projectile/melee systems hitting the boss's collision body). The arena identity, hash, and origin all come
    /// from the real load flow — a raise without valid arena content fails closed.
    /// </para>
    /// </remarks>
    [BepInPlugin(PluginGuid, PluginName, PluginVersion)]
    public sealed class FalseGodsPlugin : BaseUnityPlugin
    {
        public const string PluginGuid = "ryuka_labs.falsegods";
        public const string PluginName = "False Gods";
        public const string PluginVersion = "0.4.0";

        private const int TestBossDefinition = 1;

        // Bring-up throw shape: far enough ahead to read as incoming, high enough and slow enough to be shot.
        private const float ThrowDistance = 14f;
        private const float ThrowHeight = 1.5f;
        private const float ThrowSeconds = 1.6f;
        private const float ThrowApex = 3f;

        // Drop shape: a short reach ahead of the player and a few metres up, so a dropped crate visibly falls and
        // piles at a consistent spot when the key is tapped repeatedly.
        private const float DropDistance = 6f;
        private const float DropHeight = 4f;

        // Volley shape: lift a handful of crates off the pile, hold them a beat to telegraph, then scatter them
        // around where the player will be. Bring-up numbers - readable rise, a spread that surrounds without being
        // unfair.
        private const int VolleyCount = 6;
        private const float VolleySpreadMin = 1.4f;
        private const float VolleySpreadMax = 4.2f;
        private const float VolleyLiftHeight = 5f;
        private const float VolleyLiftSeconds = 0.5f;

        // The telegraph hangs for a random span, rolled per volley from its seed, so the player cannot time a dodge
        // to a fixed rhythm - which is what lets the volley lead a moving player without being trivially side-stepped.
        private const float VolleyHoldMin = 0.5f;
        private const float VolleyHoldMax = 1.5f;

        // Salt for the hold roll, kept well clear of the scatter's salts (0..2*count+2) so the two draws from the
        // volley's one seed are independent.
        private const int VolleyHoldSalt = 9973;

        private const float VolleyFlightSeconds = 1.2f;
        private const float VolleyApex = 4f;

        // How much of a crate's airtime to lead the player by. 1.0 aims where the player would be if they held their
        // course for the whole flight; dialling it back softens the lead if the full prediction over-commits. Tuned
        // in-game together with the random telegraph above.
        private const float VolleyLeadFraction = 1f;

        // The fraction of each volley's crates aimed at the predicted point rather than the player's current spot.
        // Half and half means no single way of moving dodges a whole volley: jink to bait the lead and the
        // current-aimed crates still land on you; run straight and the lead-aimed crates still catch you ahead.
        private const float VolleyLeadShare = 0.5f;

        // The window the player's velocity is averaged over before it feeds the lead, so a stand-still jitter reads
        // as roughly no movement instead of a full-speed feint. Larger damps harder but trusts a real turn slower.
        private const float VolleyLeadSmoothingSeconds = 0.4f;

        // Crate impact: a crate detonates on a body it reaches in flight (a tight sphere) and splashes a wider
        // circle where it lands. The damage matches the vanilla cave boss's thrown mud ball (10); the radii and
        // knockback are common-sense values. All destined for authored boss/attack content, not shipping consts.
        private const int CrateHitDamage = 10;
        private const float CrateContactRadius = 1.2f;
        private const float CrateSplashRadius = 2.5f;
        private const float CrateKnockbackSpeed = 12f;
        private const float CrateKnockbackLift = 4f;

        // Initialised in Awake (Unity's lifecycle entry point, not the constructor); null! documents that contract.
        private ConfigEntry<Key> _raiseKey = null!;
        private ConfigEntry<BossFacingMode> _facingMode = null!;
        private ConfigEntry<bool> _lockPitch = null!;
        private ConfigEntry<float> _spriteScale = null!;
        private ConfigEntry<float> _maxClientHitDamage = null!;
        private ConfigEntry<Key> _hijackKey = null!;
        private ConfigEntry<float> _fogStartDistance = null!;
        private ConfigEntry<float> _fogEndDistance = null!;
        private ConfigEntry<Key> _throwCrateKey = null!;
        private ConfigEntry<Key> _dropCrateKey = null!;
        private ConfigEntry<Key> _volleyCrateKey = null!;

        // A fresh seed per volley so successive dev volleys scatter differently; the host would pick this and send
        // it once when this is wired to the boss, so every peer lays the same volley out.
        private int _nextVolleySeed = 1;

        private IThrownCratePort _crates = null!;
        private IPlayerMotionPort _playerMotion = null!;
        private TargetMotionTracker _playerVelocity = null!;

        private float _appliedFogStart;
        private float _appliedFogEnd;

        private BepInExLogger _log = null!;
        private IArenaHijackPort _hijack = null!;
        private HijackedArenaContent _levelArena = null!;
        private LocalEncounterController _boss = null!;
        private ClientBossController? _client;
        private IFalseGodsIntegration? _clientIntegration; // the integration _client was composed on

        private int _nextEncounter = 1;
        private EncounterId _currentEncounter;

        private void Awake()
        {
            _raiseKey = Config.Bind("Boss", "RaiseKey", Key.B,
                "Raise the test boss in front of you, or tear it down if it is already up. "
                + "Stand in a loaded level first. On a multiplayer client the key is inert - the host drives the boss. "
                + "Damage the boss with your real weapons (bullets and melee); hits during the weak-point window "
                + "are amplified. The game uses the new Input System.");

            _facingMode = Config.Bind("Boss", "FacingMode", BossFacingMode.LocalBillboard,
                "Sprite facing: Fixed = a static/scripted world facing (for a very large boss); LocalBillboard = "
                + "face the local player's camera position (the vanilla NPC default; honours LockPitch); "
                + "NearestPlayer = face the authoritative nearest-player direction, shared by every viewer.");

            _lockPitch = Config.Bind("Boss", "LockPitch", false,
                "In LocalBillboard facing, false = yaw + natural elevation pitch toward the camera (vanilla), "
                + "true = yaw only (upright). Ignored by the Fixed and NearestPlayer modes.");

            // TEMPORARY dev affordance — like the whole "Boss" config section, this exists only to tune the boss
            // in-engine during development. A shipping build fixes the boss size in authored content; players must
            // not be able to change it. Remove before release (do not let it ossify into shipped config).
            _spriteScale = Config.Bind("Boss", "SpriteScale", 2.0f,
                "[DEV/TEMPORARY - removed before release] Uniform visual size of the boss (body sprite, hitboxes, "
                + "and health bar scale together, the way the vanilla boss is one scaled sprite). 1.0 is the base "
                + "size; the default is tuned so the boss reads at roughly the vanilla cave boss's size. Changeable "
                + "live; non-positive values are ignored.");

            _maxClientHitDamage = Config.Bind("Multiplayer", "MaxClientHitDamage",
                LocalEncounterController.DefaultMaxClientHitDamage,
                "Host only: the largest single hit a multiplayer client may report against the boss. A sanity "
                + "ceiling on a forged message, not a substitute for rate limiting - set it above any legitimate "
                + "single weapon hit. The host clamps to this; the simulation still decides weak-point, phase, and "
                + "death. Read once at load.");

            // TEMPORARY dev affordance (Strategy A bring-up): load our arena as the first cave level through the
            // game's own level generation, so navigation is built natively (the additive raise fails on a large
            // arena when the live level's nav is not scanned at the raise site). This first step loads the real
            // cave level to prove the entry point; substituting our arena for the generated content follows. Not a
            // shipping control - a developer-menu entry replaces the keybind later.
            _hijackKey = Config.Bind("Boss", "HijackArenaKey", Key.H,
                "[DEV/TEMPORARY - removed before release] Load the boss arena as the first cave level through the "
                + "game's native level generation (native navigation and player spawn). The game uses the new Input "
                + "System.");

            // TEMPORARY dev affordance: the cave environment's fog cutoff is tuned for corridor-sized rooms, which
            // leaves a 60-unit boss arena's walls invisible from the middle of it. Tunable live so the look can be
            // found in-engine; the value it settles on belongs in the arena's authored content, not in a player
            // config.
            _fogStartDistance = Config.Bind("Arena", "FogStartDistance", 10f,
                "[DEV/TEMPORARY - removed before release] Distance at which the boss arena's fog begins to "
                + "thicken. Only applies to the arena loaded as a level; the level's own fog COLOUR is kept.");
            _fogEndDistance = Config.Bind("Arena", "FogEndDistance", 80f,
                "[DEV/TEMPORARY - removed before release] Distance at which the boss arena's fog becomes opaque. "
                + "The arena is 60 units across, so a far corner sits about 60 units from the player's spawn. "
                + "Changeable live while standing in the arena.");

            // TEMPORARY bring-up affordance for the thrown-destructible mechanic: throw one crate at the player,
            // with no boss involved, so the flight, the shoot-it-down, and the landing can be judged on their own.
            _throwCrateKey = Config.Bind("Boss", "ThrowCrateKey", Key.N,
                "[DEV/TEMPORARY - removed before release] Throw one of the game's crates at you, arcing, from a "
                + "few metres away. Shoot it down and it drops loot like any barrel; let it land and it breaks "
                + "with nothing.");

            // TEMPORARY bring-up affordance for the resting half of the destructible supply chain: drop a crate
            // in front of you under real gravity so it falls and piles. Tap it repeatedly to build a stack. This
            // is the foundation the later "boss lifts crates off a pile and fires them" step draws from.
            _dropCrateKey = Config.Bind("Boss", "DropCrateKey", Key.M,
                "[DEV/TEMPORARY - removed before release] Drop one of the game's crates a few metres in front of "
                + "you under real gravity; it falls, rests, and stacks with others. Tap repeatedly to build a "
                + "pile. Resting crates stay shootable.");

            // TEMPORARY bring-up affordance: fire a shotgun volley from the pile - lift several resting crates,
            // hold them a beat, then scatter them around you on an arc. Drop a pile with the drop key first.
            _volleyCrateKey = Config.Bind("Boss", "VolleyCrateKey", Key.V,
                "[DEV/TEMPORARY - removed before release] Lift a handful of resting crates off the pile, hold "
                + "them a moment, then fire them as a spread scattered around you. Shoot them out of the air for "
                + "loot; the ones that land drop nothing. Build a pile with the drop key first.");

            _log = new BepInExLogger(Logger);
            _crates = new SulfurThrownCratePort(
                _log,
                new SulfurCrateImpact(
                    CrateHitDamage, CrateContactRadius, CrateSplashRadius, CrateKnockbackSpeed, CrateKnockbackLift, _log));
            _playerMotion = new SulfurPlayerMotionPort();
            _playerVelocity = new TargetMotionTracker(VolleyLeadSmoothingSeconds);

            // The Strategy A generation hooks patch the base game, so they are installed once, here, rather than
            // as a side effect of constructing a port. They stay inert until a hijacked load arms them, and pull
            // the arena from content this root owns — the adapter cannot reach the bundle pipeline itself.
            LevelGenerationHijackPatches.Install(_log);
            _levelArena = new HijackedArenaContent(
                Path.GetDirectoryName(typeof(FalseGodsPlugin).Assembly.Location) ?? ".", _log);
            LevelGenerationHijack.ArenaRooms = _levelArena.CreateRoomSource();
            _appliedFogStart = _fogStartDistance.Value;
            _appliedFogEnd = _fogEndDistance.Value;
            LevelGenerationHijack.Fog = new ArenaFogRange(_appliedFogStart, _appliedFogEnd);

            _hijack = new SulfurArenaHijackPort(_log);

            // When a hijacked level left our arena standing, a raise fights in that one instead of loading a
            // second copy of the same content on top of itself.
            _boss = new LocalEncounterController(_log, _maxClientHitDamage.Value) { LevelArena = _levelArena };

            // Subscribe before any adapter can load (their hard BepInDependency on this GUID guarantees the order),
            // so a registration always lands in an initialized seam. Composition changes are applied in Update, in
            // one place; the handler only reports the transition.
            FalseGodsIntegrations.Changed += OnIntegrationChanged;

            Logger.LogMessage($"{PluginName} {PluginVersion} loaded. Raise/drop the boss: {_raiseKey.Value}; "
                + $"damage it with real weapons. Facing: {_facingMode.Value}, lockPitch: {_lockPitch.Value}. "
                + $"Multiplayer integration: {(FalseGodsIntegrations.Current != null ? "registered" : "none (single-player)")}.");
        }

        private void Update()
        {
            // DEV (Strategy A bring-up): load our arena as the native cave level. Role-independent - it drives the
            // game's own level load, not our additive raise. Temporary, like the whole "Boss" dev config section.
            if (KeyPressed(_hijackKey.Value))
            {
                _hijack.LoadHijackedArena();
                return;
            }

            ApplyFogChanges();

            // Track the player's velocity every frame so a volley can lead by the average, not the instant — read
            // once here and reused when a volley fires this frame.
            var playerMotion = _playerMotion.TryReadLocalPlayer();
            if (playerMotion.Known)
            {
                _playerVelocity.Observe(playerMotion.Velocity, Time.deltaTime);
            }
            else
            {
                _playerVelocity.Reset();
            }

            if (KeyPressed(_throwCrateKey.Value))
            {
                ThrowOneCrateAtThePlayer();
            }

            if (KeyPressed(_dropCrateKey.Value))
            {
                DropOneCrateNearThePlayer();
            }

            if (KeyPressed(_volleyCrateKey.Value))
            {
                LaunchCrateVolleyAtThePlayer();
            }

            // Crates fly on their own clock, not the encounter's: they outlive a boss and exist without one.
            _crates.Advance(Time.deltaTime);

            var integration = FalseGodsIntegrations.Current;
            var role = EvaluateRole(integration);

            if (role == CompositionRole.Client)
            {
                RunClientComposition(integration!);
                return;
            }

            TearDownClientComposition();
            RunLocalComposition(integration, role);
        }

        /// <summary>
        /// Bring-up throw: one crate, from a few metres in front of the player, landing at their feet. Enough to
        /// judge the arc, the shoot-it-down, and the landing before any of it is wired to a boss.
        /// </summary>
        private void ThrowOneCrateAtThePlayer()
        {
            var camera = Camera.main;
            if (camera == null)
            {
                _log.LogWarning("[crate] no main camera; stand in a level first.");
                return;
            }

            var eye = camera.transform.position;
            var foot = new ArenaWorldPoint(eye.x, eye.y - LocalEncounterController.EyeToFootDrop, eye.z);

            // Thrown from ahead of the player at roughly chest height, so the arc is visible rather than dropped
            // on their head.
            var forward = camera.transform.forward;
            var from = new ArenaWorldPoint(
                eye.x + forward.x * ThrowDistance,
                foot.Y + ThrowHeight,
                eye.z + forward.z * ThrowDistance);

            if (_crates.Throw(from, foot, ThrowSeconds, ThrowApex))
            {
                _log.Log($"[crate] crate thrown from ({from.X:0.0}, {from.Y:0.0}, {from.Z:0.0}); "
                    + $"{_crates.InFlight} in the air. Shoot it for loot, or let it land for none.");
            }
        }

        /// <summary>
        /// Bring-up drop: one crate a short reach in front of the player and a few metres up, left to real gravity
        /// so it falls and rests. Tapping the key repeatedly stacks a pile — the resting foundation the supply
        /// chain (produce, pile, carry, lift, fire) is built on.
        /// </summary>
        private void DropOneCrateNearThePlayer()
        {
            var camera = Camera.main;
            if (camera == null)
            {
                _log.LogWarning("[crate] no main camera; stand in a level first.");
                return;
            }

            var eye = camera.transform.position;
            var footY = eye.y - LocalEncounterController.EyeToFootDrop;

            // A little ahead of the player (flattened to the ground plane) and a few metres up, so it drops onto the
            // floor in view rather than onto their head.
            var forward = camera.transform.forward;
            var flat = new Vector3(forward.x, 0f, forward.z).normalized;
            var at = new ArenaWorldPoint(
                eye.x + flat.x * DropDistance,
                footY + DropHeight,
                eye.z + flat.z * DropDistance);

            if (_crates.Drop(at))
            {
                _log.Log($"[crate] crate dropped at ({at.X:0.0}, {at.Y:0.0}, {at.Z:0.0}); "
                    + $"{_crates.Resting} resting. Tap again to stack a pile.");
            }
        }

        /// <summary>
        /// Bring-up volley: lift several resting crates off the pile and fire them as a spread at where the player
        /// will be when they land. The telegraph hangs a random beat so the lead cannot be dodged on a fixed
        /// rhythm. Nothing happens without a pile — that is the mechanic, not a bug — so it says so when empty.
        /// </summary>
        private void LaunchCrateVolleyAtThePlayer()
        {
            var camera = Camera.main;
            if (camera == null)
            {
                _log.LogWarning("[crate] no main camera; stand in a level first.");
                return;
            }

            var seed = _nextVolleySeed++;

            // The telegraph length is part of the volley's seeded shape, so it is rolled here (where the seed is
            // chosen) and both the hover and the lead below are computed from the same span.
            var hold = SeededRandom.Range(seed, VolleyHoldSalt, VolleyHoldMin, VolleyHoldMax);

            // A crate's whole airtime is fixed by the shape, not the distance, so the lead is a single step: aim
            // where the player will be after they rise, hover, and fly. The foot height comes from the camera; only
            // the ground position is led.
            var airtime = (VolleyLiftSeconds + hold + VolleyFlightSeconds) * VolleyLeadFraction;
            var eye = camera.transform.position;
            var footY = eye.y - LocalEncounterController.EyeToFootDrop;

            var motion = _playerMotion.TryReadLocalPlayer();
            SimVector2 current;
            SimVector2 lead;
            if (motion.Known)
            {
                // The velocity is the smoothed average tracked each frame, not this instant's reading, so a
                // stand-still jitter no longer flings the lead across the arena.
                current = motion.Position;
                lead = LeadAim.Predict(motion.Position, _playerVelocity.SmoothedVelocity, airtime);
            }
            else
            {
                // No player to read: aim both at the camera, with no lead — the volley still fires, just at one spot.
                current = new SimVector2(eye.x, eye.z);
                lead = current;
            }

            var currentCenter = new ArenaWorldPoint(current.X, footY, current.Z);
            var leadCenter = new ArenaWorldPoint(lead.X, footY, lead.Z);

            var shape = new CrateVolleyShape(
                seed, VolleyCount, VolleySpreadMin, VolleySpreadMax,
                VolleyLiftHeight, VolleyLiftSeconds, hold, VolleyFlightSeconds, VolleyApex, VolleyLeadShare);

            var launched = _crates.LaunchVolley(currentCenter, leadCenter, shape);
            if (launched > 0)
            {
                _log.Log($"[crate] volley of {launched} lifted; {_crates.Resting} still resting. "
                    + $"Hold {hold:0.00}s; some aimed here ({current.X:0.0}, {current.Z:0.0}), "
                    + $"some led {airtime:0.00}s to ({lead.X:0.0}, {lead.Z:0.0}). Shoot them for loot.");
            }
            else
            {
                _log.Log("[crate] no resting crates to lift - build a pile with the drop key first.");
            }
        }

        /// <summary>
        /// Push a live fog edit into the standing arena, so the look can be tuned without reloading the level.
        /// Only while a hijacked arena is actually up: an ordinary level's fog is the level's business.
        /// </summary>
        private void ApplyFogChanges()
        {
            var start = _fogStartDistance.Value;
            var end = _fogEndDistance.Value;
            if (start == _appliedFogStart && end == _appliedFogEnd)
            {
                return;
            }

            _appliedFogStart = start;
            _appliedFogEnd = end;
            LevelGenerationHijack.Fog = new ArenaFogRange(start, end);

            if (_levelArena.IsLive)
            {
                SulfurLevelFog.TryApply(start, end, _log);
            }
        }

        private void OnDestroy()
        {
            FalseGodsIntegrations.Changed -= OnIntegrationChanged;

            // Never leave a boss behind if the plugin unloads while one is up.
            if (_boss != null && _boss.IsActiveEncounter)
            {
                _boss.Drop();
            }

            TearDownClientComposition();
        }

        private enum CompositionRole
        {
            SinglePlayer,
            Host,
            Client,
        }

        private CompositionRole EvaluateRole(IFalseGodsIntegration? integration)
        {
            if (integration is null || !integration.Session.IsActive)
            {
                return CompositionRole.SinglePlayer;
            }

            return integration.Session.Role == RuntimeContracts.Multiplayer.SessionRole.Host
                ? CompositionRole.Host
                : CompositionRole.Client;
        }

        /// <summary>Single-player and host: the local simulation stack, with replication attached iff hosting.</summary>
        private void RunLocalComposition(IFalseGodsIntegration? integration, CompositionRole role)
        {
            if (KeyPressed(_raiseKey.Value))
            {
                if (_boss.IsActiveEncounter)
                {
                    _boss.Drop();
                }
                else
                {
                    // A host raise hands the controller the integration: the controller realizes locally, then
                    // gates the whole roster over the channel and starts (with replication attached) only when
                    // the gate resolves. A single-player raise gates the one local peer and starts immediately.
                    _currentEncounter = new EncounterId(_nextEncounter++);
                    _boss.Raise(_currentEncounter, role == CompositionRole.Host ? integration : null);
                }
            }

            // The session can start or end mid-encounter; keep the attached driver consistent with the live role.
            var wantReplication = role == CompositionRole.Host && _boss.IsUp;
            if (wantReplication && !_boss.HasReplication && _boss.CurrentManifest != null)
            {
                _boss.SetReplication(BuildHostReplication(integration!, _boss.CurrentManifest, _boss.CurrentOrigin));
            }
            else if (!wantReplication && _boss.HasReplication)
            {
                _boss.SetReplication(null);
            }

            _boss.SetFacing(_facingMode.Value, _lockPitch.Value);
            _boss.SetSpriteScale(_spriteScale.Value);
            _boss.Tick(UnityEngine.Time.deltaTime); // also drives a waiting host gate; a no-op when idle
        }

        private void RunClientComposition(IFalseGodsIntegration integration)
        {
            // A boss raised locally (as single-player or host) cannot survive a switch to the client role: the
            // host's stream is now the only authority.
            if (_boss.IsActiveEncounter)
            {
                _boss.Drop();
            }

            if (_client != null && !ReferenceEquals(_clientIntegration, integration))
            {
                TearDownClientComposition(); // a different integration registered; recompose on its channel
            }

            if (_client is null)
            {
                _client = new ClientBossController(_log, integration);
                _clientIntegration = integration;
            }

            if (KeyPressed(_raiseKey.Value))
            {
                Logger.LogMessage("Multiplayer client: the host drives the boss; the raise key is inert here.");
            }

            _client.SetFacing(_facingMode.Value, _lockPitch.Value);
            _client.SetSpriteScale(_spriteScale.Value);
            _client.Tick(UnityEngine.Time.deltaTime);
        }

        private void TearDownClientComposition()
        {
            _client?.Dispose();
            _client = null;
            _clientIntegration = null;
        }

        private EncounterHostReplication BuildHostReplication(
            IFalseGodsIntegration integration,
            Protocol.Arena.ArenaManifest manifest,
            Protocol.Wire.WorldPosition arenaOrigin) =>
            new EncounterHostReplication(
                new ReplicationSender(integration.Channel, integration.Session),
                integration.Session,
                integration.Roster,
                _currentEncounter,
                new DefinitionId(TestBossDefinition),
                manifest,
                arenaOrigin);

        private void OnIntegrationChanged()
        {
            Logger.LogMessage(FalseGodsIntegrations.Current != null
                ? "Multiplayer integration registered; the host/client composition activates with the session."
                : "Multiplayer integration revoked; returning to the single-player composition.");
        }

        private static bool KeyPressed(Key key)
        {
            try
            {
                var keyboard = Keyboard.current;
                return keyboard != null && keyboard[key].wasPressedThisFrame;
            }
            catch (Exception)
            {
                // No keyboard device, or an unmapped key.
                return false;
            }
        }
    }
}
