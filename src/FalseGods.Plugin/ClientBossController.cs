using System;
using System.IO;
using FalseGods.Application.Arena;
using FalseGods.Application.Combat;
using FalseGods.Application.Presentation;
using FalseGods.Application.Replication;
using FalseGods.Core.Simulation;
using FalseGods.Integration.Sulfur.Arena;
using FalseGods.Integration.Sulfur.Combat;
using FalseGods.Integration.Sulfur.Presentation;
using FalseGods.Integration.Sulfur.Navigation;
using FalseGods.Integration.Sulfur.Simulation;
using FalseGods.Protocol.Wire;
using FalseGods.RuntimeContracts.Arena;
using FalseGods.RuntimeContracts.Integration;
using FalseGods.UnityRuntime.Arena;
using FalseGods.UnityRuntime.Presentation;
using UnityEngine;
using ILogger = FalseGods.RuntimeContracts.Diagnostics.ILogger;

namespace FalseGods.Plugin
{
    /// <summary>
    /// The multiplayer-client encounter composition: presentation only, driven by the host. The
    /// <see cref="ClientEncounterFlow"/> answers the host's <c>EnterArena</c> by running the same local
    /// <see cref="ArenaLoadFlow"/> the host ran (realize at the host's origin, verify parity, apply navigation)
    /// and reports <c>ArenaReady</c> with the locally recomputed manifest; the <see cref="ReplicationReceiver"/>
    /// applies the host's streams idempotently and <see cref="WirePresentationMapping"/> feeds the identical
    /// presentation entry points the host uses (Architecture §4.3/§7).
    /// </summary>
    /// <remarks>
    /// No <c>BossSimulation</c>, no authoritative decision. The boss puppet stands exactly where the host says,
    /// height included — the host's boss may be standing on a terrace — while the locally realized arena supplies
    /// the floor that telegraphs and impacts are drawn on. A late joiner that never saw <c>EnterArena</c> realizes the arena from the
    /// baseline's origin and verifies its own content hash against the baseline's before showing anything
    /// (fail-visible: mismatched content logs and shows no arena). <c>EncounterAborted</c> tears the arena down;
    /// <c>EncounterEnded</c> discards the whole encounter — puppet, arena, and stream state.
    ///
    /// <para><b>Two ways the arena gets here, one sequence — the same pair the host has.</b> When a hijacked
    /// level load already left the arena standing on this peer (Strategy A), the encounter <i>adopts</i> that one
    /// rather than realizing a second copy of the same content on top of it. Otherwise it realizes its own at the
    /// host's origin through the ordinary <see cref="ArenaLoadFlow"/>. Either way the manifest reported in
    /// <c>ArenaReady</c> comes from a real local load — an adopted arena was realized through the identical flow,
    /// with the same parity check and the same locally recomputed content hash.</para>
    /// </remarks>
    internal sealed class ClientBossController : IDisposable
    {
        // How far a standing arena's origin may sit from the one the host announced and still be the same arena.
        // Both hijacked loads realize at exactly the level origin, so this only absorbs the wire quantization.
        private const float OriginEpsilon = 0.05f;

        private readonly ILogger? _logger;
        private readonly IFalseGodsIntegration _integration;
        private readonly string _contentDirectory;
        private readonly ClientEncounterFlow _controlFlow;
        private readonly ClientHitReporter _hitReporter;
        private readonly IDamagePort _damagePort;
        private readonly SulfurLocalPlayer _localPlayer;
        private readonly IBattlefieldCleanupPort _battlefield;
        private readonly IBossArmPort _rageArms;
        private readonly IBossVoicePort _voice;
        private readonly IArenaAtmospherePort _atmosphere;
        private readonly IBossRewardPort _reward;

        /// <summary>Whether this peer has declared the level it is in (or is loading) to be the boss arena, so an
        /// arena of its own is on its way and a second copy must not be loaded on top of it.</summary>
        private readonly Func<bool> _levelWillBringTheArena;

