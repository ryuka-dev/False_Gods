using System;

namespace FalseGods.RuntimeContracts.Transport
{
    /// <summary>
    /// An opaque, already-serialized message body crossing the transport boundary.
    /// </summary>
    /// <remarks>
    /// <c>FalseGods.Application</c> serializes a <c>FalseGods.Protocol</c> DTO into these bytes and hands the
    /// payload to an <see cref="Multiplayer.IEncounterChannel"/>; the adapter ships it as one opaque blob and never
    /// sees a DTO (Docs/MultiplayerLoadingContract.md §5.10). This carrier lives in <c>FalseGods.RuntimeContracts</c>
    /// precisely so it carries no <c>FalseGods.Protocol</c> reference (FG-ARCH-007): the bytes are opaque here. The
    /// buffer is copied in and out so the value is effectively immutable.
    /// </remarks>
    public readonly struct EncodedPayload
    {
        private readonly byte[]? _bytes;

        public EncodedPayload(byte[] bytes)
        {
            if (bytes is null)
            {
                throw new ArgumentNullException(nameof(bytes));
            }

            _bytes = (byte[])bytes.Clone();
        }

        /// <summary>The payload length in bytes.</summary>
        public int Length => _bytes?.Length ?? 0;

        /// <summary>A copy of the raw bytes.</summary>
        public byte[] ToArray() => (byte[])(_bytes ?? Array.Empty<byte>()).Clone();
    }
}
