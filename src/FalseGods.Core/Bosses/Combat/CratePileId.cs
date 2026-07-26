using System;

namespace FalseGods.Core.Bosses.Combat
{
    /// <summary>What a heap of resting destructibles is <i>for</i>.</summary>
    public enum CratePileKind
    {
        /// <summary>Not part of the supply line: a crate that was dropped somewhere, or spilled by a carrier who
        /// died on the way. Real, shootable, and worth loot — but the boss will never fire it.</summary>
        Loose = 0,

        /// <summary>Where the room produces destructibles. The near end of the supply line, and the end a player
        /// can stand on.</summary>
        Source = 1,

        /// <summary>Where carriers set destructibles down beside the boss. The only pile a volley draws from.</summary>
        Delivery = 2,
    }

    /// <summary>
    /// Which heap a resting destructible belongs to: its kind, and which of that kind.
    /// </summary>
    /// <remarks>
    /// <para><b>Why a crate needs to know this at all.</b> A volley lifts whatever is resting; without this, a boss
    /// would fire the crates still standing at the production points as readily as the ones carried to it, and the
    /// carrying would be scenery. The pile is what makes the walk between the two ends of the room the mechanic
    /// rather than a decoration.</para>
    /// <para><b>Why kind and index rather than one number.</b> Source 0 and delivery 0 are different places with
    /// different meanings, and a boss draws from exactly one of them. Folding both into a single integer would
    /// make the distinction a numbering convention that nothing enforces (Docs/DependencyRules.md §3); as a pair
    /// it cannot be confused, and a mistaken lookup fails to find crates instead of firing the wrong ones.</para>
    /// <para>A <see cref="CratePileKind.Loose"/> pile has no index — every loose crate is on the same
    /// nowhere-in-particular — so all loose crates compare equal.</para>
    /// </remarks>
    public readonly struct CratePileId : IEquatable<CratePileId>
    {
        private CratePileId(CratePileKind kind, int index)
        {
            Kind = kind;
            Index = index;
        }

        /// <summary>Belonging to no pile: shootable, but never the boss's ammunition.</summary>
        public static CratePileId Loose => new CratePileId(CratePileKind.Loose, 0);

        public CratePileKind Kind { get; }

        /// <summary>Which pile of its kind, indexing the room's authored group. Always 0 for
        /// <see cref="CratePileKind.Loose"/>.</summary>
        public int Index { get; }

        /// <summary>The room's <c>index</c>-th production point.</summary>
        public static CratePileId Source(int index) => Of(CratePileKind.Source, index);

        /// <summary>The room's <c>index</c>-th delivery pile — the one beside boss anchor <c>index</c>.</summary>
        public static CratePileId Delivery(int index) => Of(CratePileKind.Delivery, index);

        /// <summary>Rebuild a pile from a kind and index that came from outside — the wire, or configuration.
        /// An unknown kind or a negative index is not a pile, and is rejected rather than quietly becoming one.</summary>
        public static bool TryFrom(int kind, int index, out CratePileId pile)
        {
            pile = Loose;
            if (index < 0)
            {
                return false;
            }

            switch ((CratePileKind)kind)
            {
                case CratePileKind.Loose:
                    return true; // index ignored: every loose crate is on the same nowhere
                case CratePileKind.Source:
                case CratePileKind.Delivery:
                    pile = new CratePileId((CratePileKind)kind, index);
                    return true;
                default:
                    return false;
            }
        }

        private static CratePileId Of(CratePileKind kind, int index)
        {
            if (index < 0)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(index), index, "A pile index is a position in the room's authored group, never negative.");
            }

            return new CratePileId(kind, index);
        }

        public bool Equals(CratePileId other) => Kind == other.Kind && Index == other.Index;

        public override bool Equals(object? obj) => obj is CratePileId other && Equals(other);

        public override int GetHashCode() => ((int)Kind * 397) ^ Index;

        public static bool operator ==(CratePileId left, CratePileId right) => left.Equals(right);

        public static bool operator !=(CratePileId left, CratePileId right) => !left.Equals(right);

        public override string ToString() =>
            Kind == CratePileKind.Loose ? "loose" : $"{Kind.ToString().ToLowerInvariant()} #{Index}";
    }
}
