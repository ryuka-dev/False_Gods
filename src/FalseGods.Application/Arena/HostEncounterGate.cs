using System;
using FalseGods.Application.ReadyGate;
using FalseGods.Application.Replication;
using FalseGods.Core.Simulation;
using FalseGods.Protocol.Arena;
using FalseGods.Protocol.Wire;
using FalseGods.RuntimeContracts.Multiplayer;
using FalseGods.RuntimeContracts.Transport;

namespace FalseGods.Application.Arena
{
    /// <summary>
    /// The host's gate choreography for one encounter (Docs/MultiplayerLoadingContract.md §5.3 steps 1/4/5,
    /// §5.3.1): broadcast <c>EnterArena</c>, collect each peer's <c>ArenaReady</c> / <c>ArenaLoadFailed</c> into
    /// the fail-closed <see cref="EncounterReadyGate"/>, time out silent peers, and broadcast one
    /// <c>EncounterAborted</c> if the gate fails.
    /// </summary>
    /// <remarks>
    /// A thin driver around the gate: the gate owns the validation rules; this class owns the wire choreography
    /// and the timeout clock. Inbound payloads are untrusted — a message that does not decode, is not for this
    /// encounter, or is a replication kind is ignored (the <see cref="ReplicationReceiver"/> owns those); the
    /// gate itself rejects non-members. The <b>sender id</b> is the channel's authenticated peer, never anything
    /// read from the payload. The abort broadcast happens exactly once, whichever path (mismatch, load failure,
    /// timeout) aborted the gate. The caller polls <see cref="Status"/> after <see cref="Tick"/> and starts the
    /// encounter only on <see cref="GateStatus.Resolved"/> — there is no "start anyway".
    /// </remarks>
    public sealed class HostEncounterGate : IDisposable
    {
        private readonly IEncounterChannel _channel;
        private readonly IMultiplayerSession _session;
        private readonly ReplicationSender _sender;
        private readonly EncounterReadyGate _gate;
        private readonly EncounterId _encounter;
        private readonly ArenaManifest _hostManifest;
        private readonly WorldPosition _origin;
        private readonly float _timeoutSeconds;

        private float _elapsedSeconds;
        private bool _opened;
        private bool _abortBroadcast;

        public HostEncounterGate(
            IEncounterChannel channel,
            IMultiplayerSession session,
            IPlayerRoster roster,
            ReplicationSender sender,
            EncounterId encounter,
            ArenaManifest hostManifest,
            WorldPosition origin,
            float timeoutSeconds)
        {
            _channel = channel ?? throw new ArgumentNullException(nameof(channel));
            _session = session ?? throw new ArgumentNullException(nameof(session));
            _sender = sender ?? throw new ArgumentNullException(nameof(sender));
            _hostManifest = hostManifest ?? throw new ArgumentNullException(nameof(hostManifest));
            _gate = new EncounterReadyGate(hostManifest, roster);
            _encounter = encounter;
            _origin = origin;
            _timeoutSeconds = timeoutSeconds;
        }

        public GateStatus Status => _gate.Status;

        public GateAbortReason AbortReason => _gate.AbortReason;

        /// <summary>
        /// Step 1: announce the arena to every client and record the host's own readiness (the host has already
        /// realized and validated locally — its manifest <i>is</i> the reference). Call once.
        /// </summary>
        public void Open()
        {
            if (_opened)
            {
                throw new InvalidOperationException("The gate is already open; one gate drives one encounter.");
            }

            _opened = true;
            _channel.Received += OnReceived;
            _sender.BroadcastEnterArena(new EnterArena(_encounter, _hostManifest, _origin));
            _gate.SubmitReady(_session.LocalPeer, _hostManifest);
        }

        /// <summary>
        /// Advance the timeout clock and broadcast the abort if the gate has failed. Call once per frame while
        /// waiting; harmless afterwards.
        /// </summary>
        public void Tick(float deltaSeconds)
        {
            if (!_opened)
            {
                return;
            }

            if (_gate.Status == GateStatus.Waiting)
            {
                _elapsedSeconds += deltaSeconds;
                if (_elapsedSeconds >= _timeoutSeconds)
                {
                    _gate.OnTimeout();
                }
            }

            if (_gate.Status == GateStatus.Aborted && !_abortBroadcast)
            {
                _abortBroadcast = true;
                _sender.BroadcastAborted(new EncounterAborted(_encounter, ToWireReason(_gate.AbortReason)));
            }
        }

        /// <summary>Required members that have not reported ready yet (diagnostic, e.g. for the timeout log).</summary>
        public string DescribeOutstanding() => string.Join(", ", _gate.Outstanding);

        public void Dispose() => _channel.Received -= OnReceived;

        private void OnReceived(SessionPeerId sender, EncodedPayload payload)
        {
            DecodedMessage message;
            try
            {
                message = EncounterCodec.Decode(payload);
            }
            catch (Exception)
            {
                return; // untrusted input: undecodable traffic is not the gate's problem
            }

            switch (message.Value)
            {
                case ArenaReady ready when ready.Encounter == _encounter:
                    _gate.SubmitReady(sender, ready.Manifest);
                    break;
                case ArenaLoadFailed failed when failed.Encounter == _encounter:
                    _gate.SubmitLoadFailed(sender);
                    break;
            }
        }

        private static EncounterAbortReason ToWireReason(GateAbortReason reason)
        {
            switch (reason)
            {
                case GateAbortReason.ContentHashSchemaMismatch:
                    return EncounterAbortReason.ContentHashSchemaMismatch;
                case GateAbortReason.VersionMismatch:
                    return EncounterAbortReason.VersionMismatch;
                case GateAbortReason.ContentMismatch:
                    return EncounterAbortReason.ContentMismatch;
                case GateAbortReason.LoadFailed:
                    return EncounterAbortReason.LoadFailed;
                case GateAbortReason.Timeout:
                    return EncounterAbortReason.Timeout;
                default:
                    return EncounterAbortReason.Unspecified;
            }
        }
    }
}
