using System;

namespace FalseGods.Protocol.Arena
{
    /// <summary>
    /// Thrown when authored arena content cannot produce a canonical, reproducible hash — the runtime
    /// equivalent of the "build-time export failure" the canonical definition demands
    /// (Docs/MultiplayerLoadingContract.md §5.2.1, Docs/OriginalContentPipeline.md §8.3).
    /// </summary>
    /// <remarks>
    /// The failing cases: a <c>NaN</c> or infinite float anywhere in the authored data, a zero-length
    /// quaternion, a <see cref="StableMarkerId"/> that was never assigned, and a duplicate
    /// <see cref="StableMarkerId"/>. Each would otherwise yield a hash that is not reproducible across machines
    /// (or is silently ambiguous), so hashing refuses loudly rather than emitting one.
    /// </remarks>
    public sealed class ArenaContentExportException : Exception
    {
        public ArenaContentExportException(string message)
            : base(message)
        {
        }
    }
}
