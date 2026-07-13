using System;

namespace FalseGods.Core.Simulation
{
    /// <summary>
    /// The identity of one encounter participant (a player) that the boss may target.
    /// </summary>
    /// <remarks>
    /// This is a <b>session peer</b> identity as the domain sees it, not a transport connection handle, a Steam
    /// account, or a display name (Docs/DependencyRules.md §4). The domain only ever compares participants for
    /// equality and asks an <see cref="IEncounterParticipantQuery"/> for their positions; how the id maps onto a
    /// real network peer is an adapter concern outside Core.
    /// </remarks>
    public readonly struct ParticipantId : IEquatable<ParticipantId>
    {
        private readonly int _value;

        public ParticipantId(int value)
        {
            _value = value;
        }

        public int Value => _value;

        public bool Equals(ParticipantId other) => _value == other._value;

        public override bool Equals(object? obj) => obj is ParticipantId other && Equals(other);

        public override int GetHashCode() => _value;

        public override string ToString() => $"participant:{_value}";

        public static bool operator ==(ParticipantId left, ParticipantId right) => left.Equals(right);

        public static bool operator !=(ParticipantId left, ParticipantId right) => !left.Equals(right);
    }
}
