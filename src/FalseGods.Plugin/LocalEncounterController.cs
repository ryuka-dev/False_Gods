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
using FalseGods.Core.Bosses.Combat;
using FalseGods.Core.Encounters;
using FalseGods.Core.Simulation;
using FalseGods.Integration.Sulfur.Arena;
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

        /// <summary>
        /// How many minions a summoning station calls up. First-pass number, tuned in game.
        private const int MinionsPerSummon = 4;

        /// <summary>
        /// The first boss's itinerary: it holds its home anchor, drops to the second one to summon, returns, does
        /// it once more, and comes home to die. Read against the arena's authored anchors by index — anchor 0 is the
        /// first child of the room's anchor group, anchor 1 the second.
        /// </summary>
        /// <remarks>
        /// Boss design, so it lives with the boss and not in the room, and an authored order rather than the
        /// vanilla cave boss's random pool — a fight whose shape is the point should be the same fight twice.
        /// Still code rather than content: there is no boss-content pipeline yet, and one boss does not justify
        /// inventing one (Docs/DefinitionOfDone.md §3). The thresholds are first-pass numbers, tuned in game.
        /// </remarks>
        private static readonly IReadOnlyList<BossStation> Itinerary = new[]
        {
            new BossStation(anchorIndex: 0, enterAtHealthFraction: 1.00f),
            new BossStation(anchorIndex: 1, enterAtHealthFraction: 0.80f, summonCount: MinionsPerSummon),
            new BossStation(anchorIndex: 0, enterAtHealthFraction: 0.60f),
            new BossStation(anchorIndex: 1, enterAtHealthFraction: 0.40f, summonCount: MinionsPerSummon),
            new BossStation(anchorIndex: 0, enterAtHealthFraction: 0.20f),
        };

        private readonly ILogger _logger;
        private readonly ISimulationClock _clock;
        private readonly IEncounterParticipantQuery _participants;
        private readonly IDamagePort _damagePort;
        private readonly SulfurLocalPlayer _localPlayer;
        private readonly string _contentDirectory;
        private readonly float _maxClientHitDamage;
        private readonly IMinionSpawnPort _minionSpawns;

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

        // False while the encounter is fought in an arena the LEVEL owns (Strategy A): the arena outlives the
        // fight there, so tearing the encounter down must not take the level's start area with it.
        private bool _ownsArena;
        private ArenaManifest? _manifest;
        private LoadedArena? _arenaContent; // the room's authored content, for as long as the fight uses it

        private EncounterId _encounter;
        private WorldPosition _originWire;
        private BossActivity _lastReportedActivity = BossActivity.Dead; // forces the first real activity to report
        private int _lastReportedPending = -1;

        public LocalEncounterController(
            ILogger logger,
            IMinionSpawnPort minions,
            float maxClientHitDamage = DefaultMaxClientHitDamage)
        {
            _logger = logger;
            _minionSpawns = minions ?? throw new ArgumentNullException(nameof(minions));
            _maxClientHitDamage = maxClientHitDamage;
            // The single-player Core-port bundle. Clock and roster are stateless and shared across raises; the RNG is
            // reseeded per raise so successive fights vary.
            _clock = new SulfurSimulationClock();
            _participants = new SulfurParticipantQuery();
            _damagePort = new SulfurDamagePort(logger);
            _localPlayer = new SulfurLocalPlayer();
            _contentDirectory = Path.GetDirectoryName(typeof(LocalEncounterController).Assembly.Location) ?? ".";
        }

        /// <summary>
        /// The arena a hijacked level load left standing, when there is one. Set by the Composition Root; while it
        /// reports a live arena, a raise fights in <i>that</i> arena instead of loading and placing its own.
        /// </summary>
        public HijackedArenaContent? LevelArena { get; set; }

        public bool IsUp => _presentation != null;

        /// <summary>Whether the controller owns a live encounter attempt — fighting, or still gating.</summary>
        public bool IsActiveEncounter => IsUp || _hostGate != null;

        /// <summary>Whether a host replication driver is currently attached.</summary>
        public bool HasReplication => _replication != null;

        /// <summary>This encounter's validated manifest (for a mid-fight host attach), or null before the gate.</summary>
        public ArenaManifest? CurrentManifest => _manifest;

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

            _encounter = encounter;
            _hostIntegration = hostIntegration;

            // ── The arena may already be here. A hijacked level load realized our arena AS the level, through
            // this same load flow — same content hash, same parity check, same borrowed materials — so the
            // encounter fights in that one rather than loading a second copy of it on top of itself. The arena
            // belongs to the level, not to this encounter: dropping the boss leaves the cave standing.
            var levelArena = LevelArena;
            if (levelArena != null && levelArena.IsLive)
            {
                var standing = levelArena.Realized;
                if (standing?.Manifest is null || standing.Arena is null)
                {
                    _logger?.LogWarning("The level's arena is standing but reported no load result; cannot raise in it.");
                    return false;
                }

                _ownsArena = false;
                _realization = levelArena.Realization;
                _flow = null;
                return RaiseInArena(standing, HijackedArenaContent.Origin);
            }

            var camera = Camera.main;
            if (camera == null)
            {
                _logger?.LogWarning("Cannot start the encounter: no main camera. Load a level and stand in it first.");
                return false;
            }

            _ownsArena = true;

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
                new AstarNavigationPort(() => realizationForNav.CurrentRoot, _logger),
                new SulfurVanillaAssetProvider(() => realizationForNav.CurrentRoot, _logger));

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

            return RaiseInArena(realized, origin);
        }

        /// <summary>
        /// Everything from a validated, standing arena onwards: the ready gate, then the boss. Shared by both
        /// ways an arena can get here — loaded and placed by this raise, or already standing because a hijacked
        /// level load made it the level. There is deliberately one gate and one start path, whichever it was.
        /// </summary>
        private bool RaiseInArena(ArenaRealizeResult realized, ArenaWorldPoint origin)
        {
            _manifest = realized.Manifest;
            _originWire = new WorldPosition(origin.X, origin.Y, origin.Z);

            if (_hostIntegration != null)
            {
                // ── MULTIPLAYER GATE: EnterArena to every client; the boss starts from Tick when the whole
                // roster has proven matching content (§5.3 steps 1/4/5), or the attempt aborts (§5.3.1).
                _hostSender = new ReplicationSender(_hostIntegration.Channel, _hostIntegration.Session);
                _hostGate = new HostEncounterGate(
                    _hostIntegration.Channel,
                    _hostIntegration.Session,
                    _hostIntegration.Roster,
                    _hostSender,
                    _encounter,
                    realized.Manifest!,
                    _originWire,
                    GateTimeoutSeconds);
                _pendingStart = realized;
                _hostGate.Open();
                _logger?.Log($"Encounter {_encounter}: arena realized and EnterArena broadcast; waiting for every "
                    + $"session peer's ArenaReady (timeout {GateTimeoutSeconds:0}s).");
                return true;
            }

            // ── SINGLE-PLAYER GATE: the real gate over the one-member local roster — no second code path.
            var gate = new EncounterReadyGate(realized.Manifest!, LocalRoster.Instance);
            var status = gate.SubmitReady(LocalRoster.LocalPeer, realized.Manifest!);
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
                            + $"(outstanding: [{_hostGate.DescribeOutstanding()}]). Clients were told; releasing the "
                            + "local arena.");
                        CleanupGate();
                        ReleaseArena();
                        break;
                }

                return;
            }

            if (_boss is null || _presenter is null || _presentation is null)
            {
                return;
            }

            _boss.Advance();
            ReportActivityChange();
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
            _minionSpawns.DespawnAll();
            _arenaContent = null;
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
            var ownedArena = _ownsArena;
            ReleaseArena();
            _replication = null; // the driver is per-encounter; the next raise gets a fresh one
            _hostIntegration = null;
            _logger?.Log(ownedArena
                ? "Encounter torn down; arena navigation restored and nothing remains."
                : "Encounter torn down; the level's arena is left standing, as it belongs to the level.");
        }

        /// <summary>Everything after the gate: encounter domain, boss on the arena's authoritative floor, and —
        /// when hosting — the replication driver, attached before the boss spawns so the spawn events replicate.</summary>
        private void StartBoss(ArenaRealizeResult realized)
        {
            var manifest = realized.Manifest!;
            var arena = realized.Arena!;

            _arena = new ArenaSimulation();
            _coordinator = new EncounterCoordinator(_encounter, _arena, new MechanismGroupId(PhaseTwoGroup));

            // The anchors the room authored, in authored order; a station refers to one by index. Without them the
            // boss has no itinerary and simply stands where it spawns.
            var anchors = ToBossAnchors(arena.BossAnchors);
            var definition = new BossDefinition(
                maxHealth: 50000,
                phaseTwoHealthFraction: 0.5f,
                moveSpeed: 0f, // anchored: the itinerary decides where it stands, so it must not also walk
                idleSeconds: 2.0f,
                telegraphSeconds: 1.5f,
                commitSeconds: 0.4f,
                recoverSeconds: 2.0f,
                weakPointDamageMultiplier: 3,
                attackDamage: 20,
                aimedHitRadius: 2.0f,
                areaHitRadius: 5.0f,
                stations: anchors.Count > 0 ? Itinerary : null);

            _boss = new BossSimulation(
                new BossInstanceId(1),
                definition,
                _clock,
                new SeededAuthoritativeRandom(Environment.TickCount),
                _participants,
                anchors);

            // Spawn before building the presentation, so the boss is asked where it stands rather than told: an
            // itinerary puts it at its first station, which need not be the authored enemy spawn or even on the
            // floor. The spawn event waits in the simulation's buffer and is drained by the Present() below, once
            // presentation and replication are both attached.
            _arenaContent = arena;
            var bossSpawn = arena.BossSpawn;
            _boss.Spawn(new SimVector2(bossSpawn.X, bossSpawn.Z), bossSpawn.Y);

            // The authored enemy spawn's height stays the arena's ground, where telegraphs and impacts are drawn.
            _presentation = new BossPresentation(
                _logger,
                new Vector3(_boss.Position.X, _boss.PositionHeight, _boss.Position.Z),
                bossSpawn.Y);

            // Size is authored in the room, not chosen by the player: a room that authored none leaves the
            // presentation's own default standing.
            if (arena.BossSize > 0f)
            {
                _presentation.SpriteScale = arena.BossSize;
            }

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
            Present();
            _presentation.Render(0f);

            // The node count is this encounter's own contribution to navigation, which is zero — and says nothing
            // about the graph — when the level built the navigation itself. Reporting it as such keeps a healthy
            // Strategy A raise from reading like an arena with no navigation at all.
            var navigation = _ownsArena
                ? $"{arena.NavWalkableNodes} walkable nav node(s) applied"
                : "navigation built by the level itself";

            _logger?.Log($"Encounter {_encounter} started: arena '{manifest.ArenaId}' at "
                + $"({_originWire.X:0.0}, {_originWire.Y:0.0}, {_originWire.Z:0.0}), {navigation}, "
                + $"boss at ({bossSpawn.X:0.0}, {bossSpawn.Y:0.0}, {bossSpawn.Z:0.0}) on "
                + "the arena floor. Shoot or melee it; weak-window hits are amplified.");

            ReportAuthoredBossContent(arena);
        }

        /// <summary>Report every activity transition while a boss is up, so a relocation that does not happen is
        /// as visible in the log as one that does. Diagnostics only.</summary>
        private void ReportActivityChange()
        {
            if (_boss is null)
            {
                return;
            }

            // A station becoming pending is the other half of the story: it says the threshold WAS noticed, so a
            // boss that then fails to move is a different bug from one that never noticed.
            if (_boss.PendingStationIndex != _lastReportedPending)
            {
                _lastReportedPending = _boss.PendingStationIndex;
                if (_lastReportedPending >= 0)
                {
                    _logger?.Log($"[boss-activity] station {_lastReportedPending} reached at {_boss.Health}/"
                        + $"{_boss.MaxHealth} hp; leaving now (was {_boss.Activity}).");
                }
            }

            if (_boss.Activity == _lastReportedActivity)
            {
                return;
            }

            _lastReportedActivity = _boss.Activity;
            var pending = _boss.PendingStationIndex >= 0
                ? $", waiting to move to station {_boss.PendingStationIndex}"
                : string.Empty;
            _logger?.Log($"[boss-activity] {_boss.Activity} at station {_boss.StationIndex} "
                + $"({_boss.Health}/{_boss.MaxHealth} hp){pending}");
        }

        /// <summary>
        /// Put the boss's summons in the room. The places come from the arena, not from the boss: the boss asks
        /// for a number, the room decides where they can stand. Asking for more than the room authored wraps
        /// around its spawn points rather than dropping the extras — a room with two places can still be asked
        /// for four minions.
        /// </summary>
        private void Summon(SummonRequest request)
        {
            var places = _arenaContent?.MinionSpawns;
            if (places is null || places.Count == 0)
            {
                _logger?.LogWarning($"[minion] station {request.StationIndex} summons {request.Count}, but the "
                    + "room authored no minion spawn points; nothing summoned.");
                return;
            }

            var at = new ArenaWorldPoint[request.Count];
            for (var i = 0; i < request.Count; i++)
            {
                at[i] = places[i % places.Count];
            }

            _logger?.Log($"[minion] station {request.StationIndex} summons {request.Count} at "
                + $"{places.Count} authored place(s).");
            _minionSpawns.Summon(at);
        }

        /// <summary>Turn the room's authored anchor world positions into the simulation's ground-plus-height form:
        /// the fight is reasoned about on the ground plane, and the height rides along as placement.</summary>
        private static IReadOnlyList<BossAnchor> ToBossAnchors(IReadOnlyList<ArenaWorldPoint> authored)
        {
            var anchors = new BossAnchor[authored.Count];
            for (var i = 0; i < authored.Count; i++)
            {
                anchors[i] = new BossAnchor(new SimVector2(authored[i].X, authored[i].Z), authored[i].Y);
            }

            return anchors;
        }

        /// <summary>
        /// Report the boss content the room authored, so it can be checked against the prefab without guessing at
        /// it from how the boss looks. Diagnostics only — nothing here decides anything.
        /// </summary>
        private void ReportAuthoredBossContent(LoadedArena arena)
        {
            var size = arena.BossSize > 0f
                ? $"size {arena.BossSize:0.##} (authored)"
                : "size: none authored, presentation default in use";

            if (arena.BossAnchors.Count == 0)
            {
                _logger?.Log($"[boss-content] {size}; no authored anchors — the boss has no room-authored places "
                    + "to stand.");
                return;
            }

            _logger?.Log($"[boss-content] {size}; {arena.BossAnchors.Count} authored anchor(s): "
                + $"{Describe(arena.BossAnchors)}; {arena.MinionSpawns.Count} minion spawn point(s)");

            // The supply line the carriers work: where destructibles are produced, and where they are delivered.
            // Reported apart from the anchors because a room can author a boss without authoring a supply line.
            _logger?.Log($"[supply-content] {arena.CrateSources.Count} crate source(s): "
                + $"{Describe(arena.CrateSources)}; {arena.CratePiles.Count} delivery pile(s): "
                + $"{Describe(arena.CratePiles)}");

            if (arena.CratePiles.Count > 0 && arena.CratePiles.Count < arena.BossAnchors.Count)
            {
                _logger?.LogWarning($"[supply-content] the room authored {arena.CratePiles.Count} pile(s) for "
                    + $"{arena.BossAnchors.Count} anchor(s); anchors past the last pile are supplied by it.");
            }
        }

        /// <summary>One line of authored world points, numbered as the code indexes them. Diagnostics only.</summary>
        private static string Describe(IReadOnlyList<ArenaWorldPoint> points)
        {
            if (points.Count == 0)
            {
                return "none";
            }

            var text = new System.Text.StringBuilder();
            for (var i = 0; i < points.Count; i++)
            {
                text.Append(i == 0 ? string.Empty : ", ")
                    .Append($"#{i} ({points[i].X:0.0}, {points[i].Y:0.0}, {points[i].Z:0.0})");
            }

            return text.ToString();
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

            // Say so out loud: a refused hit otherwise looks exactly like a hit that never arrived, which makes
            // "is it invulnerable?" unanswerable from the log.
            if (_boss.IsRelocating)
            {
                _logger?.Log($"[weapon-damage] refused: the boss is {_boss.Activity} (invulnerable while it moves); "
                    + $"health stays {_boss.Health}.");
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

            var damageRequests = _boss.DrainDamageRequests();
            if (damageRequests.Count > 0)
            {
                ApplyOrDeferPlayerDamage(damageRequests);
            }

            var summons = _boss.DrainSummonRequests();
            for (var i = 0; i < summons.Count; i++)
            {
                Summon(summons[i]);
            }

            var arenaEvents = _arena.DrainEvents();
            for (var i = 0; i < arenaEvents.Count; i++)
            {
                _arenaPresentation?.Handle(ArenaPresentationMapping.ToEvent(arenaEvents[i]));
            }

            _replication?.Publish(
                _boss, bossEvents, _arena, arenaEvents, _coordinator.Phase, new SimulationTick(_clock.Tick));
        }

        /// <summary>
        /// Apply the boss's resolved player hits. In single-player the local player is the only participant, so each
        /// request goes straight to the game's damage model through the <see cref="IDamagePort"/>. As a host, a hit
        /// on the host's own player applies locally the same way, while a hit on a remote client's player is sent to
        /// that client to apply on its own player (§5.6, ST's per-player health ownership) — the host never damages
        /// its local puppet of a remote player. An index that maps to neither is dropped.
        /// </summary>
        private void ApplyOrDeferPlayerDamage(IReadOnlyList<DamageRequest> requests)
        {
            if (_hostIntegration == null)
            {
                for (var i = 0; i < requests.Count; i++)
                {
                    _damagePort.ApplyDamage(requests[i].Target, requests[i].Amount);
                }

                return;
            }

            _localPlayer.TryGetLocalParticipantIndex(out var localIndex);
            for (var i = 0; i < requests.Count; i++)
            {
                var request = requests[i];
                if (_hostIntegration.Players.TryGetRemotePeer(request.Target.Value, out var peer))
                {
                    _hostSender?.SendBossHitPlayer(new BossHitPlayer(_encounter, request.Amount), peer);
                }
                else if (request.Target.Value == localIndex)
                {
                    _damagePort.ApplyDamage(request.Target, request.Amount);
                }
                else
                {
                    _logger?.Log($"[boss-damage] hit on participant {request.Target.Value} maps to no session peer "
                        + "or the local player; dropped.");
                }
            }
        }

        /// <summary>
        /// Give up this encounter's claim on the arena: torn down when the encounter loaded it, merely let go of
        /// when the arena belongs to the level (Strategy A) and must outlive the fight. Idempotent at any stage.
        /// </summary>
        private void ReleaseArena()
        {
            if (_ownsArena)
            {
                _flow?.Teardown();
            }

            _flow = null;
            _realization = null;
            _manifest = null;
            _ownsArena = false;
        }

        /// <summary>A failed raise leaves nothing behind: the flow's teardown is idempotent at any stage.</summary>
        private void Abort(string reason)
        {
            _logger?.LogWarning($"Encounter not started ({reason}). Nothing was placed; the fail-closed path "
                + "tore down whatever had been acquired.");
            CleanupGate();
            ReleaseArena();
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
