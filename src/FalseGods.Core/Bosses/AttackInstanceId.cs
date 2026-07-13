using System;

namespace FalseGods.Core.Bosses
{
    /// <summary>
    /// The stable identity of a single boss attack occurrence.
    /// </summary>
    /// <remarks>
    /// The host assigns it once, when it selects the attack, and it travels with every event for that attack —
    /// telegraph, commit — so a duplicated or reordered event applies its effect <b>exactly once</b> per
    /// <see cref="AttackInstanceId"/> (Docs/MinimalProofOfConceptPlan.md B2/B5/B6, Docs/DefinitionOfDone.md
    /// "Duplicate &amp; out-of-order events"). Clients never mint one; they only ever receive it. Within a boss
    /// instance it is a monotonically increasing counter, which is enough for identity and ordering — cross-machine
    /// bit-determinism is explicitly not required (Docs/ADRs/ADR-003).
    /// </remarks>
    public readonly struct AttackInstanceId : IEquatable<AttackInstanceId>
    {
        private readonly long _value;

        public AttackInstanceId(long value)
        {
            _value = value;
        }

        public long Value => _value;

        /// <summary>The next id in sequence. Used only on the host, where attacks are minted.</summary>
        public AttackInstanceId Next() => new AttackInstanceId(_value + 1);

        public bool Equals(AttackInstanceId other) => _value == other._value;

        public override bool Equals(object? obj) => obj is AttackInstanceId other && Equals(other);

        public override int GetHashCode() => _value.GetHashCode();

        public override string ToString() => $"attack:{_value}";

        public static bool operator ==(AttackInstanceId left, AttackInstanceId right) => left.Equals(right);

        public static bool operator !=(AttackInstanceId left, AttackInstanceId right) => !left.Equals(right);
    }
}