        /// <summary>The same declaration the host makes, made here too: aim assist and homing run on the machine
        /// doing the aiming, so each peer has to show its own game its own puppet.</summary>
        private readonly IBossPresencePort _presence;

        private ReplicationReceiver _receiver;
        private IDisposable? _hitBinding;
        private BossPresentation? _presentation;
        private EncounterId? _encounter;
        private int _presentedEvents;
        private int _presentedArenaEvents;
        private bool _waitingForCameraLogged;

        // The arena this peer fights in for the announced/joined encounter — realized here, or adopted from the
        // level that already realized it.
        private BundleArenaRealization? _realization;
        private ArenaLoadFlow? _arenaFlow;
        private LoadedArena? _loadedArena;
        private ArenaPresentation? _arenaPresentation;
        private EncounterId? _arenaEncounter;
        private bool _lateJoinArenaFailed;
        private bool _arenaSnapshotReplayed;

        // Whether this peer has already played (or caught up on) the opening for the encounter it is watching.
        private bool _openingPlayed;
        private bool _waitingForOwnArenaLogged;

        // False while the encounter is fought in an arena the LEVEL owns (Strategy A): the arena outlives the
        // fight there, so tearing the encounter down must not take the level's start area with it.
        private bool _ownsArena;

        /// <param name="atmosphere">What the room does when the fight starts. Built by the composition root and
        /// shared with the local controller: only one of the two is ever driving a room.</param>
        /// <param name="levelWillBringTheArena">Whether this peer is in — or on its way into — the level it has
        /// declared to be the boss arena. While that is true and no arena is standing yet, the host's announcement
        /// is waited on rather than answered with a load of our own.</param>
        public ClientBossController(
            ILogger logger,
            MonoBehaviour host,
            IFalseGodsIntegration integration,
            IArenaAtmospherePort atmosphere,
            Func<bool> levelWillBringTheArena)
        {
            _logger = logger;
            _integration = integration ?? throw new ArgumentNullException(nameof(integration));
            _atmosphere = atmosphere ?? throw new ArgumentNullException(nameof(atmosphere));
            _levelWillBringTheArena = levelWillBringTheArena ?? throw new ArgumentNullException(nameof(levelWillBringTheArena));
            _contentDirectory = Path.GetDirectoryName(typeof(ClientBossController).Assembly.Location) ?? ".";
            _receiver = new ReplicationReceiver(integration.Channel, integration.Session);
            _hitReporter = new ClientHitReporter(integration.Channel, integration.Session);
            _damagePort = new SulfurDamagePort(logger);
            _localPlayer = new SulfurLocalPlayer();
            _battlefield = new SulfurBattlefieldCleanup(logger);
            _rageArms = new SulfurBossArmPort(host, logger);
            // Built here like the rest of this end's adapters: a client pays its own player, because loot is a
            // local pickup nothing mirrors. See IBossRewardPort.
            _reward = new SulfurBossReward(logger);
            _presence = new SulfurBossPresence(() => _presentation?.CollisionCollider, logger);
            _voice = new SulfurBossVoice(host.transform, logger);
            _voice.Warm();
            _atmosphere.Warm();
            _controlFlow = new ClientEncounterFlow(integration.Channel, integration.Session)
            {
                OnEnterArena = HandleEnterArena,
                OnAborted = aborted =>
                {
                    _logger?.Log($"Host aborted {aborted.Encounter} at the gate ({aborted.Reason}); tearing the local arena down.");
                    TeardownArena();
                },
                OnEnded = ended =>
                {
                    _logger?.Log($"Host ended {ended.Encounter}; discarding the encounter.");
                    DiscardEncounter();
                },
                OnBossHitPlayer = HandleBossHitPlayer,
            };
            _logger?.Log("Client encounter composition ready: listening for the host's announcements and streams.");
        }

