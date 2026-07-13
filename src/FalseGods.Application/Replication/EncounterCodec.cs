using System;
using System.IO;
using FalseGods.Protocol.Wire;
using FalseGods.RuntimeContracts.Transport;

namespace FalseGods.Application.Replication
{
    /// <summary>Which replication DTO an <see cref="EncodedPayload"/> carries.</summary>
    public enum ReplicationKind : byte
    {
        BossSnapshot = 1,
        ArenaSnapshot = 2,
        BossEvent = 3,
        ArenaEvent = 4,
        EncounterBaseline = 5,
    }

    /// <summary>One decoded replication message: its <see cref="Kind"/> and the deserialized DTO as <see cref="Value"/>.</summary>
    public readonly struct DecodedMessage
    {
        public DecodedMessage(ReplicationKind kind, object value)
        {
            Kind = kind;
            Value = value;
        }

        public ReplicationKind Kind { get; }

        /// <summary>The DTO — a <c>BossSnapshot</c>, <c>ArenaSnapshot</c>, <c>IBossWireEvent</c>, <c>IArenaWireEvent</c>, or <c>EncounterBaseline</c>.</summary>
        public object Value { get; }
    }

    /// <summary>
    /// Bridges the two halves of the message strategy (Docs/MultiplayerLoadingContract.md §5.10): it wraps the
    /// <c>byte[]</c> a <see cref="WireCodec"/> produces into an <see cref="EncodedPayload"/> tagged with a
    /// <see cref="ReplicationKind"/>, and decodes it back. All False Gods messages ride one opaque channel,
    /// disambiguated by this leading kind byte inside the payload — ST never gains a per-message id or codec.
    /// </summary>
    /// <remarks>
    /// This is the one place both vocabularies meet: <c>FalseGods.Protocol</c>'s wire DTOs and
    /// <c>FalseGods.RuntimeContracts</c>'s opaque carrier. <c>FalseGods.Protocol</c> cannot reference the carrier
    /// (FG-ARCH-007), so the wrapping must live here in <c>FalseGods.Application</c>.
    /// </remarks>
    public static class EncounterCodec
    {
        public static EncodedPayload Encode(BossSnapshot snapshot) => Wrap(ReplicationKind.BossSnapshot, WireCodec.Serialize(snapshot));

        public static EncodedPayload Encode(ArenaSnapshot snapshot) => Wrap(ReplicationKind.ArenaSnapshot, WireCodec.Serialize(snapshot));

        public static EncodedPayload Encode(IBossWireEvent bossEvent) => Wrap(ReplicationKind.BossEvent, WireCodec.Serialize(bossEvent));

        public static EncodedPayload Encode(IArenaWireEvent arenaEvent) => Wrap(ReplicationKind.ArenaEvent, WireCodec.Serialize(arenaEvent));

        public static EncodedPayload Encode(EncounterBaseline baseline) => Wrap(ReplicationKind.EncounterBaseline, WireCodec.Serialize(baseline));

        /// <summary>Decode an opaque payload back into its DTO. Treats the payload as untrusted input.</summary>
        public static DecodedMessage Decode(EncodedPayload payload)
        {
            var bytes = payload.ToArray();
            if (bytes.Length == 0)
            {
                throw new InvalidDataException("Empty replication payload: no kind tag.");
            }

            var kind = (ReplicationKind)bytes[0];
            var body = new byte[bytes.Length - 1];
            Array.Copy(bytes, 1, body, 0, body.Length);

            switch (kind)
            {
                case ReplicationKind.BossSnapshot:
                    return new DecodedMessage(kind, WireCodec.DeserializeBossSnapshot(body));
                case ReplicationKind.ArenaSnapshot:
                    return new DecodedMessage(kind, WireCodec.DeserializeArenaSnapshot(body));
                case ReplicationKind.BossEvent:
                    return new DecodedMessage(kind, WireCodec.DeserializeBossEvent(body));
                case ReplicationKind.ArenaEvent:
                    return new DecodedMessage(kind, WireCodec.DeserializeArenaEvent(body));
                case ReplicationKind.EncounterBaseline:
                    return new DecodedMessage(kind, WireCodec.DeserializeBaseline(body));
                default:
                    throw new InvalidDataException($"Unknown replication kind tag {bytes[0]}.");
            }
        }

        private static EncodedPayload Wrap(ReplicationKind kind, byte[] wireBytes)
        {
            var framed = new byte[wireBytes.Length + 1];
            framed[0] = (byte)kind;
            Array.Copy(wireBytes, 0, framed, 1, wireBytes.Length);
            return new EncodedPayload(framed);
        }
    }
}
