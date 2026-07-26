using System;
using System.IO;
using FalseGods.Application.Arena;
using FalseGods.Application.Combat;
using FalseGods.Application.Presentation;
using FalseGods.Application.Replication;
using FalseGods.Core.Simulation;
using FalseGods.Integration.Sulfur.Arena;
using FalseGods.Integration.Sulfur.Combat;
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
    /// No <c>BossSimulation</c>, no authoritative decision. The boss puppet stands on the <b>arena's</b> floor —
    /// the authored enemy-spawn height of the locally realized arena at the host's origin — replacing the old
    /// local-camera-height guess. A late joiner that never saw <c>EnterArena</c> realizes the arena from the
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

        // False while the encounter is fought in an arena the LEVEL owns (Strategy A): the arena outlives the
        // fight there, so tearing the encounter down must not take the level's start area with it.
        private bool _ownsArena;

        public ClientBossController(ILogger logger, IFalseGodsIntegration integration)
        {
            _logger = logger;
            _integration = integration ?? throw new ArgumentNullException(nameof(integration));
            _contentDirectory = Path.GetDirectoryName(typeof(ClientBossController).Assembly.Location) ?? ".";
            _receiver = new ReplicationReceiver(integration.Channel, integration.Session);
            _hitReporter = new ClientHitReporter(integration.Channel, integration.Session);
            _damagePort = new SulfurDamagePort(logger);
            _localPlayer = new SulfurLocalPlayer();
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
        /// Advance one frame: realize the arena from the baseline when this is a late join, raise the puppet on
        /// the arena floor when the host's state first arrives, replay newly-applied wire events as presentation
        /// cues, apply the latest snapshot state, and render.
        /// </summary>
        public void Tick(float deltaSeconds)
        {
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

            if (_presentation is null && !TryRaisePresentation(snapshot.Position.X, snapshot.Position.Z, snapshot.Encounter))
            {
                return;
            }

            ReplayArenaSnapshotOnce();

            var events = _receiver.AppliedBossEvents;
            for (; _presentedEvents < events.Count; _presentedEvents++)
            {
                _presentation!.Handle(WirePresentationMapping.ToEvent(snapshot.Boss, events[_presentedEvents]));
            }

            var arenaEvents = _receiver.AppliedArenaEvents;
            for (; _presentedArenaEvents < arenaEvents.Count; _presentedArenaEvents++)
            {
                _arenaPresentation?.Handle(WirePresentationMapping.ToEvent(arenaEvents[_presentedArenaEvents]));
            }

            _presentation!.Apply(WirePresentationMapping.ToState(snapshot));
            _presentation.Render(deltaSeconds);
        }

        /// <summary>Tear everything down; nothing from the encounter remains in the level.</summary>
        public void Dispose()
        {
            _controlFlow.Dispose();
            _receiver.Dispose();
            _hitBinding?.Dispose();
            _hitBinding = null;
            _presentation?.Dispose();
            _presentation = null;
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
            if (outcome.Manifest is null)
            {
                _logger?.LogWarning($"Arena load for {enter.Encounter} failed: {outcome.FailureReason}. Reporting ArenaLoadFailed.");
            }
            else
            {
                _logger?.Log($"Arena for {enter.Encounter} ready at ({enter.Origin.X:0.0}, {enter.Origin.Y:0.0}, "
                    + $"{enter.Origin.Z:0.0}); reporting ArenaReady.");
            }

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

        private bool TryRaisePresentation(float x, float z, EncounterId encounter)
        {
            // The authoritative floor: the locally realized arena's authored boss-spawn height at the host's
            // origin. Without an arena (load failed / not yet announced) there is nothing correct to stand the
            // puppet on — show nothing rather than guess.
            if (_loadedArena is null || _arenaEncounter != encounter)
            {
                if (!_waitingForCameraLogged)
                {
                    _waitingForCameraLogged = true;
                    _logger?.Log($"Host boss state for {encounter} arrived before a matching local arena; waiting.");
                }

                return false;
            }

            var floorY = _loadedArena.BossSpawn.Y;
            _presentation = new BossPresentation(_logger, new Vector3(x, floorY, z));

            // The puppet takes its size from the same authored room the host's boss takes it from, so the two
            // agree without the size ever going on the wire.
            if (_loadedArena.BossSize > 0f)
            {
                _presentation.SpriteScale = _loadedArena.BossSize;
            }

            _encounter = encounter;
            _presentedEvents = 0;
            _presentedArenaEvents = 0;
            _waitingForCameraLogged = false;

            // Hit intent: the local player's real weapons strike the puppet's Hitbox capsule exactly as they strike
            // the host's boss, but the sink reports the hit to the host instead of applying damage — the client
            // decides nothing, the authoritative result returns as a replicated BossDamaged event (§5.6).
            _hitBinding = BossWeaponDamage.Bind(
                _presentation.HitCollider.gameObject, new HitReportSink(_hitReporter, encounter), _logger);

            _logger?.Log($"Client boss puppet raised for {encounter} at ({x:0.0}, {floorY:0.0}, {z:0.0}) on the arena floor; host-driven. Your weapons report hits to the host.");
            return true;
        }

        private void ResetForNewEncounter(EncounterId next)
        {
            _logger?.Log($"Host started {next}; discarding the previous encounter's stream and visuals.");
            _receiver.Dispose();
            _receiver = new ReplicationReceiver(_integration.Channel, _integration.Session);
            _hitBinding?.Dispose();
            _hitBinding = null;
            _presentation?.Dispose();
            _presentation = null;
            _encounter = null;
            _presentedEvents = 0;
            _presentedArenaEvents = 0;
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
            _presentation?.Dispose();
            _presentation = null;
            _encounter = null;
            _presentedEvents = 0;
            _presentedArenaEvents = 0;
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