        /// <summary>
        /// The arena a hijacked level load left standing, when there is one. Set by the Composition Root; while it
        /// reports a live arena, the encounter is fought in <i>that</i> arena instead of realizing its own copy —
        /// the same adoption a host raise does (see <c>LocalEncounterController.Raise</c>).
        /// </summary>
        public HijackedArenaContent? LevelArena { get; set; }

        public bool IsUp => _presentation != null;

        /// <summary>
        /// Advance one frame: realize the arena from the baseline when this is a late join, raise the puppet where
        /// the host's first state says it stands, replay newly-applied wire events as presentation cues, apply the
        /// latest snapshot state, and render.
        /// </summary>
        public void Tick(float deltaSeconds)
        {
            // An announcement this peer was not ready for is retried here, not on the channel thread: what it is
            // waiting for is the level finishing its own generation.
            _controlFlow.Tick(deltaSeconds);
            TryRealizeFromBaseline();

            var snapshot = _receiver.LatestBossSnapshot;
            if (snapshot is null)
            {
                return;
            }

            if (_encounter != null && snapshot.Encounter != _encounter.Value)
            {
                ResetForNewEncounter(snapshot.Encounter);
                return; // the fresh receiver repopulates from the host's next messages
            }

            if (_presentation is null
                && !TryRaisePresentation(
                    snapshot.Position.X, snapshot.PositionHeight, snapshot.Position.Z, snapshot.Encounter))
            {
                return;
            }

            ReplayArenaSnapshotOnce();

            var events = _receiver.AppliedBossEvents;
            for (; _presentedEvents < events.Count; _presentedEvents++)
            {
                var bossEvent = events[_presentedEvents];
                _presentation!.Handle(WirePresentationMapping.ToEvent(snapshot.Boss, bossEvent));
                if (bossEvent is BossRelocatedEvent)
                {
                    ClearOurOwnFloor();
                }
                else if (bossEvent is BossEnragedEvent enraged && enraged.Enraged)
                {
                    // Made here rather than sent: a roar decides nothing, and this end knows where the boss is.
                    _voice.Roar(new ArenaWorldPoint(
                        snapshot.Position.X, snapshot.PositionHeight, snapshot.Position.Z));
                }
                else if (bossEvent is BossBeganEvent)
                {
                    PlayTheOpening(snapshot, withCeremony: true);
                }
                else if (bossEvent is BossDefeatedEvent)
                {
                    _atmosphere.StopBattleMusic();
                    _presence.HideHealthBar();

                    // This peer's own roll, on the way out. Not sent and not asked for: the host is paying its own
                    // player in the same frame, off the same fact, and both read the place out of the room they
                    // each realized — so the two payouts land together without either being told where.
                    _reward.DropReward(_loadedArena?.RewardDrop ?? new ArenaWorldPoint(
                        snapshot.Position.X, snapshot.PositionHeight, snapshot.Position.Z));
                }
            }

            CatchUpOnTheOpening(snapshot);

            var arenaEvents = _receiver.AppliedArenaEvents;
            for (; _presentedArenaEvents < arenaEvents.Count; _presentedArenaEvents++)
            {
                _arenaPresentation?.Handle(WirePresentationMapping.ToEvent(arenaEvents[_presentedArenaEvents]));
            }

            var state = WirePresentationMapping.ToState(snapshot);
            _presentation!.Apply(state);
            _presence.ReportHealth(state.HealthFraction);
            CarryTheHostsArms(snapshot);
            _presentation.Render(deltaSeconds);
        }

