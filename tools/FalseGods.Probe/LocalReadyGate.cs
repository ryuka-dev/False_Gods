using System;
using System.Collections.Generic;
using System.Linq;

namespace FalseGods.Probe
{
    /// <summary>
    /// A throwaway, in-probe stand-in for the single-arena READY GATE of the multiplayer loading contract
    /// (Docs/MultiplayerLoadingContract.md §5.3): the session "starts" only once every REQUIRED member has
    /// signalled ready over content it has both realized and validated. It exists to prove the SEQUENCE SHAPE
    /// — load -> validate -> ready -> start — holds for a single local peer, not to be the production gate
    /// (that layer is not built yet; src/ is still a skeleton).
    ///
    /// The two properties worth proving are the fail-closed ones:
    ///   - it never resolves until the required set is fully satisfied (a partial or empty ready set does not
    ///     start the session), and
    ///   - a ready from a peer that is not in the required set is rejected, never silently admitted
    ///     (Docs/... rule: possession of an identifier is not membership — treat external input as untrusted).
    ///
    /// Single-player is the degenerate case: the required set is exactly {local}, so the gate resolves the
    /// instant the local peer readies. A two-member set models why a real session waits.
    /// </summary>
    internal sealed class LocalReadyGate
    {
        private readonly HashSet<string> _required;
        private readonly HashSet<string> _ready = new HashSet<string>(StringComparer.Ordinal);

        public LocalReadyGate(IEnumerable<string> requiredPeers)
        {
            _required = new HashSet<string>(requiredPeers ?? throw new ArgumentNullException(nameof(requiredPeers)),
                StringComparer.Ordinal);
            if (_required.Count == 0)
                throw new ArgumentException("A ready gate needs at least one required peer.", nameof(requiredPeers));
        }

        /// <summary>Fail-closed: true only when every required peer has signalled ready.</summary>
        public bool IsResolved => _required.IsSubsetOf(_ready);

        public int RequiredCount => _required.Count;
        public int ReadyCount => _ready.Count;

        /// <summary>
        /// Record that <paramref name="peer"/> is ready. Returns false — and changes nothing — when the peer is
        /// not a required member of this session (untrusted input is rejected, not admitted). A duplicate ready
        /// from a required peer is idempotent.
        /// </summary>
        public bool MarkReady(string peer)
        {
            if (peer == null || !_required.Contains(peer))
                return false;
            _ready.Add(peer);
            return true;
        }

        public string Describe() =>
            $"{ReadyCount}/{RequiredCount} required peers ready [{string.Join(", ", _required.OrderBy(p => p))}]";
    }
}
