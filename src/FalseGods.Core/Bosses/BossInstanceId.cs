using System;

namespace FalseGods.Core.Bosses
{
    /// <summary>
    /// The stable identity of one boss instance within an encounter.
    /// </summary>
    /// <remarks>
    /// Every boss domain event carries it, so replication can route results to the right boss and clients can
    /// reconcile them idempotently (Docs/MinimalProofOfConceptPlan.md §7.6.2, Docs/ADRs/ADR-003). It is assigned
    /// by whoever creates the encounter and is not derived from a Unity <c>GetInstanceID()</c>, a scene path, or a
    /// memory address (Docs/DependencyRules.md §4). Core owns the value type; the wire layer
    /// (<c>FalseGods.Protocol</c>, which references Core) reuses it rather than defining a second one.
    /// </remarks>
    public readonly struct BossInstanceId : IEquatable<BossInstanceId>
    {
        private readonly int _value;

        public BossInstanceId(int value)
        {
            _value = value;
        }

        public int Value => _value;

        public bool Equals(BossInstanceId other) => _value == other._value;

        public override bool Equals(object? obj) => obj is BossInstanceId other && Equals(other);

        public override int GetHashCode() => _value;

        public override string ToString() => $"boss:{_value}";

        public static bool operator ==(BossInstanceId left, BossInstanceId right) => left.Equals(right);

        public static bool operator !=(BossInstanceId left, BossInstanceId right) => !left.Equals(right);
    }
}
