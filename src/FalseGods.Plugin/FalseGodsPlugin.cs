using System;
using System.Collections.Generic;
using System.IO;
using BepInEx;
using BepInEx.Configuration;
using FalseGods.Application.Arena;
using FalseGods.Application.Combat;
using FalseGods.Application.Presentation;
using FalseGods.Application.Replication;
using FalseGods.Core.Bosses.Combat;
using FalseGods.Core.Simulation;
using FalseGods.Integration.Sulfur.Arena;
using FalseGods.Integration.Sulfur.Combat;
using FalseGods.Integration.Sulfur.Presentation;
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
    /// stream, and it decides nothing.</item>
    /// </list>
    /// When the adapter's registration token is disposed, the change event fires and the next frame falls back to
    /// the single-player composition (PoC B0).
    /// </para>
    ///
    /// <para>
    /// <see cref="PluginGuid"/> is stable because the ST adapter declares a <c>[BepInDependency]</c> on it. The
    /// boss is raised by its arena being built and started by a player walking into the room — no key raises it,
    /// and damage is the real weapon path (the game's projectile/melee systems hitting the boss's collision body).
    /// The arena identity, hash, and origin all come from the real load flow — a raise without valid arena content
    /// fails closed. <b>One development key is left</b>: the one that takes the session to the arena at all, which
    /// has no replacement yet because nothing in ordinary play leads there.
    /// </para>
    /// </remarks>
    [BepInPlugin(PluginGuid, PluginName, PluginVersion)]
    public sealed class FalseGodsPlugin : BaseUnityPlugin
    {
        public const string PluginGuid = "ryuka_labs.falsegods";
        public const string PluginName = "False Gods";
        public const string PluginVersion = "0.4.0";

        private const int TestBossDefinition = 1;

        // Volley shape: lift a handful of crates off the pile, hold them a beat to telegraph, then scatter them
        // around where the players will be. Readable rise, a spread that surrounds without being unfair.
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
        private ConfigEntry<float> _maxClientHitDamage = null!;
        private ConfigEntry<Key> _hijackKey = null!;

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
        private IBossVoicePort _voice = null!;
        private IArenaAtmospherePort _atmosphere = null!;

        // Which arena-building generation run this peer's automatic raise belongs to. One arena, one automatic
        // raise: dropping the boss by hand must not be undone by the next frame, and the next visit to the arena
        // is a new run and so gets a new one.
        private int _raisedForRun;
        private ClientBossController? _client;
        private IFalseGodsIntegration? _clientIntegration; // the integration _client was composed on

        private int _nextEncounter = 1;
        private EncounterId _currentEncounter;

        private void Awake()
        {
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

            // The arena is built dark. The players walk into a room they cannot see across, and it opens around
            // them when the fight starts — so what the level applies is the opening depth, not the fight's.
            LevelGenerationHijack.Fog = new ArenaFogRange(ArenaDepth.OpeningStart, ArenaDepth.OpeningEnd);

            _hijack = new SulfurArenaHijackPort(_log);

            // Warmed when an encounter starts rather than here: taking the vanilla boss's roar means reading the
            // game's creature database, which it loads asynchronously and has not built yet at plugin load. The
            // room's own atmosphere is fetched the same way and for the same reason — its music comes off the
            // vanilla boss's room.
            _voice = new SulfurBossVoice(transform, _log);
            _atmosphere = new SulfurArenaAtmosphere(this, _log);

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
                _voice,
                _atmosphere,
                _playerMotion,
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

            Logger.LogMessage($"{PluginName} {PluginVersion} loaded. The boss stands up with its arena and is "
                + $"started by walking into the room; take the session there with {_hijackKey.Value}. "
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


            // Track EVERY player's velocity each frame so a volley can lead all of them by the average rather than
            // the instant — and so a barrage threatens the whole room, not whoever happens to be hosting.
            TrackPlayerMotion(Time.deltaTime);

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
            _atmosphere?.Dispose();
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
            RaiseWithTheArena(integration, role);

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

        /// <summary>
        /// The boss belongs to the room, so it is raised with the room: the moment a hijacked level finishes
        /// building the arena, the encounter starts and the boss stands in it, waiting for somebody to walk in.
        /// </summary>
        /// <remarks>
        /// <para><b>When the level is finished, not when the arena appears.</b> The arena is instantiated a third
        /// of the way through generation — before navigation is scanned and before the player is placed — so a
        /// raise triggered by its existence would gate a roster with nobody in it and, on a host, announce the
        /// arena to the session before its own level was habitable. The end of the generation run is the moment
        /// the players are really in the room.</para>
        /// <para><b>Once per arena.</b> Dropping the boss by hand has to stay dropped, so the automatic raise is
        /// tied to the run that built the arena rather than to the absence of a boss; leaving and coming back is a
        /// new run and gets a new raise, which is exactly what a fresh visit should be.</para>
        /// <para><b>Not on a client.</b> This is only reached from the local composition; a client's encounter
        /// arrives from the host, as everything else about the fight does.</para>
        /// </remarks>
        private void RaiseWithTheArena(IFalseGodsIntegration? integration, CompositionRole role)
        {
            var run = LevelGenerationHijack.ArenaRunsFinished;
            if (run == 0 || run == _raisedForRun || !_levelArena.IsLive)
            {
                return;
            }

            _raisedForRun = run;

            // A new arena replaced the one the previous fight stood in — the level destroyed it on its way out —
            // so that fight is over whether or not anybody said so.
            if (_boss.IsActiveEncounter)
            {
                _boss.Drop();
            }

            Logger.LogMessage("The boss arena is standing; raising the boss in it. It waits, untouchable, until a "
                + "player walks into the room.");
            Raise(integration, role);
        }

        private void Raise(IFalseGodsIntegration? integration, CompositionRole role)
        {
            _currentEncounter = new EncounterId(_nextEncounter++);
            _boss.Raise(_currentEncounter, role == CompositionRole.Host ? integration : null);
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
                _client = new ClientBossController(
                    _log,
                    this,
                    integration,
                    _atmosphere,
                    // The host raises as soon as its own level is up, which is routinely before this peer has
                    // finished generating the same level. While that is true, the announcement is waited on rather
                    // than answered by loading a second copy of an arena the level is about to hand us.
                    () => _hijack.IsArenaModeOn && !_levelArena.IsLive)
                {
                    LevelArena = _levelArena,
                };
                _clientIntegration = integration;
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
