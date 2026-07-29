using System;

namespace FalseGods.Core.Bosses.Combat
{
    /// <summary>
    /// The stable identity of one band the boss can summon — a named squad, not a headcount.
    /// </summary>
    /// <remarks>
    /// <para>A band is <b>named</b> rather than counted because a summon is a decision about <i>what arrives</i>,
    /// and the fight only reads as designed if the second wave differs from the first. Asking for "four minions"
    /// can only ever produce the same wave twice; asking for a band lets the roster answer with a composition.</para>
    /// <para>It is a distinct value type rather than a loose string (Docs/DependencyRules.md §3), and it carries no
    /// creature: which real units a band consists of is the game's vocabulary and therefore the adapter's, while
    /// which band a station summons is boss design and therefore the itinerary's. The id is the seam between the
    /// two, exactly as <see cref="FalseGods.Core.Arena.MechanismGroupId"/> is between a boss phase and the arena
    /// elements it switches on.</para>
    /// <para>The default value names no band, which is what a station that merely stands somewhere carries.</para>
    /// </remarks>
    public readonly struct MinionBandId : IEquatable<MinionBandId>
    {
        private readonly string _value;

        public MinionBandId(string value)
        {
            if (string.IsNullOrEmpty(value))
            {
                throw new ArgumentException("A minion band id must be a non-empty string.", nameof(value));
            }

            _value = value;
        }

        public string Value => _value ?? string.Empty;

        /// <summary>False for the default value — the "no band" a non-summoning station carries.</summary>
        public bool NamesABand => !string.IsNullOrEmpty(_value);

        public bool Equals(MinionBandId other) => string.Equals(Value, other.Value, StringComparison.Ordinal);

        public override bool Equals(object? obj) => obj is MinionBandId other && Equals(other);

        public override int GetHashCode() => StringComparer.Ordinal.GetHashCode(Value);

        public override string ToString() => NamesABand ? Value : "(no band)";

        public static bool operator ==(MinionBandId left, MinionBandId right) => left.Equals(right);

        public static bool operator !=(MinionBandId left, MinionBandId right) => !left.Equals(right);
    }
}
