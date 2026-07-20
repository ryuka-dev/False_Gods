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
    /// </remarks>
    internal sealed class ClientBossController : IDisposable
    {
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

        // The locally realized arena for the announced/joined encounter.
        private BundleArenaRealization? _realization;
        private ArenaLoadFlow? _arenaFlow;
        private LoadedArena? _loadedArena;
        private ArenaPresentation? _arenaPresentation;
        private EncounterId? _arenaEncounter;
        private bool _lateJoinArenaFailed;
        private bool _arenaSnapshotReplayed;

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

        public bool IsUp => _presentation != null;

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

        /// <summary>The host announced an arena: run the identical local load at the host's origin and hand back
        /// the locally recomputed manifest (or the failure to report). A previous arena is replaced.</summary>
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
                _logger?.Log($"Arena for {enter.Encounter} realized at ({enter.Origin.X:0.0}, {enter.Origin.Y:0.0}, "
                    + $"{enter.Origin.Z:0.0}); reporting ArenaReady.");
            }

            return outcome;
        }

        private ClientLoadOutcome RealizeArenaAt(WorldPosition origin, EncounterId encounter)
        {
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

            _realization = realization;
            _arenaFlow = flow;
            _loadedArena = realized.Arena;
            _arenaPresentation = new ArenaPresentation(realization, _logger);
            _arenaEncounter = encounter;
            _arenaSnapshotReplayed = false;
            return ClientLoadOutcome.Ready(realized.Manifest);
        }

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

            if (_arenaFlow != null && _arenaEncounter == baseline.Encounter)
            {
                return; // already realized for this encounter (the EnterArena path)
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

        private void TeardownArena()
        {
            _arenaPresentation = null;
            _arenaFlow?.Teardown();
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
