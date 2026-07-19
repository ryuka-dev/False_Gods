using System;
using FalseGods.Application.Presentation;
using FalseGods.Application.Replication;
using FalseGods.Core.Simulation;
using FalseGods.RuntimeContracts.Integration;
using FalseGods.UnityRuntime.Presentation;
using UnityEngine;
using ILogger = FalseGods.RuntimeContracts.Diagnostics.ILogger;

namespace FalseGods.Plugin
{
    /// <summary>
    /// The multiplayer-client boss composition: presentation only, driven by the host's replication stream — a
    /// <see cref="ReplicationReceiver"/> applies the wire messages idempotently and
    /// <see cref="WirePresentationMapping"/> turns them into the same presentation contracts the host's own
    /// presenter produces, feeding the identical <see cref="BossPresentation"/> entry point (Architecture §4.3/§7).
    /// </summary>
    /// <remarks>
    /// There is no <c>BossSimulation</c> here and no authoritative decision: the boss appears when the host's
    /// baseline or first snapshot arrives, is placed on this machine's local floor (presentation-owned, like the
    /// billboard facing), and every phase/weak-point/death change is an applied host result. A snapshot carrying a
    /// new <c>EncounterId</c> means the host started a new encounter — the old receiver and visuals are discarded
    /// and rebuilt, so a host re-raise during development does not leave a stale puppet behind.
    ///
    /// <para>
    /// Known limitation (dev slice): if the host tears the boss down mid-fight, no wire message says so yet — the
    /// last-known state keeps rendering until the session ends, this controller is disposed, or a new encounter
    /// starts. The discrete encounter-teardown event is part of the arena/teardown slice (PoC B9/B10).
    /// </para>
    /// </remarks>
    internal sealed class ClientBossController : IDisposable
    {
        private readonly ILogger? _logger;
        private readonly IFalseGodsIntegration _integration;

        private ReplicationReceiver _receiver;
        private BossPresentation? _presentation;
        private EncounterId? _encounter;
        private int _presentedEvents;
        private bool _waitingForCameraLogged;

        public ClientBossController(ILogger logger, IFalseGodsIntegration integration)
        {
            _logger = logger;
            _integration = integration ?? throw new ArgumentNullException(nameof(integration));
            _receiver = new ReplicationReceiver(integration.Channel);
            _logger?.Log("Client boss composition ready: listening for the host's encounter stream.");
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
        /// Advance one frame: raise the puppet when the host's state first arrives, replay newly-applied wire
        /// events as presentation cues, apply the latest snapshot state, and render.
        /// </summary>
        public void Tick(float deltaSeconds)
        {
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

            var events = _receiver.AppliedBossEvents;
            for (; _presentedEvents < events.Count; _presentedEvents++)
            {
                _presentation!.Handle(WirePresentationMapping.ToEvent(snapshot.Boss, events[_presentedEvents]));
            }

            _presentation!.Apply(WirePresentationMapping.ToState(snapshot));
            _presentation.Render(deltaSeconds);
        }

        /// <summary>Tear the puppet and the receiver down; nothing from the encounter remains in the level.</summary>
        public void Dispose()
        {
            _receiver.Dispose();
            _presentation?.Dispose();
            _presentation = null;
            _logger?.Log("Client boss composition torn down; nothing remains.");
        }

        private bool TryRaisePresentation(float x, float z, EncounterId encounter)
        {
            var camera = Camera.main;
            if (camera == null)
            {
                if (!_waitingForCameraLogged)
                {
                    _waitingForCameraLogged = true;
                    _logger?.LogWarning("Host boss state arrived but there is no local camera yet; waiting for a level.");
                }

                return false;
            }

            // The floor height is a local presentation concern (the authoritative position is x/z only), derived
            // the same way the host derives its own: the local viewer's eye height.
            var floorY = camera.transform.position.y - SinglePlayerBossController.EyeToFootDrop;
            _presentation = new BossPresentation(_logger, new Vector3(x, floorY, z));
            _encounter = encounter;
            _presentedEvents = 0;
            _waitingForCameraLogged = false;
            _logger?.Log($"Client boss puppet raised for {encounter} at ({x:0.0}, {floorY:0.0}, {z:0.0}); host-driven.");
            return true;
        }

        private void ResetForNewEncounter(EncounterId next)
        {
            _logger?.Log($"Host started {next}; discarding the previous encounter's stream and visuals.");
            _receiver.Dispose();
            _receiver = new ReplicationReceiver(_integration.Channel);
            _presentation?.Dispose();
            _presentation = null;
            _encounter = null;
            _presentedEvents = 0;
        }
    }
}