        /// <summary>
        /// Put the host's arms where they belong on this machine.
        /// </summary>
        /// <remarks>
        /// <para><b>The session layer mirrors a spawn, not a journey.</b> Measured in a two-peer session: the
        /// host's arms follow its boss across the room while a client's copies stand exactly where they first
        /// appeared, so by the boss's next station they are throwing mud from a place with no boss in it.</para>
        /// <para>Nothing has to be sent for this. The boss's pose is already replicated, and so is whether it is
        /// enraged, so the client works the arms' places out with the same rule the host used and arrives at the
        /// same answer. Raising and killing them stay the host's; this peer only carries.</para>
        /// </remarks>
        private void CarryTheHostsArms(BossSnapshot snapshot)
        {
            if (!snapshot.Enraged)
            {
                _rageArms.Release();
                return;
            }

            _rageArms.Adopt(RageArms.Count);
            _rageArms.Follow(new ArmPlacement(
                new ArenaWorldPoint(snapshot.Position.X, snapshot.PositionHeight, snapshot.Position.Z),
                snapshot.Facing,
                RageArms.SideDistance,
                RageArms.ForwardOffset,
                RageArms.Lift,
                RageArms.Scale));
        }

        /// <summary>
        /// Play the opening on this machine: the boss bellows, this peer's own room opens, and the music starts.
        /// </summary>
        /// <remarks>
        /// <b>Nothing is sent for any of it.</b> The one fact that crossed is that the host's boss began; the roar,
        /// the fog and the music are each peer's own account of it, made with the same numbers the host used. Which
        /// is also why it is safe for a client to do this at all — none of it decides anything.
        /// </remarks>
        private void PlayTheOpening(BossSnapshot snapshot, bool withCeremony)
        {
            _openingPlayed = true;
            if (withCeremony)
            {
                _voice.Roar(new ArenaWorldPoint(
                    snapshot.Position.X, snapshot.PositionHeight, snapshot.Position.Z));
                _atmosphere.SetRoomDepth(
                    ArenaDepth.FightStart,
                    ArenaDepth.FightEnd,
                    ArenaDepth.RevealHoldSeconds,
                    ArenaDepth.RevealSeconds);
                _logger?.Log("[opening] the host's boss began; roaring and opening the room here.");
            }
            else
            {
                // Caught up rather than played: no pause, no roar. A peer that arrived after the fight started has
                // nothing to be shown, and showing it anyway would be a second opening for a fight already running.
                _atmosphere.SetRoomDepth(ArenaDepth.FightStart, ArenaDepth.FightEnd);
                _logger?.Log("[opening] the fight was already under way; opening the room without the ceremony.");
            }

            _atmosphere.StartBattleMusic();

            // The same bar the host is showing, driven by this peer's own copy of the host's health.
            _presence.ShowHealthBar();
        }

        /// <summary>
        /// Correct a peer that missed the beginning. The snapshot carries whether the fight is on, which is exactly
        /// what a late joiner — or a peer whose reliable event arrived before it had an arena to show — needs to
        /// stop standing in a dark, silent room while everyone else fights.
        /// </summary>
        private void CatchUpOnTheOpening(BossSnapshot snapshot)
        {
            if (_openingPlayed || !snapshot.Begun)
            {
                return;
            }

            PlayTheOpening(snapshot, withCeremony: false);
        }

        /// <summary>
        /// Clear this peer's own bodies when the host's boss moves on.
        /// </summary>
        /// <remarks>
        /// <para><b>Each peer sweeps for itself, and that is not a divergence.</b> The host does the same thing at
        /// the same moment, off the same fact — its boss relocating is what produced the event this is reading —
        /// and the bodies are already dead on both machines. Nothing is being decided here: a corpse is not a
        /// thing anybody is still fighting, so removing this peer's copy of one cannot disagree with the host
        /// about anything that matters.</para>
        /// <para>It has to be done here because the session layer mirrors <i>spawns</i>, not removals: measured
        /// in a two-peer session, the host's floor cleared and the client's stayed exactly as it was.</para>
        /// </remarks>
        private void ClearOurOwnFloor()
        {
            var arena = _loadedArena;
            if (arena is null)
            {
                return;
            }

            var swept = _battlefield.SweepCorpses(arena.Origin, BattlefieldSweep.ArenaReach);
            _logger?.Log($"[cleanup] the host's boss moved on; {swept} body/bodies going into the floor here.");
        }

