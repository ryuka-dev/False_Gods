using System;

namespace FalseGods.Core.Arena
{
    /// <summary>
    /// The stable identity of an arena mechanism group — a set of arena elements activated together (e.g. the
    /// hazards that switch on in the boss's second phase).
    /// </summary>
    /// <remarks>
    /// It is an authored id, not a loose string passed around by convention (Docs/DependencyRules.md §3): a
    /// distinct value type so the arena's mechanism vocabulary cannot be confused with any other id. The
    /// <c>EncounterCoordinator</c> names a group when translating a boss phase change into an arena command
    /// (Docs/Architecture.md §5); the arena owns which concrete elements the group contains.
    /// </remarks>
    public readonly struct MechanismGroupId : IEquatable<MechanismGroupId>
    {
        private readonly string _value;

        public MechanismGroupId(string value)
        {
            if (string.IsNullOrEmpty(value))
            {
                throw new ArgumentException("A mechanism group id must be a non-empty string.", nameof(value));
            }

            _value = value;
        }

        public string Value => _value ?? string.Empty;

        public bool Equals(MechanismGroupId other) => string.Equals(Value, other.Value, StringComparison.Ordinal);

        public override bool Equals(object? obj) => obj is MechanismGroupId other && Equals(other);

        public override int GetHashCode() => StringComparer.Ordinal.GetHashCode(Value);

        public override string ToString() => Value;

        public static bool operator ==(MechanismGroupId left, MechanismGroupId right) => left.Equals(right);

        public static bool operator !=(MechanismGroupId left, MechanismGroupId right) => !left.Equals(right);
    }
}
