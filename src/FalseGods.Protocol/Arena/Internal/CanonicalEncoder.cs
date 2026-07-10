using System;
using System.IO;
using System.Text;

namespace FalseGods.Protocol.Arena.Internal
{
    /// <summary>
    /// Builds the canonical byte document the content hash is taken over
    /// (Docs/MultiplayerLoadingContract.md §5.2.1): every integer little-endian, every string UTF-8 with a
    /// length prefix.
    /// </summary>
    /// <remarks>
    /// The exact byte layout is part of the hash schema — any change to it is a
    /// <see cref="ContentHashSchemaVersion"/> bump. Endianness is forced little-endian regardless of host
    /// architecture so the document is identical everywhere.
    /// </remarks>
    internal sealed class CanonicalEncoder
    {
        private readonly MemoryStream _stream = new MemoryStream();

        /// <summary>Writes a 32-bit integer, little-endian. (No <c>Span</c>: net472's BCL has none.)</summary>
        public void WriteInt32(int value)
        {
            _stream.WriteByte((byte)value);
            _stream.WriteByte((byte)(value >> 8));
            _stream.WriteByte((byte)(value >> 16));
            _stream.WriteByte((byte)(value >> 24));
        }

        /// <summary>Writes a 64-bit integer, little-endian.</summary>
        public void WriteInt64(long value)
        {
            for (var i = 0; i < 8; i++)
                _stream.WriteByte((byte)(value >> (8 * i)));
        }

        /// <summary>
        /// Writes a required UTF-8 string prefixed by its byte length (int32, little-endian). The length prefix
        /// makes concatenation unambiguous, so <c>("ab", "c")</c> can never encode the same as <c>("a", "bc")</c>.
        /// </summary>
        /// <remarks>
        /// Rejects <c>null</c>: a required field must never silently encode as empty. Callers validate required
        /// tokens up front (<see cref="ContentHashComputer"/>), so this only fires on a programming error;
        /// genuinely optional values go through <see cref="WriteOptionalString"/> / <see cref="WriteOptionalMarker"/>.
        /// </remarks>
        public void WriteString(string value)
        {
            if (value is null)
                throw new ArgumentNullException(nameof(value), "A required string reached the encoder as null; validate it before encoding.");

            var bytes = Encoding.UTF8.GetBytes(value);
            WriteInt32(bytes.Length);
            _stream.Write(bytes, 0, bytes.Length);
        }

        /// <summary>Writes a marker id in its canonical string form (length-prefixed UTF-8).</summary>
        public void WriteMarker(StableMarkerId marker) => WriteString(marker.ToCanonicalString());

        /// <summary>
        /// Writes an optional marker id. Absent is encoded as a zero-length string; since a present marker's
        /// canonical form is always the 36-character GUID text, absent and present are never confusable.
        /// </summary>
        public void WriteOptionalMarker(StableMarkerId? marker) =>
            WriteString(marker.HasValue ? marker.Value.ToCanonicalString() : string.Empty);

        /// <summary>Writes an optional string, absent encoded as a zero-length string.</summary>
        public void WriteOptionalString(string? value) => WriteString(value ?? string.Empty);

        /// <summary>Writes a quantised length component (int64).</summary>
        public void WriteLength(double value, string context) => WriteInt64(Quantizer.QuantizeLength(value, context));

        /// <summary>Writes a position (x, y, z), each quantised as a length.</summary>
        public void WriteVector(Vector3 vector, string context)
        {
            WriteLength(vector.X, context);
            WriteLength(vector.Y, context);
            WriteLength(vector.Z, context);
        }

        /// <summary>Writes a full local transform: position, canonicalised rotation (w, x, y, z), then scale.</summary>
        public void WriteTransform(AuthoredTransform transform, string context)
        {
            WriteVector(transform.Position, context);

            var (w, x, y, z) = Quantizer.QuantizeRotation(transform.Rotation, context);
            WriteInt64(w);
            WriteInt64(x);
            WriteInt64(y);
            WriteInt64(z);

            WriteVector(transform.Scale, context);
        }

        /// <summary>Writes axis-aligned bounds as centre then size, each a quantised length vector.</summary>
        public void WriteBounds(AuthoredBounds bounds, string context)
        {
            WriteVector(bounds.Center, context);
            WriteVector(bounds.Size, context);
        }

        public byte[] ToArray() => _stream.ToArray();
    }
}