        /// <summary>Tear everything down; nothing from the encounter remains in the level.</summary>
        public void Dispose()
        {
            _controlFlow.Dispose();
            _receiver.Dispose();
            _hitBinding?.Dispose();
            _hitBinding = null;
            _presence.Withdraw();
            _presentation?.Dispose();
            _presentation = null;
            _openingPlayed = false;
            // The atmosphere itself belongs to the composition root and outlives this controller; what this leaves
            // behind is only what it started.
            _atmosphere.StopBattleMusic();
            TeardownArena();
            _logger?.Log("Client encounter composition torn down; nothing remains.");
        }

        /// <summary>The host announced an arena: get into the same arena at the host's origin — adopting the one
        /// a hijacked level left standing, or realizing our own — and hand back the locally recomputed manifest
        /// (or the failure to report). A previous arena is released first.</summary>
        private ClientLoadOutcome HandleEnterArena(EnterArena enter)
        {
            TeardownArena();
            _lateJoinArenaFailed = false;

            var outcome = RealizeArenaAt(enter.Origin, enter.Encounter);
            if (outcome.NotYet)
            {
                // Said once, not every frame it is asked again.
                if (!_waitingForOwnArenaLogged)
                {
                    _waitingForOwnArenaLogged = true;
                    _logger?.Log($"The host announced {enter.Encounter}, but this peer's own arena is still being "
                        + $"built by the level ({outcome.FailureReason}); waiting for it rather than loading a "
                        + "second copy.");
                }

                return outcome;
            }

            _waitingForOwnArenaLogged = false;
            if (outcome.Manifest is null)
            {
                _logger?.LogWarning($"Arena load for {enter.Encounter} failed: {outcome.FailureReason}. Reporting ArenaLoadFailed.");
                return outcome;
            }

            _logger?.Log($"Arena for {enter.Encounter} ready at ({enter.Origin.X:0.0}, {enter.Origin.Y:0.0}, "
                + $"{enter.Origin.Z:0.0}); reporting ArenaReady.");

            // A fresh encounter starts in the dark here as it does on the host, so the opening reads the same on
            // both machines. What lifts it is the host's boss beginning, which arrives on the wire.
            _openingPlayed = false;
            _atmosphere.SetRoomDepth(ArenaDepth.OpeningStart, ArenaDepth.OpeningEnd);
            return outcome;
        }

        /// <summary>Get this peer into the encounter's arena at <paramref name="origin"/>: adopt the one the level
        /// already stands in when there is one, else realize our own there.</summary>
        private ClientLoadOutcome RealizeArenaAt(WorldPosition origin, EncounterId encounter)
        {
            var levelArena = LevelArena;
            if (levelArena != null && levelArena.IsLive)
            {
                return AdoptLevelArena(levelArena, origin, encounter);
            }

            // This peer has declared the level it is in — or is still loading — to be the arena, so an arena of its
            // own is coming. Loading one here would not merely duplicate it: a standing arena holds the bundle
            // open, and whichever of the two came second would fail (Runbook §3.12). The host raises the moment its
            // own level is up, which is routinely before a peer has finished generating the same level, so this is
            // the ordinary case and not an unusual one.
            if (_levelWillBringTheArena())
            {
                return ClientLoadOutcome.Deferred(
                    "this peer's level is still building the arena it declared");
            }

            var realization = new BundleArenaRealization(
                Path.Combine(_contentDirectory, LocalEncounterController.BundleFileName),
                Path.Combine(_contentDirectory, LocalEncounterController.ArtifactFileName),
                LocalEncounterController.ArenaPrefabName,
                _logger);
            var flow = new ArenaLoadFlow(
                realization,
                realization,
                new AstarNavigationPort(() => realization.CurrentRoot, _logger),
                new SulfurVanillaAssetProvider(() => realization.CurrentRoot, _logger));

            var prepared = flow.Prepare();
            if (!prepared.Success)
            {
                flow.Teardown();
                return ClientLoadOutcome.Failed(prepared.FailureReason ?? "prepare failed");
            }

            var realized = flow.Realize(new ArenaWorldPoint(origin.X, origin.Y, origin.Z));
            if (!realized.Success || realized.Manifest is null || realized.Arena is null)
            {
                return ClientLoadOutcome.Failed(realized.FailureReason ?? "realize failed");
            }

            _ownsArena = true;
            _realization = realization;
            _arenaFlow = flow;
            _loadedArena = realized.Arena;
            _arenaPresentation = new ArenaPresentation(realization, _logger);
            _arenaEncounter = encounter;
            _arenaSnapshotReplayed = false;
            return ClientLoadOutcome.Ready(realized.Manifest);
        }

