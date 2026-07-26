using System;
using System.Collections.Generic;
using FalseGods.Application.Replication;
using FalseGods.Protocol.Wire;
using FalseGods.RuntimeContracts.Multiplayer;
using FalseGods.RuntimeContracts.Transport;

namespace FalseGods.Application.Arena
{
    /// <summary>
    /// The session's agreement on which level is a boss arena: the host declares it, every peer applies the
    /// declaration, and a peer that wants the session taken there asks the host rather than going alone.
    /// </summary>
    /// <remarks>
    /// <para><b>Why this exists.</b> A boss arena delivered by replacing a generated level (Strategy A) is built
    /// independently on every peer, and most generation runs on a machine were asked for by somebody else — the
    /// host leads transitions and the others follow. A peer that does not know the level is a boss arena builds
    /// the game's own content instead, and the session ends up in two different rooms. This carries that one fact
    /// across, ahead of any transition, so every peer's generation agrees.</para>
    /// <para><b>Authority.</b> The host owns it, like every other level and world decision (SULFUR Together
    /// invariant 1). A client's <see cref="Request"/> is exactly that — the host declares, broadcasts, and leads,
    /// or does nothing. This mirrors the session layer's own handling of a client-initiated transition.</para>
    /// <para><b>Standing, not consumed.</b> The declaration outlives any one transition; it is re-sent to each
    /// peer that joins afterwards (<see cref="Tick"/>), so a late joiner is never the only peer that does not
    /// know. A peer that leaves is forgotten, so a rejoin is told again.</para>
    /// <para><b>Untrusted input</b> (Docs/DependencyRules.md §12): a declaration is accepted only from the
    /// session host, a request only by the host and only from a current member. The sender is the channel's
    /// authenticated peer, never read from the payload. Traffic that does not decode, and every other message
    /// kind, is ignored — the encounter flows own those. Callbacks fire on the channel's delivery thread (the
    /// game's main thread, per the channel contract).</para>
    /// </remarks>
    public sealed class ArenaLevelFlow : IDisposable
    {
        private readonly IEncounterChannel _channel;
        private readonly IMultiplayerSession _session;
        private readonly IPlayerRoster _roster;
        private readonly HashSet<SessionPeerId> _informedPeers = new HashSet<SessionPeerId>();

        private ArenaLevelDeclared? _declaration;

        public ArenaLevelFlow(IEncounterChannel channel, IMultiplayerSession session, IPlayerRoster roster)
        {
            _channel = channel ?? throw new ArgumentNullException(nameof(channel));
            _session = session ?? throw new ArgumentNullException(nameof(session));
            _roster = roster ?? throw new ArgumentNullException(nameof(roster));
            _channel.Received += OnReceived;
        }

        /// <summary>The host declared a level to be (or to no longer be) a boss arena — apply it locally. Raised
        /// on every peer including the host, so there is one place that applies a declaration.</summary>
        public Action<ArenaLevelDeclared>? OnDeclared { get; set; }

        /// <summary>Host only: a session member asked for the boss arena. The host decides what to do with it —
        /// typically declare and lead the transition.</summary>
        public Action<ArenaLevelRequested>? OnRequested { get; set; }

        /// <summary>The declaration currently in force, or null while none has been made.</summary>
        public ArenaLevelDeclared? Declaration => _declaration;

        public void Dispose() => _channel.Received -= OnReceived;

        /// <summary>
        /// Host: declare <paramref name="level"/> a boss arena (or, with <paramref name="isBossArena"/> false,
        /// withdraw it), apply it locally, and tell every client. A client calling this does nothing — it has
        /// nothing to declare; it asks with <see cref="Request"/>.
        /// </summary>
        public void Declare(ArenaLevelId level, bool isBossArena = true)
        {
            if (!IsHosting)
            {
                return;
            }

            var declaration = new ArenaLevelDeclared(level, isBossArena);
            _declaration = declaration;

            // Everyone is told again, including peers that already held the previous declaration: this changes
            // what a level IS, and a peer still holding the old answer would build the wrong room.
            _informedPeers.Clear();
            OnDeclared?.Invoke(declaration);
            Broadcast(declaration);
        }

        /// <summary>Client: ask the host to take the session to the boss arena. On the host this is a no-op —
        /// it declares directly.</summary>
        public void Request(ArenaLevelId level)
        {
            if (!_session.IsActive || _session.Role == SessionRole.Host)
            {
                return;
            }

            _channel.Send(
                EncounterCodec.Encode(new ArenaLevelRequested(level)),
                MessageDelivery.ReliableOrdered,
                MessageTarget.Host);
        }

        /// <summary>
        /// Host: tell peers that joined since the declaration was made. Cheap and idempotent — call it from the
        /// composition's frame loop.
        /// </summary>
        public void Tick()
        {
            var declaration = _declaration;
            if (declaration is null || !IsHosting)
            {
                return;
            }

            var members = _roster.Members;

            // Forget peers that left, so a rejoin under the same id is told again.
            _informedPeers.RemoveWhere(peer => !Contains(members, peer));

            for (var i = 0; i < members.Count; i++)
            {
                var peer = members[i];
                if (peer == _session.LocalPeer || _informedPeers.Contains(peer))
                {
                    continue;
                }

                _channel.Send(
                    EncounterCodec.Encode(declaration), MessageDelivery.ReliableOrdered, MessageTarget.ToPeer(peer));
                _informedPeers.Add(peer);
            }
        }

        private bool IsHosting => _session.IsActive && _session.Role == SessionRole.Host;

        private void Broadcast(ArenaLevelDeclared declaration) =>
            _channel.Send(
                EncounterCodec.Encode(declaration), MessageDelivery.ReliableOrdered, MessageTarget.AllClients);

        private void OnReceived(SessionPeerId sender, EncodedPayload payload)
        {
            DecodedMessage message;
            try
            {
                message = EncounterCodec.Decode(payload);
            }
            catch (Exception)
            {
                return;
            }

            switch (message.Value)
            {
                case ArenaLevelDeclared declared when sender == _session.HostPeer && !IsHosting:
                    _declaration = declared;
                    OnDeclared?.Invoke(declared);
                    break;
                case ArenaLevelRequested requested when IsHosting && IsMember(sender):
                    OnRequested?.Invoke(requested);
                    break;
            }
        }

        private bool IsMember(SessionPeerId peer) => Contains(_roster.Members, peer);

        private static bool Contains(IReadOnlyList<SessionPeerId> peers, SessionPeerId peer)
        {
            for (var i = 0; i < peers.Count; i++)
            {
                if (peers[i] == peer)
                {
                    return true;
                }
            }

            return false;
        }
    }
}
