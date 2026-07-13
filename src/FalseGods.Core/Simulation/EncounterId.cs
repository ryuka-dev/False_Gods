using System;

namespace FalseGods.Core.Simulation
{
    /// <summary>
    /// The stable identity of one encounter run — the scope that ties a boss, an arena, both event streams, and
    /// the baseline together.
    /// </summary>
    /// <remarks>
    /// All peers agree on it (Docs/OriginalBossNetworkingArchitecture.md §9.4, invariant 1). It scopes duplicate
    /// suppression — reliable events are idempotent by (<see cref="EncounterId"/>, stream, sequence) — and the
    /// once-per-join <c>EncounterBaseline</c>. Assigned by whoever starts the encounter; it is not derived from a
    /// scene name, a Unity id, or a memory address (Docs/DependencyRules.md §4). Core owns the value type; the wire
    /// layer (<c>FalseGods.Protocol</c>, which references Core) reuses it.
    /// </remarks>
    public readonly struct EncounterId : IEquatable<EncounterId>
    {
        private readonly int _value;

        public EncounterId(int value)
        {
            _value = value;
        }

        public int Value => _value;

        public bool Equals(EncounterId other) => _value == other._value;

        public override bool Equals(object? obj) => obj is EncounterId other && Equals(other);

        public override int GetHashCode() => _value;

        public override string ToString() => $"encounter:{_value}";

        public static bool operator ==(EncounterId left, EncounterId right) => left.Equals(right);

        public static bool operator !=(EncounterId left, EncounterId right) => !left.Equals(right);
    }
}
