using System;
using System.Collections.Generic;
using FalseGods.RuntimeContracts.Multiplayer;
using FalseGods.RuntimeContracts.Transport;

namespace FalseGods.ApplicationTests
{
    /// <summary>A channel that records what was sent and lets a test deliver an inbound payload.</summary>
    internal sealed class FakeChannel : IEncounterChannel
    {
        public List<(EncodedPayload Payload, MessageDelivery Delivery, MessageTarget Target)> Sent { get; } =
            new List<(EncodedPayload, MessageDelivery, MessageTarget)>();

        public event Action<SessionPeerId, EncodedPayload>? Received;

        public void Send(EncodedPayload payload, MessageDelivery delivery, MessageTarget target) =>
            Sent.Add((payload, delivery, target));

        /// <summary>Deliver an inbound payload to subscribers, as the real channel would on the main thread.</summary>
        public void Deliver(SessionPeerId sender, EncodedPayload payload) => Received?.Invoke(sender, payload);
    }

    /// <summary>A session whose role and local peer the test sets.</summary>
    internal sealed class FakeSession : IMultiplayerSession
    {
        public FakeSession(SessionRole role, int localPeer = 0)
        {
            Role = role;
            LocalPeer = new SessionPeerId(localPeer);
        }

        public SessionRole Role { get; }

        public bool IsActive => true;

        public SessionPeerId LocalPeer { get; }
    }

    /// <summary>A roster whose membership the test controls.</summary>
    internal sealed class FakeRoster : IPlayerRoster
    {
        private readonly List<SessionPeerId> _members = new List<SessionPeerId>();

        public FakeRoster(params int[] members)
        {
            foreach (var m in members)
            {
                _members.Add(new SessionPeerId(m));
            }
        }

        public IReadOnlyList<SessionPeerId> Members => _members;

        public void Remove(int member) => _members.Remove(new SessionPeerId(member));
    }
}
