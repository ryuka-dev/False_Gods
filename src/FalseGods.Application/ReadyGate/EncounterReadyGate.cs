using System;
using System.Collections.Generic;
using System.Linq;
using FalseGods.Protocol.Arena;
using FalseGods.RuntimeContracts.Multiplayer;
using FalseGods.RuntimeContracts.Transport;

namespace FalseGods.Application.ReadyGate
{
    /// <summary>Whether the gate is still waiting, has passed, or has aborted.</summary>
    public enum GateStatus
    {
        Waiting = 0,
        Resolved = 1,
        Aborted = 2,
    }

    /// <summary>Why the gate aborted (fail-closed); <see cref="None"/> until it does (Docs/MultiplayerLoadingContract.md §5.3.1).</summary>
    public enum GateAbortReason
    {
        None = 0,
        ContentHashSchemaMismatch = 1,
        VersionMismatch = 2,
        ContentMismatch = 3,
        LoadFailed = 4,
        Timeout = 5,
    }

    /// <summary>
    /// The fail-closed arena ready gate (Docs/MultiplayerLoadingContract.md §5.3/§5.3.1): the boss does not start
    /// until <b>every</b> required session member has reported ready over content that matches the host's — same
    /// schema, same version, byte-identical hash.
    /// </summary>
    /// <remarks>
    /// This is the production successor to the P9 probe's <c>LocalReadyGate</c>. It lives in
    /// <c>FalseGods.Application</c> rather than behind a <c>RuntimeContracts</c> port because it compares
    /// <c>FalseGods.Protocol</c> content-hash / schema values, which <c>RuntimeContracts</c> may not reference
    /// (FG-ARCH-007). The required set is the current <see cref="IPlayerRoster"/> membership, including the host's
    /// own local peer, so single-player — a one-member roster — resolves the instant the local peer readies, on the
    /// identical code path as multiplayer (§5.3).
    ///
    /// <para>
    /// Every rule here fails closed. A <c>ContentHashSchemaVersion</c> mismatch aborts <b>without comparing the
    /// hashes at all</b> (they mean different things); a matching schema with a different hash is
    /// <see cref="GateAbortReason.ContentMismatch"/>; a load failure or a timeout aborts. There is no
    /// "start anyway". An abort is sticky. A ready from a peer that is not a required member is rejected, never
    /// admitted — possession of an identifier is not membership (Docs/DependencyRules.md §12).
    /// </para>
    /// </remarks>
    public sealed class EncounterReadyGate
    {
        private readonly ArenaManifest _hostManifest;
        private readonly IPlayerRoster _roster;
        private readonly HashSet<SessionPeerId> _ready = new HashSet<SessionPeerId>();

        public EncounterReadyGate(ArenaManifest hostManifest, IPlayerRoster roster)
        {
            _hostManifest = hostManifest ?? throw new ArgumentNullException(nameof(hostManifest));
            _roster = roster ?? throw new ArgumentNullException(nameof(roster));
        }

        /// <summary>The current gate status, re-evaluated against the live roster.</summary>
        public GateStatus Status
        {
            get
            {
                if (AbortReason != GateAbortReason.None)
                {
                    return GateStatus.Aborted;
                }

                return AllRequiredReady() ? GateStatus.Resolved : GateStatus.Waiting;
            }
        }

        /// <summary>The abort reason, or <see cref="GateAbortReason.None"/> while waiting or resolved.</summary>
        public GateAbortReason AbortReason { get; private set; } = GateAbortReason.None;

        /// <summary>Required members that have not yet reported ready.</summary>
        public IReadOnlyList<SessionPeerId> Outstanding =>
            _roster.Members.Where(m => !_ready.Contains(m)).ToList();

        /// <summary>
        /// Record <paramref name="peer"/>'s readiness over <paramref name="report"/>, validating it against the
        /// host's manifest. Returns the resulting status. A non-member is rejected (no state change); a mismatch
        /// aborts fail-closed.
        /// </summary>
        public GateStatus SubmitReady(SessionPeerId peer, ArenaManifest report)
        {
            if (report is null)
            {
                throw new ArgumentNullException(nameof(report));
            }

            if (AbortReason != GateAbortReason.None)
            {
                return GateStatus.Aborted; // sticky
            }

            if (!_roster.Members.Contains(peer))
            {
                return Status; // untrusted: not a required member, not admitted
            }

            // Schema first, and a schema mismatch is refused WITHOUT comparing the hashes (§5.2.1/§5.3.1).
            if (report.ContentHashSchemaVersion != _hostManifest.ContentHashSchemaVersion)
            {
                return Abort(GateAbortReason.ContentHashSchemaMismatch);
            }

            if (!string.Equals(report.ArenaId, _hostManifest.ArenaId, StringComparison.Ordinal)
                || report.ArenaVersion != _hostManifest.ArenaVersion
                || report.ProtocolVersion != _hostManifest.ProtocolVersion
                || !string.Equals(report.BundleVersion, _hostManifest.BundleVersion, StringComparison.Ordinal))
            {
                return Abort(GateAbortReason.VersionMismatch);
            }

            if (report.ContentHash != _hostManifest.ContentHash)
            {
                return Abort(GateAbortReason.ContentMismatch);
            }

            _ready.Add(peer);
            return Status;
        }

        /// <summary>A required peer failed to load — abort the encounter (§5.3.1). No boss starts.</summary>
        public GateStatus SubmitLoadFailed(SessionPeerId peer)
        {
            if (AbortReason != GateAbortReason.None)
            {
                return GateStatus.Aborted;
            }

            if (!_roster.Members.Contains(peer))
            {
                return Status;
            }

            return Abort(GateAbortReason.LoadFailed);
        }

        /// <summary>A required peer never answered — abort rather than start with a partial roster (§5.3.1).</summary>
        public GateStatus OnTimeout()
        {
            if (Status == GateStatus.Waiting)
            {
                return Abort(GateAbortReason.Timeout);
            }

            return Status;
        }

        private bool AllRequiredReady()
        {
            var members = _roster.Members;
            if (members.Count == 0)
            {
                return false; // fail-closed: an empty required set never resolves
            }

            for (var i = 0; i < members.Count; i++)
            {
                if (!_ready.Contains(members[i]))
                {
                    return false;
                }
            }

            return true;
        }

        private GateStatus Abort(GateAbortReason reason)
        {
            AbortReason = reason;
            return GateStatus.Aborted;
        }
    }
}