        /// <summary>
        /// Adopt the arena a hijacked level load already left standing on this peer, the way a host raise adopts
        /// its own. The level realized it through the same <see cref="ArenaLoadFlow"/> — same bundle, same
        /// realized-vs-authored parity check, same locally recomputed content hash — so its manifest is exactly
        /// what this peer would report after loading a second copy. Loading that second copy is not merely
        /// wasteful: the standing arena holds the AssetBundle open, and a second <c>LoadFromFile</c> of the same
        /// file fails, which is why a client standing in a hijacked level used to fail the whole encounter closed.
        /// <para>The arena belongs to the level, not to this encounter: an encounter teardown leaves it standing.</para>
        /// </summary>
        private ClientLoadOutcome AdoptLevelArena(
            HijackedArenaContent levelArena, WorldPosition origin, EncounterId encounter)
        {
            var standing = levelArena.Realized;
            var realization = levelArena.Realization;
            if (standing?.Manifest is null || standing.Arena is null || realization is null)
            {
                return ClientLoadOutcome.Failed("the level's arena is standing but reported no load result");
            }

            // The host's arena and the one standing here must be the same room in the same place: boss and
            // mechanism positions arrive in world coordinates, so an origin that disagrees would put the fight
            // somewhere this player is not. Both hijacked loads realize at the level origin, so agreement is the
            // normal case; a mismatch means the host is fighting elsewhere — report it rather than show a wrong
            // arena, and never load a second copy into a level that is already our arena.
            var standingOrigin = standing.Arena.Origin;
            if (!SameOrigin(standingOrigin, origin))
            {
                return ClientLoadOutcome.Failed(
                    $"the level's arena stands at ({standingOrigin.X:0.0}, {standingOrigin.Y:0.0}, "
                    + $"{standingOrigin.Z:0.0}) but the host announced ({origin.X:0.0}, {origin.Y:0.0}, "
                    + $"{origin.Z:0.0})");
            }

            _ownsArena = false;
            _realization = realization;
            _arenaFlow = null;
            _loadedArena = standing.Arena;
            _arenaPresentation = new ArenaPresentation(realization, _logger);
            _arenaEncounter = encounter;
            _arenaSnapshotReplayed = false;
            _logger?.Log($"Adopting the arena the hijacked level left standing for {encounter}; "
                + "no second copy loaded, and the level keeps it when the encounter ends.");
            return ClientLoadOutcome.Ready(standing.Manifest);
        }

        /// <summary>Whether a standing arena is where the host says the encounter's arena is, within a tolerance
        /// far below anything that would misplace the fight.</summary>
        private static bool SameOrigin(ArenaWorldPoint standing, WorldPosition announced) =>
            Math.Abs(standing.X - announced.X) <= OriginEpsilon
            && Math.Abs(standing.Y - announced.Y) <= OriginEpsilon
            && Math.Abs(standing.Z - announced.Z) <= OriginEpsilon;

