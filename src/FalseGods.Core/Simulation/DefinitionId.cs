using System;

namespace FalseGods.Core.Simulation
{
    /// <summary>
    /// The identity of a boss or arena <em>definition</em> (design/data), distinct from an instance id.
    /// </summary>
    /// <remarks>
    /// It answers "which boss is this" — all peers must agree on it (Docs/OriginalBossNetworkingArchitecture.md
    /// §9.4). It is separate from <see cref="BossInstanceId"/> (a specific live instance) and from an attack's
    /// <c>AttackDefinitionId</c> (Docs/DependencyRules.md §4). Core owns the value type; the wire layer reuses it.
    /// </remarks>
    public readonly struct DefinitionId : IEquatable<DefinitionId>
    {
        private readonly int _value;

        public DefinitionId(int value)
        {
            _value = value;
        }

        public int Value => _value;

        public bool Equals(DefinitionId other) => _value == other._value;

        public override bool Equals(object? obj) => obj is DefinitionId other && Equals(other);

        public override int GetHashCode() => _value;

        public override string ToString() => $"definition:{_value}";

        public static bool operator ==(DefinitionId left, DefinitionId right) => left.Equals(right);

        public static bool operator !=(DefinitionId left, DefinitionId right) => !left.Equals(right);
    }
}
