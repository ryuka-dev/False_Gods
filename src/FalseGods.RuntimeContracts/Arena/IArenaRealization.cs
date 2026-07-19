using System;
using System.Collections.Generic;

namespace FalseGods.RuntimeContracts.Arena
{
    /// <summary>One realized hierarchy node, reported by path with its runtime <b>local</b> transform so the
    /// flow can verify it against the authored parity map (RiskList R14). Local, not world: the check is about
    /// hierarchy shape, not placement.</summary>
    public sealed record RealizedParityNode(
        string Path,
        ArenaWorldPoint LocalPosition,
        ArenaRotation LocalRotation,
        ArenaWorldPoint LocalScale);

    /// <summary>A gameplay marker resolved to its realized <b>world</b> position (spawn points, exits).</summary>
    public sealed record RealizedMarker(string Path, ArenaWorldPoint WorldPosition);

    /// <summary>The outcome of realizing the arena: the parity nodes and markers that were found, or a
    /// diagnostic error. A requested path that was not found is simply absent from the lists — the flow treats
    /// absence as a parity/marker failure (fail closed).</summary>
    public sealed record ArenaRealizationResult(
        bool Success,
        string? Error,
        IReadOnlyList<RealizedParityNode> ParityNodes,
        IReadOnlyList<RealizedMarker> Markers)
    {
        public static ArenaRealizationResult Failed(string error) => new ArenaRealizationResult(
            false, error, Array.Empty<RealizedParityNode>(), Array.Empty<RealizedMarker>());
    }

    /// <summary>
    /// Realizes the authored arena prefab in the world at a given origin, reports the realized hierarchy for
    /// parity checking, and tears it down (Docs/ArenaLoadingProposal.md §2.4).
    /// </summary>
    /// <remarks>
    /// Implemented in <c>FalseGods.UnityRuntime</c> alongside <see cref="IArenaAssetProvider"/> (which holds the
    /// loaded prefab); declared here for the same reference-graph reason documented on that port. The arena is
    /// always realized with identity rotation — the navigation tile grid is axis-aligned (see
    /// <c>EnterArena</c>'s remarks in the wire contract).
    /// <para>
    /// <see cref="Realize"/> requires <see cref="IArenaAssetProvider.Load"/> to have succeeded, may be called
    /// once per load, and on failure leaves nothing in the world. <see cref="Teardown"/> destroys everything
    /// realized and is idempotent. The implementation makes no gameplay decision and applies no navigation —
    /// that is <c>INavigationPort</c>'s job.
    /// </para>
    /// </remarks>
    public interface IArenaRealization
    {
        ArenaRealizationResult Realize(
            ArenaWorldPoint origin,
            IReadOnlyList<string> parityPaths,
            IReadOnlyList<string> markerPaths);

        void Teardown();
    }
}
