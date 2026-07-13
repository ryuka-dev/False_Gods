namespace FalseGods.RuntimeContracts.Transport
{
    /// <summary>Which peers an <see cref="EncodedPayload"/> is addressed to.</summary>
    public enum MessageTargetKind
    {
        /// <summary>The host (a client sending upstream, e.g. an <c>ArenaReady</c>).</summary>
        Host = 0,

        /// <summary>Every connected client (the host broadcasting snapshots/events).</summary>
        AllClients = 1,

        /// <summary>One specific peer (e.g. a per-join <c>EncounterBaseline</c>).</summary>
        SpecificPeer = 2,
    }

    /// <summary>
    /// The addressee of a send over an <see cref="Multiplayer.IEncounterChannel"/> — host, all clients, or one
    /// peer — mirroring the bridge's own target options without naming a transport type.
    /// </summary>
    public readonly struct MessageTarget
    {
        private MessageTarget(MessageTargetKind kind, SessionPeerId peer)
        {
            Kind = kind;
            Peer = peer;
        }

        public MessageTargetKind Kind { get; }

        /// <summary>The specific peer, meaningful only when <see cref="Kind"/> is <see cref="MessageTargetKind.SpecificPeer"/>.</summary>
        public SessionPeerId Peer { get; }

        public static MessageTarget Host { get; } = new MessageTarget(MessageTargetKind.Host, default);

        public static MessageTarget AllClients { get; } = new MessageTarget(MessageTargetKind.AllClients, default);

        public static MessageTarget ToPeer(SessionPeerId peer) => new MessageTarget(MessageTargetKind.SpecificPeer, peer);

        public override string ToString() =>
            Kind == MessageTargetKind.SpecificPeer ? $"{Kind}({Peer})" : Kind.ToString();
    }
}
