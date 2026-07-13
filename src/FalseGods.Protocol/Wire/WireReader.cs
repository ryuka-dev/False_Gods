using System;
using System.IO;
using System.Text;

namespace FalseGods.Protocol.Wire
{
    /// <summary>
    /// The little-endian reader mirroring <see cref="WireWriter"/>. Reads a payload back into primitives, throwing
    /// on a truncated or malformed buffer rather than reading past the end.
    /// </summary>
    /// <remarks>
    /// Every read is bounds-checked, because a wire payload is untrusted input (Docs/DependencyRules.md §12): a
    /// short buffer or an absurd length prefix throws <see cref="EndOfStreamException"/> instead of corrupting or
    /// over-reading. <see cref="AtEnd"/> lets a codec assert it consumed exactly the whole payload.
    /// </remarks>
    public sealed class WireReader
    {
        private readonly byte[] _buffer;
        private int _position;

        public WireReader(byte[] buffer)
        {
            _buffer = buffer ?? throw new ArgumentNullException(nameof(buffer));
        }

        /// <summary>True once every byte has been consumed.</summary>
        public bool AtEnd => _position == _buffer.Length;

        public bool ReadBool() => ReadRawByte() != 0;

        public int ReadInt32()
        {
            var b0 = ReadRawByte();
            var b1 = ReadRawByte();
            var b2 = ReadRawByte();
            var b3 = ReadRawByte();
            return b0 | (b1 << 8) | (b2 << 16) | (b3 << 24);
        }

        public long ReadInt64()
        {
            long value = 0;
            for (var i = 0; i < 8; i++)
            {
                value |= (long)ReadRawByte() << (8 * i);
            }

            return value;
        }

        public float ReadSingle()
        {
            var bytes = ReadBytes(4);
            if (!BitConverter.IsLittleEndian)
            {
                Array.Reverse(bytes);
            }

            return BitConverter.ToSingle(bytes, 0);
        }

        public string ReadString()
        {
            var length = ReadInt32();
            if (length < 0)
            {
                throw new EndOfStreamException($"Wire string length {length} is negative.");
            }

            var bytes = ReadBytes(length);
            return Encoding.UTF8.GetString(bytes);
        }

        public byte[] ReadBytes(int count)
        {
            if (count < 0)
            {
                throw new EndOfStreamException($"Requested {count} bytes.");
            }

            if (_position + count > _buffer.Length)
            {
                throw new EndOfStreamException(
                    $"Wire payload is truncated: needed {count} bytes at offset {_position} of {_buffer.Length}.");
            }

            var result = new byte[count];
            Array.Copy(_buffer, _position, result, 0, count);
            _position += count;
            return result;
        }

        private byte ReadRawByte()
        {
            if (_position >= _buffer.Length)
            {
                throw new EndOfStreamException(
                    $"Wire payload is truncated: read past the end at offset {_position} of {_buffer.Length}.");
            }

            return _buffer[_position++];
        }
    }
}
