using System;
using System.IO;
using System.Text;

namespace FalseGods.Protocol.Wire
{
    /// <summary>
    /// A little-endian binary writer for the replication wire format (Docs/MultiplayerLoadingContract.md §5.10).
    /// </summary>
    /// <remarks>
    /// Dumb byte I/O: it knows primitives, not DTOs — <see cref="WireCodec"/> knows the DTO layouts. Endianness is
    /// forced little-endian regardless of host architecture, so a payload is byte-identical everywhere. Unlike the
    /// arena content hash's <c>CanonicalEncoder</c>, wire floats are stored as raw IEEE-754 and are <b>not</b>
    /// quantised: replication only needs round-trip fidelity (positions are interpolated), never cross-machine
    /// bit-identity (ADR-003).
    /// </remarks>
    public sealed class WireWriter
    {
        private readonly MemoryStream _stream = new MemoryStream();

        public void WriteBool(bool value) => _stream.WriteByte(value ? (byte)1 : (byte)0);

        public void WriteInt32(int value)
        {
            _stream.WriteByte((byte)value);
            _stream.WriteByte((byte)(value >> 8));
            _stream.WriteByte((byte)(value >> 16));
            _stream.WriteByte((byte)(value >> 24));
        }

        public void WriteInt64(long value)
        {
            for (var i = 0; i < 8; i++)
            {
                _stream.WriteByte((byte)(value >> (8 * i)));
            }
        }

        /// <summary>Writes a 32-bit float as raw IEEE-754, little-endian on every architecture.</summary>
        public void WriteSingle(float value)
        {
            var bytes = BitConverter.GetBytes(value);
            if (!BitConverter.IsLittleEndian)
            {
                Array.Reverse(bytes);
            }

            _stream.Write(bytes, 0, bytes.Length);
        }

        /// <summary>Writes a required UTF-8 string prefixed by its byte length (int32). Rejects null.</summary>
        public void WriteString(string value)
        {
            if (value is null)
            {
                throw new ArgumentNullException(nameof(value), "A required wire string reached the writer as null.");
            }

            var bytes = Encoding.UTF8.GetBytes(value);
            WriteInt32(bytes.Length);
            _stream.Write(bytes, 0, bytes.Length);
        }

        /// <summary>Writes raw bytes with no length prefix (the caller and reader agree on the fixed count).</summary>
        public void WriteBytes(byte[] value)
        {
            if (value is null)
            {
                throw new ArgumentNullException(nameof(value));
            }

            _stream.Write(value, 0, value.Length);
        }

        public byte[] ToArray() => _stream.ToArray();
    }
}
