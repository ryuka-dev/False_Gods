using System;
using System.Collections.Generic;
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

        // How high above a delivery pile a hand-placed crate appears, so it visibly falls onto whatever is already
        // stacked there rather than being born inside it.
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

        // How fast the boss can physically throw, NOT a limit on the attack: the crates rise together and are then
        // released one at a time so a volley reads as a barrage rather than one instant, but the boss is never the
        // bottleneck. What decides how many crates a player has coming at them is how many were carried to the
        // boss - the supply line is the throttle, so this is set well above any rate the room can supply.
        private const float VolleyCratesPerSecond = 10f;
        private const float VolleyFireInterval = 1f / VolleyCratesPerSecond;

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
        private SulfurCarriedLoadMirror? _carriedLoads;

        // Aiming state, reused every frame so tracking the room costs no allocation.
        private readonly List<PlayerAim> _playersToThrowAt = new List<PlayerAim>();
        private readonly List<CrateVolleyAim> _volleyAims = new List<CrateVolleyAim>();
        private readonly Dictionary<int, TrackedPlayer> _playerSpeeds = new Dictionary<int, TrackedPlayer>();
        private readonly HashSet<int> _playersSeen = new HashSet<int>();
        private readonly List<int> _forgottenPlayers = new List<int>();
        private IPlayerMotionPort _playerMotion = null!;

        private float _appliedFogStart;
        private float _appliedFogEnd;

        private BepInExLogger _log = null!;
        private IArenaHijackPort _hijack = null!;
        private ArenaLevelFlow? _levelFlow;
        private IFalseGodsIntegration? _levelFlowIntegration; // the integration _levelFlow was composed on
        private IDisposable? _spawnOwnership;                 // held while an integration carries our spawns
        private IFalseGodsIntegration? _spawnOwnershipIntegration;
        private CrateCommandFlow? _crateFlow;
        private IFalseGodsIntegration? _crateFlowIntegration;  // the integration _crateFlow was composed on
        private HijackedArenaContent _levelArena = null!;
        private SulfurArenaHazard _hazard = null!;
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

            // The boss's size and facing used to be config here. They are not player choices: the size is
            // authored in the arena (GameplayRoot/BossBody's scale) and read at load, and this boss faces the
            // local player the way the vanilla cave boss does, which is the presentation's default. Both are
            // properties of the boss, so neither belongs in a file a player can edit.

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
                "[DEV/TEMPORARY - removed before release] Take the session to the boss arena, which replaces the "
                + "first cave level and is generated natively (native navigation and player spawn); press it "
                + "again to pick up re-authored arena content. ONE press on EITHER machine is enough - the host "
                + "declares the level a boss arena for everyone and leads the transition, and a client's press is "
                + "a request to the host. Without a session it just goes there. The game uses the new Input "
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
                "[DEV/TEMPORARY - removed before release] Put one of the game's crates on the delivery pile beside "
                + "the boss, as a carrier will; it falls, rests, and stacks with others. Tap repeatedly to stock "
                + "the boss. Resting crates stay shootable, and only these are the boss's ammunition.");

            // TEMPORARY bring-up affordance: fire a shotgun volley from the pile - lift several resting crates,
            // hold them a beat, then scatter them around you on an arc. Drop a pile with the drop key first.
            _volleyCrateKey = Config.Bind("Boss", "VolleyCrateKey", Key.V,
                "[DEV/TEMPORARY - removed before release] Lift a handful of crates off the boss's delivery pile, "
                + "hold them a moment, then fire them as a spread scattered around you. Shoot them out of the air "
                + "for loot; the ones that land drop nothing. Only delivered crates can be fired - crates still "
                + "standing at a production point are not the boss's until somebody carries them over.");

            _log = new BepInExLogger(Logger);
            var cratePort = new SulfurThrownCratePort(
                _log,
                new SulfurCrateImpact(
                    CrateHitDamage, CrateContactRadius, CrateSplashRadius, CrateKnockbackSpeed, CrateKnockbackLift, _log));
            _crates = cratePort;

            // A destructible that a player destroyed is the one thing about a crate the other peers cannot work
            // out from the commands they were sent, so it is the one thing said out loud. Where it goes depends on
            // who this peer is, which the flow knows; with no session there is nobody to tell.
            cratePort.Died = (crateId, death) => _crateFlow?.ReportDestroyed(crateId, death);

            // Only ever used on a client: it puts the host's loads on the backs of the goblins this peer mirrored,
            // which the carry commands cannot do because they carry no idea of which goblin is which.
            _carriedLoads = new SulfurCarriedLoadMirror(cratePort, _log);
            _playerMotion = new SulfurPlayerMotionPort();

            // The Strategy A generation hooks patch the base game, so they are installed once, here, rather than
            // as a side effect of constructing a port. They stay inert until a hijacked load arms them, and pull
            // the arena from content this root owns — the adapter cannot reach the bundle pipeline itself.
            LevelGenerationHijackPatches.Install(_log);
            _levelArena = new HijackedArenaContent(
                Path.GetDirectoryName(typeof(FalseGodsPlugin).Assembly.Location) ?? ".", _log);

            // The arena's standing hazards read the volumes the cloned scenery brought with it, so they follow
            // whichever arena is up and go quiet with it.
            _hazard = new SulfurArenaHazard(
                () => _levelArena.Realization?.CurrentRoot!,
                VanillaPropDecoration.ParentPath,
                VanillaPropDecoration.MudPoolHazardVolumeName,
                VanillaPropDecoration.MudPoolHazardDamage,
                VanillaPropDecoration.MudPoolHazardInterval,
                _log);
            LevelGenerationHijack.ArenaRooms = _levelArena.CreateRoomSource();
            _appliedFogStart = _fogStartDistance.Value;
            _appliedFogEnd = _fogEndDistance.Value;
            LevelGenerationHijack.Fog = new ArenaFogRange(_appliedFogStart, _appliedFogEnd);

            _hijack = new SulfurArenaHijackPort(_log);

            // When a hijacked level left our arena standing, a raise fights in that one instead of loading a
            // second copy of the same content on top of itself.
            // Minions are the game's own units, spawned through the game's own entry point; the plugin is the
            // behaviour whose lifetime scopes those asynchronous loads.
            // The announce callback reaches for the crate flow at call time, not now: the flow is rebuilt whenever
            // the session changes, and without a session there is nobody to tell.
            _boss = new LocalEncounterController(
                _log,
                new SulfurMinionSpawnPort(this, _log),
                // A second, separately tracked band: the rage ends only when the emergency band is dead, so it
                // cannot share a roster with the ordinary waves that go on around it - and it is outlined, because
                // a player has to be able to tell which of the goblins on the floor are the ones that matter.
                new SulfurMinionSpawnPort(this, _log, outlined: true),
                // The other half of the rage, and the half the players cannot finish: the cave boss's own arms,
                // which rise beside it and keep throwing until the route feeds it again.
                new SulfurBossArmPort(this, _log),
                // One room, fought in for a long time, with waves summoned into it on purpose: without this the
                // floor ends the fight buried under everything the players have killed.
                new SulfurBattlefieldCleanup(_log),
                _crates,
                new SulfurCarrierPort(
                    this,
                    _crates,
                    _log,
                    (at, pile, count, radius) => _crateFlow?.BroadcastTaken(at, pile, count, radius),
                    (from, at, pile, count, seed) => _crateFlow?.BroadcastSetDown(from, at, pile, count, seed)),
                (at, pile) => _crateFlow?.BroadcastDropped(at, pile),
                LaunchCrateVolley,
                _maxClientHitDamage.Value) { LevelArena = _levelArena };

            // Subscribe before any adapter can load (their hard BepInDependency on this GUID guarantees the order),
            // so a registration always lands in an initialized seam. Composition changes are applied in Update, in
            // one place; the handler only reports the transition.
            FalseGodsIntegrations.Changed += OnIntegrationChanged;

            Logger.LogMessage($"{PluginName} {PluginVersion} loaded. Raise/drop the boss: {_raiseKey.Value}; "
                + "damage it with real weapons. "
                + $"Multiplayer integration: {(FalseGodsIntegrations.Current != null ? "registered" : "none (single-player)")}.");
        }

        private void Update()
        {
            // The session's agreement on which level is the boss arena has to exist before anyone asks to go
            // there, so it is maintained every frame rather than only while an encounter is up.
            MaintainArenaLevelFlow(FalseGodsIntegrations.Current);
            MaintainSpawnOwnership(FalseGodsIntegrations.Current);
            MaintainCrateFlow(FalseGodsIntegrations.Current);

            // DEV (Strategy A bring-up): take the session to the arena. Role-independent for the player - one
            // press on either machine gets everyone there - but not role-independent underneath: the host
            // declares and leads, a client asks. Temporary, like the whole "Boss" dev config section.
            if (KeyPressed(_hijackKey.Value))
            {
                GoToArenaLevel();
                return;
            }

            ApplyFogChanges();

            // Track EVERY player's velocity each frame so a volley can lead all of them by the average rather than
            // the instant — and so a barrage threatens the whole room, not whoever happens to be hosting.
            TrackPlayerMotion(Time.deltaTime);

            // The destructibles are host-authoritative like everything else that changes the world: the host does
            // the thing and tells everyone, and a client's key does nothing rather than producing a second set of
            // crates only it can see.
            if (KeyPressed(_throwCrateKey.Value) && CrateKeysActHere())
            {
                ThrowOneCrateAtThePlayer();
            }

            if (KeyPressed(_dropCrateKey.Value) && CrateKeysActHere())
            {
                DeliverOneCrateToTheBoss();
            }

            if (KeyPressed(_volleyCrateKey.Value) && CrateKeysActHere())
            {
                LaunchCrateVolleyAtThePlayer();
            }

            // Crates fly on their own clock, not the encounter's: they outlive a boss and exist without one.
            _crates.Advance(Time.deltaTime);

            var integration = FalseGodsIntegrations.Current;
            var role = EvaluateRole(integration);

            // The sludge burns only where the world is ours. A client's pool is scenery: what standing in it costs
            // is settled on the host, exactly as the boss's own hits are, so nobody is burned twice.
            if (role != CompositionRole.Client)
            {
                _hazard.Advance(Time.deltaTime);
            }

            if (role == CompositionRole.Client)
            {
                RunClientComposition(integration!);
                return;
            }

            TearDownClientComposition();
            RunLocalComposition(integration, role);
        }

        /// <summary>
        /// Whether this peer's destructible keys do anything. A client's do not: the host owns what happens to
        /// the world, and a client that made its own crates would be dodging a different volley from everyone
        /// else's. Without a session there is only this peer, so they work.
        /// </summary>
        private bool CrateKeysActHere()
        {
            var integration = FalseGodsIntegrations.Current;
            if (integration is null || !integration.Session.IsActive)
            {
                return true;
            }

            if (integration.Session.Role == RuntimeContracts.Multiplayer.SessionRole.Host)
            {
                return true;
            }

            Logger.LogMessage("Multiplayer client: the host throws the crates; this key is inert here.");
            return false;
        }

        /// <summary>
        /// Keep the destructible-command flow composed on whatever integration is live, so a client can build the
        /// host's crates. Torn down with the session — without one there is nobody to tell.
        /// </summary>
        private void MaintainCrateFlow(IFalseGodsIntegration? integration)
        {
            if (integration is null || !integration.Session.IsActive)
            {
                if (_crateFlow != null)
                {
                    _crateFlow.Dispose();
                    _crateFlow = null;
                    _crateFlowIntegration = null;

                    // The mirrored loads exist only for as long as there is a host telling us about them.
                    _carriedLoads?.Clear();
                }

                return;
            }

            if (_crateFlow != null && !ReferenceEquals(_crateFlowIntegration, integration))
            {
                _crateFlow.Dispose();
                _crateFlow = null;
                _crateFlowIntegration = null;
            }

            if (_crateFlow is null)
            {
                // Each applied command is logged the way the host logs the one it made, so the two sides can be
                // compared. A volley that scattered differently would otherwise be invisible in the logs, and the
                // whole point of sending a seed instead of positions is that it must not.
                _crateFlow = new CrateCommandFlow(integration.Channel, integration.Session)
                {
                    OnDropped = (at, pile) =>
                    {
                        _crates.Drop(at, pile);
                        _log.Log($"[crate] host dropped one on {pile} at ({at.X:0.0}, {at.Y:0.0}, {at.Z:0.0}); "
                            + $"{_crates.RestingOn(pile)} resting on that pile here.");
                    },
                    OnThrown = (from, to, seconds, apex) =>
                    {
                        _crates.Throw(from, to, seconds, apex);
                        _log.Log($"[crate] host threw one from ({from.X:0.0}, {from.Y:0.0}, {from.Z:0.0}) to "
                            + $"({to.X:0.0}, {to.Y:0.0}, {to.Z:0.0}); {_crates.InFlight} in the air here.");
                    },
                    OnTaken = (at, pile, count, radius) =>
                    {
                        var took = _crates.TakeFrom(pile, count, at, radius);
                        _carriedLoads?.PickedUp(at, count);
                        _log.Log($"[carrier] host collected {count} off {pile}; {took} taken here, "
                            + $"{_crates.RestingOn(pile)} left on it.");
                    },
                    OnSetDown = (from, at, pile, count, seed) =>
                    {
                        var placed = _crates.TossRing(from, at, pile, count, seed);
                        _carriedLoads?.PutDown(from);
                        _log.Log($"[carrier] host put {count} down on {pile} (seed {seed}); {placed} laid out "
                            + $"here, {_crates.RestingOn(pile)} on that pile.");
                    },
                    OnVolleyFired = (pile, aims, shape) =>
                    {
                        var launched = _crates.LaunchVolley(pile, aims, shape);
                        _log.Log($"[crate] host fired a volley of {shape.Count} off {pile} (seed {shape.Seed}) "
                            + $"spread over {aims.Count} player(s); {launched} lifted here.");
                    },
                    OnDestroyed = (crateId, death) =>
                    {
                        var destroyed = _crates.Destroy(crateId, death);
                        _log.Log($"[crate] host settled that crate {crateId} was {Describe(death)}; "
                            + (destroyed ? "destroyed here too." : "this peer no longer had it."));
                    },
                    OnDestroyRequested = (crateId, death) =>
                    {
                        // A client saw one of its own crates destroyed. The host destroys the same one and then
                        // says so to everyone, the client included: settled once, in the one place where the
                        // session layer expects the loot to be rolled.
                        var destroyed = _crates.Destroy(crateId, death);
                        _crateFlow?.BroadcastDestroyed(crateId, death);
                        _log.Log($"[crate] a client's player {Describe(death)} crate {crateId}; "
                            + (destroyed ? "destroyed here and settled for everyone." : "the host no longer had it."));
                    },
                };
                _crateFlowIntegration = integration;
            }
        }

        /// <summary>How a destruction reads in the log, so the two peers' lines can be compared at a glance.</summary>
        private static string Describe(CrateDeath death) =>
            death == CrateDeath.Shot ? "shot" : "burst on a player";

        /// <summary>
        /// Declare this plugin a host-authoritative spawner for as long as an integration is live. The boss's
        /// minions are spawned with this component as their owner, on the host only; without the declaration the
        /// session layer has no reason to think they should travel, and a client's room stays empty.
        /// </summary>
        private void MaintainSpawnOwnership(IFalseGodsIntegration? integration)
        {
            if (ReferenceEquals(integration, _spawnOwnershipIntegration))
            {
                return;
            }

            _spawnOwnership?.Dispose();
            _spawnOwnership = integration?.Spawns.DeclareHostAuthoritative(this);
            _spawnOwnershipIntegration = integration;

            // Who is still in the fight is a session fact, so the gate follows the session: with one, the boss and
            // everything it aims stop attacking players who are down; without one, everybody is fighting.
            FightingPlayers.AskedOf(integration?.Lives);
            OurDestructibles.ClaimedWith(integration?.Destructibles);

            if (integration != null && _spawnOwnership == null)
            {
                Logger.LogWarning("The session layer would not carry our runtime spawns; the boss's minions will "
                    + "appear on the host only.");
            }
        }

        /// <summary>
        /// Keep the session-wide arena-level agreement composed on whatever integration is live, and let the host
        /// tell peers that joined since it declared. Without a session there is nobody to agree with and the local
        /// declaration is the whole truth.
        /// </summary>
        private void MaintainArenaLevelFlow(IFalseGodsIntegration? integration)
        {
            if (integration is null || !integration.Session.IsActive)
            {
                if (_levelFlow != null)
                {
                    TearDownArenaLevelFlow();
                }

                return;
            }

            if (_levelFlow != null && !ReferenceEquals(_levelFlowIntegration, integration))
            {
                TearDownArenaLevelFlow(); // a different integration registered; recompose on its channel
            }

            if (_levelFlow is null)
            {
                _levelFlow = new ArenaLevelFlow(integration.Channel, integration.Session, integration.Roster)
                {
                    OnDeclared = ApplyArenaLevelDeclaration,
                    OnRequested = HandleArenaLevelRequest,
                };
                _levelFlowIntegration = integration;
                ReconcileArenaLevelOnJoiningSession(integration);
            }

            // The declaration lasts one visit: the generation hooks withdraw it locally the moment the players
            // generate a different level. On the host that local truth is the session's, so the withdrawal is
            // published — a peer that happened not to be generating must not be left holding it.
            var declaration = _levelFlow.Declaration;
            if (declaration != null
                && declaration.IsBossArena
                && !_hijack.IsArenaModeOn
                && integration.Session.Role == RuntimeContracts.Multiplayer.SessionRole.Host)
            {
                _levelFlow.Declare(declaration.Level, isBossArena: false);
            }

            _levelFlow.Tick();
        }

        /// <summary>
        /// A declaration made while alone is not automatically the session's. Joining as the <b>host</b> makes
        /// this peer's standing declaration the session's, so it is announced; joining as a <b>client</b> hands
        /// the question to the host, so a local declaration is dropped until the host makes one. Without this, a
        /// peer that used the dev key in single-player would keep hijacking a level the rest of the session
        /// generates normally, and the players would stand in different rooms.
        /// </summary>
        private void ReconcileArenaLevelOnJoiningSession(IFalseGodsIntegration integration)
        {
            if (integration.Session.Role == RuntimeContracts.Multiplayer.SessionRole.Host)
            {
                if (_hijack.IsArenaModeOn)
                {
                    _levelFlow?.Declare(_hijack.ArenaLevel);
                }

                return;
            }

            if (_hijack.IsArenaModeOn)
            {
                Logger.LogMessage("Joined a session: the host decides which level is the boss arena, so the local "
                    + "declaration is dropped until it says so.");
                _hijack.LeaveArenaMode();
            }
        }

        private void TearDownArenaLevelFlow()
        {
            _levelFlow?.Dispose();
            _levelFlow = null;
            _levelFlowIntegration = null;
        }

        /// <summary>
        /// The dev entry to the boss arena. In single-player, and on a host, this peer decides and goes; on a
        /// client it is a request, because the host owns level transitions (invariant 1) and going alone would
        /// only make the session's own transition logic drag everyone back through an undeclared level.
        /// </summary>
        private void GoToArenaLevel()
        {
            var flow = _levelFlow;
            if (flow is null)
            {
                _hijack.LoadHijackedArena(); // no session: declare locally and go
                return;
            }

            if (FalseGodsIntegrations.Current?.Session.Role == RuntimeContracts.Multiplayer.SessionRole.Host)
            {
                // Declare first so every client knows what the level is BEFORE the transition messages that make
                // them generate it; both travel the same reliable-ordered channel, so the order is kept.
                flow.Declare(_hijack.ArenaLevel);
                _hijack.LoadHijackedArena();
                return;
            }

            flow.Request(_hijack.ArenaLevel);
            Logger.LogMessage("Asked the host to take the session to the boss arena; the host leads the transition.");
        }

        /// <summary>Apply a host declaration locally: from now on that level builds our arena instead of the
        /// game's own content, on this peer, however the generation was asked for.</summary>
        private void ApplyArenaLevelDeclaration(Protocol.Wire.ArenaLevelDeclared declaration)
        {
            if (!declaration.IsBossArena)
            {
                _hijack.LeaveArenaMode();
                return;
            }

            if (!_hijack.DeclareArenaLevel(declaration.Level))
            {
                Logger.LogWarning($"The host declared {declaration.Level} a boss arena, but this build does not "
                    + "recognise that level; it will generate normally here.");
            }
        }

        /// <summary>
        /// Host: a peer asked for the boss arena. The host decides and leads — it declares the level to everyone
        /// and makes the transition, exactly as if it had pressed the key itself. The request names the level it
        /// means, but what gets declared is <b>this build's own</b> arena: a request is a trigger, not a way for
        /// a peer to choose which level the host hijacks.
        /// </summary>
        private void HandleArenaLevelRequest(Protocol.Wire.ArenaLevelRequested request)
        {
            var ours = _hijack.ArenaLevel;
            if (request.Level != ours)
            {
                Logger.LogWarning($"A session peer asked for a boss arena at {request.Level}, but this build's "
                    + $"arena is {ours}; ignoring the request.");
                return;
            }

            Logger.LogMessage("A session peer asked for the boss arena; declaring it and leading the transition.");
            _levelFlow?.Declare(ours);
            _hijack.LoadHijackedArena();
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
                _crateFlow?.BroadcastThrown(from, foot, ThrowSeconds, ThrowApex);
                _log.Log($"[crate] crate thrown from ({from.X:0.0}, {from.Y:0.0}, {from.Z:0.0}); "
                    + $"{_crates.InFlight} in the air. Shoot it for loot, or let it land for none.");
            }
        }

        /// <summary>
        /// Bring-up delivery: put one crate on the pile beside wherever the boss is standing, as a carrier will
        /// once there are carriers. Tapping the key repeatedly stocks the boss — the ammunition a volley draws
        /// from, and the only crates it can draw from.
        /// </summary>
        private void DeliverOneCrateToTheBoss()
        {
            if (!_boss.TryGetSupplyPile(out var pile, out var at))
            {
                _log.Log("[crate] nothing to deliver to: raise the boss in a room that authored delivery piles.");
                return;
            }

            // Dropped above the pile so it falls onto whatever is already stacked there, exactly as a produced
            // crate falls onto its source.
            var above = new ArenaWorldPoint(at.X, at.Y + DropHeight, at.Z);
            if (_crates.Drop(above, pile))
            {
                _crateFlow?.BroadcastDropped(above, pile);
                _log.Log($"[crate] delivered one to {pile} at ({at.X:0.0}, {at.Y:0.0}, {at.Z:0.0}); "
                    + $"{_crates.RestingOn(pile)} on that pile. Tap again to stock the boss.");
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

            // A volley is fired off the boss's own pile and nothing else — crates still standing at the production
            // points are not its ammunition until somebody brings them.
            if (!_boss.TryGetSupplyPile(out var pile, out _))
            {
                _log.Log("[crate] no boss pile to fire from: raise the boss in a room that authored delivery piles.");
                return;
            }

            LaunchCrateVolley(pile, VolleyCount);
        }

        /// <summary>
        /// Throw <paramref name="count"/> crates off <paramref name="pile"/> at the players: aim where they are and
        /// where they are going, scatter the crates around those points, and tell every client the few numbers it
        /// takes to build the same volley.
        /// </summary>
        /// <remarks>
        /// Shared by the boss, which does this on its own clock as ammunition reaches it, and by the bring-up key,
        /// which is only a way to make it happen on demand. The aiming lives here because this is where the
        /// player's motion is tracked; the boss decides only when and how many.
        /// </remarks>
        private void LaunchCrateVolley(CratePileId pile, int count)
        {
            var camera = Camera.main;
            if (camera == null)
            {
                return;
            }

            var seed = _nextVolleySeed++;

            // The telegraph length is part of the volley's seeded shape, so it is rolled here (where the seed is
            // chosen) and both the hover and the lead below are computed from the same span.
            var hold = SeededRandom.Range(seed, VolleyHoldSalt, VolleyHoldMin, VolleyHoldMax);

            // A crate's whole airtime is fixed by the shape, not the distance, so the lead is a single step: aim
            // where each player will be after the crates rise, hover, and fly. Only the ground position is led;
            // each aim keeps the height its own player is standing at.
            var airtime = (VolleyLiftSeconds + hold + VolleyFlightSeconds) * VolleyLeadFraction;

            var aims = BuildVolleyAims(airtime);
            if (aims.Count == 0)
            {
                _log.Log("[crate] nobody to throw at — every player is down or out of the level.");
                return;
            }

            var shape = new CrateVolleyShape(
                seed, count, VolleySpreadMin, VolleySpreadMax,
                VolleyLiftHeight, VolleyLiftSeconds, hold, VolleyFlightSeconds, VolleyApex, VolleyLeadShare,
                VolleyFireInterval);

            var launched = _crates.LaunchVolley(pile, aims, shape);
            if (launched > 0)
            {
                // The shape is the volley: every client computes the same crates from these inputs, so what the
                // players dodge is the same volley rather than a description of one.
                _crateFlow?.BroadcastVolley(pile, aims, shape);
                _log.Log($"[crate] volley of {launched} lifted off {pile}; {_crates.RestingOn(pile)} left there. "
                    + $"Hold {hold:0.00}s, then {VolleyCratesPerSecond:0.#}/s for {launched * VolleyFireInterval:0.00}s; "
                    + $"spread over {aims.Count} player(s), led {airtime:0.00}s. Shoot them for loot.");
            }
            else
            {
                _log.Log($"[crate] {pile} is empty - the boss has no ammunition. Nothing has been carried to it "
                    + "yet.");
            }
        }

        /// <summary>
        /// One aim per player worth throwing at: where they stand, and where they will be when the crates arrive.
        /// </summary>
        /// <remarks>
        /// <para><b>Everyone, not the local player.</b> Reading the game's player singleton aims every volley at
        /// whoever is hosting; measured on two peers, every crate landed on the host and the client walked through
        /// the barrage untouched.</para>
        /// <para><b>Why the speeds are tracked here.</b> Only the player on this machine has a movement controller
        /// to ask. Everyone else is a figure the session keeps up to date, so their speed comes from watching their
        /// position move — one smoothed tracker per player, for the same reason the local one is smoothed: a
        /// jitter must not fling the lead across the arena.</para>
        /// </remarks>
        private List<CrateVolleyAim> BuildVolleyAims(float airtime)
        {
            _playerMotion.ReadPlayersToThrowAt(_playersToThrowAt);

            _volleyAims.Clear();
            for (var i = 0; i < _playersToThrowAt.Count; i++)
            {
                var player = _playersToThrowAt[i];
                if (!_playerSpeeds.TryGetValue(player.Index, out var tracked))
                {
                    continue; // seen for the first time this frame; it is aimed at from the next volley on
                }

                // Each player's OWN height: a room with terraces has them standing at different levels, and one
                // height for everybody threw short of whoever had climbed.
                var lead = LeadAim.Predict(player.Position, tracked.SmoothedVelocity, airtime);
                _volleyAims.Add(new CrateVolleyAim(
                    new ArenaWorldPoint(player.Position.X, player.Height, player.Position.Z),
                    new ArenaWorldPoint(lead.X, player.Height, lead.Z)));
            }

            return _volleyAims;
        }

        /// <summary>
        /// Keep a smoothed speed for every player in the room, so a volley can lead all of them and not just the
        /// one on this machine. Players who leave are forgotten.
        /// </summary>
        private void TrackPlayerMotion(float deltaSeconds)
        {
            if (deltaSeconds <= 0f)
            {
                return;
            }

            _playerMotion.ReadPlayersToThrowAt(_playersToThrowAt);
            _playersSeen.Clear();

            for (var i = 0; i < _playersToThrowAt.Count; i++)
            {
                var player = _playersToThrowAt[i];
                _playersSeen.Add(player.Index);

                if (!_playerSpeeds.TryGetValue(player.Index, out var tracked))
                {
                    tracked = new TrackedPlayer(VolleyLeadSmoothingSeconds);
                    _playerSpeeds[player.Index] = tracked;
                }

                tracked.Observe(player, deltaSeconds);
            }

            if (_playerSpeeds.Count == _playersSeen.Count)
            {
                return;
            }

            _forgottenPlayers.Clear();
            foreach (var known in _playerSpeeds.Keys)
            {
                if (!_playersSeen.Contains(known))
                {
                    _forgottenPlayers.Add(known);
                }
            }

            for (var i = 0; i < _forgottenPlayers.Count; i++)
            {
                _playerSpeeds.Remove(_forgottenPlayers[i]);
            }
        }

        /// <summary>
        /// One player's smoothed ground speed. The player on this machine reports its own velocity, which is the
        /// reading the controller itself keeps; everybody else's is worked out from how far they moved, because a
        /// figure the session drives has no controller to ask.
        /// </summary>
        private sealed class TrackedPlayer
        {
            private readonly TargetMotionTracker _tracker;
            private SimVector2 _lastPosition;
            private bool _hasLastPosition;

            public TrackedPlayer(float smoothingSeconds)
            {
                _tracker = new TargetMotionTracker(smoothingSeconds);
            }

            public SimVector2 SmoothedVelocity => _tracker.SmoothedVelocity;

            public void Observe(PlayerAim player, float deltaSeconds)
            {
                SimVector2 velocity;
                if (player.VelocityKnown)
                {
                    velocity = player.Velocity;
                }
                else if (_hasLastPosition)
                {
                    velocity = new SimVector2(
                        (player.Position.X - _lastPosition.X) / deltaSeconds,
                        (player.Position.Z - _lastPosition.Z) / deltaSeconds);
                }
                else
                {
                    velocity = SimVector2.Zero;
                }

                _lastPosition = player.Position;
                _hasLastPosition = true;
                _tracker.Observe(velocity, deltaSeconds);
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

            // Arena mode lives in a static the generation hooks can reach, so it outlives this component unless
            // it is released here: a plugin that goes away must not leave the game's next cave load hijacked.
            _hijack?.LeaveArenaMode();

            // Never leave a boss behind if the plugin unloads while one is up.
            if (_boss != null && _boss.IsActiveEncounter)
            {
                _boss.Drop();
            }

            TearDownClientComposition();
            TearDownArenaLevelFlow();
            _spawnOwnership?.Dispose();
            _spawnOwnership = null;
            _spawnOwnershipIntegration = null;
            _crateFlow?.Dispose();
            _crateFlow = null;
            _crateFlowIntegration = null;
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
                // Same hand-off the local controller gets: when a hijacked level left our arena standing on this
                // peer, the client fights in that one instead of realizing a second copy of it.
                _client = new ClientBossController(_log, integration) { LevelArena = _levelArena };
                _clientIntegration = integration;
            }

            if (KeyPressed(_raiseKey.Value))
            {
                Logger.LogMessage("Multiplayer client: the host drives the boss; the raise key is inert here.");
            }

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