        /// <summary>A late joiner never saw EnterArena: realize from the baseline's origin, then verify the
        /// locally recomputed content hash against the baseline's — mismatched content shows nothing rather than
        /// a wrong arena (fail-visible, logged once).</summary>
        private void TryRealizeFromBaseline()
        {
            var baseline = _receiver.Baseline;
            if (baseline is null || _lateJoinArenaFailed)
            {
                return;
            }

            if (_loadedArena != null && _arenaEncounter == baseline.Encounter)
            {
                return; // already standing in this encounter's arena (realized here, or adopted from the level)
            }

            var outcome = RealizeArenaAt(baseline.ArenaOrigin, baseline.Encounter);
            if (outcome.NotYet)
            {
                return; // the level is still building this peer's arena; try again next frame
            }

            if (outcome.Manifest is null)
            {
                _lateJoinArenaFailed = true;
                _logger?.LogWarning($"Late-join arena load failed: {outcome.FailureReason}. The boss puppet will "
                    + "not be shown for this encounter.");
                return;
            }

            if (!string.Equals(outcome.Manifest.ArenaId, baseline.ArenaId, StringComparison.Ordinal)
                || outcome.Manifest.ArenaVersion != baseline.ArenaVersion
                || outcome.Manifest.ContentHash != baseline.ContentHash)
            {
                _lateJoinArenaFailed = true;
                _logger?.LogWarning("Late-join arena content does not match the host's baseline "
                    + $"({outcome.Manifest.ArenaId} v{outcome.Manifest.ArenaVersion} vs {baseline.ArenaId} "
                    + $"v{baseline.ArenaVersion}); tearing it down and showing nothing.");
                TeardownArena();
                return;
            }

            _logger?.Log($"Late join: arena for {baseline.Encounter} realized from the baseline's origin and "
                + "content-verified against it.");
        }

        /// <summary>Replay the baseline/latest arena snapshot as idempotent cues once the arena and puppet are
        /// up, so a late joiner shows mechanisms and an opened exit it never saw the events for.</summary>
        private void ReplayArenaSnapshotOnce()
        {
            if (_arenaSnapshotReplayed || _arenaPresentation is null)
            {
                return;
            }

            var arenaSnapshot = _receiver.LatestArenaSnapshot;
            if (arenaSnapshot is null)
            {
                return;
            }

            _arenaSnapshotReplayed = true;
            var cues = WirePresentationMapping.ToEvents(arenaSnapshot);
            for (var i = 0; i < cues.Count; i++)
            {
                _arenaPresentation.Handle(cues[i]);
            }
        }

        private bool TryRaisePresentation(float x, float height, float z, EncounterId encounter)
        {
            // The arena is what makes a puppet placeable at all: the host's position is world-space, and the
            // arena's own authored floor is where this peer draws telegraphs and impacts. Without an arena (load
            // failed / not yet announced) there is nothing correct to stand the puppet on — show nothing rather
            // than guess.
            if (_loadedArena is null || _arenaEncounter != encounter)
            {
                if (!_waitingForCameraLogged)
                {
                    _waitingForCameraLogged = true;
                    _logger?.Log($"Host boss state for {encounter} arrived before a matching local arena; waiting.");
                }

                return false;
            }

            // The puppet stands exactly where the host says, height included — the host's boss may be on a
            // terrace. The arena's authored floor is a separate fact, and stays the ground for telegraphs.
            _presentation = new BossPresentation(
                _logger, new Vector3(x, height, z), _loadedArena.BossSpawn.Y);

            // The puppet takes its size from the same authored room the host's boss takes it from, so the two
            // agree without the size ever going on the wire.
            if (_loadedArena.BossSize > 0f)
            {
                _presentation.SpriteScale = _loadedArena.BossSize;
            }

            // Same as the host: this peer's own weapons have to be able to find this peer's own puppet.
            _presence.Declare();

            _encounter = encounter;
            _presentedEvents = 0;
            _presentedArenaEvents = 0;
            _waitingForCameraLogged = false;

            // Hit intent: the local player's real weapons strike the puppet's Hitbox capsule exactly as they strike
            // the host's boss, but the sink reports the hit to the host instead of applying damage — the client
            // decides nothing, the authoritative result returns as a replicated BossDamaged event (§5.6).
            _hitBinding = BossWeaponDamage.Bind(
                _presentation.HitCollider.gameObject, new HitReportSink(_hitReporter, encounter), _logger);

            _logger?.Log($"Client boss puppet raised for {encounter} at ({x:0.0}, {height:0.0}, {z:0.0}), "
                + "where the host says it stands; host-driven. Your weapons report hits to the host.");
            return true;
        }

