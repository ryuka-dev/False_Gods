using System;

namespace FalseGods.RuntimeContracts.Transport
{
    /// <summary>
    /// The identity of one peer in the multiplayer session, as the project sees it — a transport-neutral handle.
    /// </summary>
    /// <remarks>
    /// It is a <b>session peer</b> identity, deliberately distinct from a transport connection handle, a Steam
    /// account, a display name, or the boss domain's <c>ParticipantId</c> (Docs/DependencyRules.md §3–§4). The
    /// optional ST adapter maps its transport's peer id onto this; nothing above the adapter sees a LiteNetLib or
    /// Steamworks type. The ready gate matches an <c>ArenaReady</c>'s sender against the required members by this
    /// id.
    /// </remarks>
    public readonly struct SessionPeerId : IEquatable<SessionPeerId>
    {
        private readonly int _value;

        public SessionPeerId(int value)
        {
            _value = value;
        }

        public int Value => _value;

        public bool Equals(SessionPeerId other) => _value == other._value;

        public override bool Equals(object? obj) => obj is SessionPeerId other && Equals(other);

        public override int GetHashCode() => _value;

        public override string ToString() => $"peer:{_value}";

        public static bool operator ==(SessionPeerId left, SessionPeerId right) => left.Equals(right);

        public static bool operator !=(SessionPeerId left, SessionPeerId right) => !left.Equals(right);
    }
}