        private void ResetForNewEncounter(EncounterId next)
        {
            _logger?.Log($"Host started {next}; discarding the previous encounter's stream and visuals.");
            _receiver.Dispose();
            _receiver = new ReplicationReceiver(_integration.Channel, _integration.Session);
            _hitBinding?.Dispose();
            _hitBinding = null;
            _presence.Withdraw();
            _presentation?.Dispose();
            _presentation = null;
            _encounter = null;
            _presentedEvents = 0;
            _presentedArenaEvents = 0;
            // A new encounter gets its own opening, and the previous fight's music is not its.
            _openingPlayed = false;
            _atmosphere.StopBattleMusic();
            // The arena for the new encounter arrives via EnterArena (or the new baseline); a stale one is
            // replaced there. If the announcement already came, keep it.
            if (_arenaEncounter != null && _arenaEncounter != next)
            {
                TeardownArena();
            }
        }

        /// <summary>Discard everything encounter-local: puppet, arena, and stream state (EncounterEnded).</summary>
        private void DiscardEncounter()
        {
            _receiver.Dispose();
            _receiver = new ReplicationReceiver(_integration.Channel, _integration.Session);
            _hitBinding?.Dispose();
            _hitBinding = null;
            _presence.Withdraw();
            _presentation?.Dispose();
            _presentation = null;
            _encounter = null;
            _presentedEvents = 0;
            _presentedArenaEvents = 0;
            _openingPlayed = false;
            _atmosphere.StopBattleMusic();
            TeardownArena();
        }

        /// <summary>The host resolved that the boss hit this client's player; apply the host-decided damage to the
        /// local player (§5.6). The client owns its own health — it applies the amount, it never recomputes it. Only
        /// the current encounter's hits count; a stale or non-positive one is ignored.</summary>
        private void HandleBossHitPlayer(BossHitPlayer hit)
        {
            if (hit.Amount <= 0 || _encounter == null || hit.Encounter != _encounter.Value)
            {
                return;
            }

            if (_localPlayer.TryGetLocalParticipantIndex(out var localIndex))
            {
                _damagePort.ApplyDamage(new ParticipantId(localIndex), hit.Amount);
                _logger?.Log($"Boss hit you for {hit.Amount} (host-authoritative); applied to your local player.");
            }
        }

        /// <summary>Give up this encounter's claim on the arena: torn down when the encounter realized it, merely
        /// let go of when it belongs to the level (Strategy A) and must outlive the fight. Idempotent.</summary>
        private void TeardownArena()
        {
            _arenaPresentation = null;
            if (_ownsArena)
            {
                _arenaFlow?.Teardown();
            }

            _ownsArena = false;
            _arenaFlow = null;
            _realization = null;
            _loadedArena = null;
            _arenaEncounter = null;
            _arenaSnapshotReplayed = false;
        }

        /// <summary>The client's <see cref="IBossDamageSink"/>: a real weapon hit on the puppet becomes a hit
        /// <b>report</b> to the host for a fixed encounter, never a local damage application (§5.6, invariant 2).</summary>
        private sealed class HitReportSink : IBossDamageSink
        {
            private readonly ClientHitReporter _reporter;
            private readonly EncounterId _encounter;

            public HitReportSink(ClientHitReporter reporter, EncounterId encounter)
            {
                _reporter = reporter;
                _encounter = encounter;
            }

            public void ApplyWeaponDamage(float amount) => _reporter.ReportHit(_encounter, amount);
        }
    }
}
