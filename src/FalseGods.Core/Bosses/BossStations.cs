using System;
using FalseGods.Core.Simulation;

namespace FalseGods.Core.Bosses
{
    /// <summary>
    /// One place the room authored for a boss to stand: its ground position and the height it stands at.
    /// </summary>
    /// <remarks>
    /// The height is carried beside the ground position rather than folded into it because the fight is reasoned
    /// about on the ground plane — distances to participants, attack radii, aim points are all two-dimensional —
    /// while the height is a <i>placement</i> fact taken from the anchor, never a simulated quantity. A boss does
    /// not move vertically; it stands somewhere that happens to be high.
    /// </remarks>
    public readonly struct BossAnchor : IEquatable<BossAnchor>
    {
        public BossAnchor(SimVector2 ground, float height)
        {
            Ground = ground;
            Height = height;
        }

        public SimVector2 Ground { get; }

        public float Height { get; }

        public bool Equals(BossAnchor other) => Ground.Equals(other.Ground) && Height.Equals(other.Height);

        public override bool Equals(object? obj) => obj is BossAnchor other && Equals(other);

        public override int GetHashCode() => (Ground.GetHashCode() * 397) ^ Height.GetHashCode();

        public override string ToString() => $"({Ground.X:0.0}, {Height:0.0}, {Ground.Z:0.0})";
    }

    /// <summary>
    /// One step of a boss's itinerary: which authored anchor it occupies, and the health fraction at or below
    /// which it moves there.
    /// </summary>
    /// <remarks>
    /// <para>The itinerary is <b>boss design, not room content</b>. The room says where a boss may stand; which of
    /// those places it uses, and when, is this boss's script — which is why the anchors are numbered rather than
    /// named for a role, and why a station refers to one by index.</para>
    /// <para>Unlike the vanilla cave boss, which picks its next pool at random, an itinerary is an authored order.
    /// A fight whose shape is the point of the encounter should be the same fight every time.</para>
    /// </remarks>
    public readonly struct BossStation : IEquatable<BossStation>
    {
        public BossStation(int anchorIndex, float enterAtHealthFraction, int summonCount = 0)
        {
            if (summonCount < 0)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(summonCount), summonCount, "A station cannot summon a negative number of minions.");
            }

            if (anchorIndex < 0)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(anchorIndex), anchorIndex, "An anchor index cannot be negative.");
            }

            if (enterAtHealthFraction <= 0f || enterAtHealthFraction > 1f)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(enterAtHealthFraction),
                    enterAtHealthFraction,
                    "A station is entered at a health fraction in (0, 1] — a station entered at zero health would "
                    + "be entered by a dead boss.");
            }

            AnchorIndex = anchorIndex;
            EnterAtHealthFraction = enterAtHealthFraction;
            SummonCount = summonCount;
        }

        /// <summary>Which of the room's authored anchors this station stands at.</summary>
        public int AnchorIndex { get; }

        /// <summary>The health fraction at or below which the boss moves here. The first station's is 1 — it is
        /// where the boss starts.</summary>
        public float EnterAtHealthFraction { get; }

        /// <summary>How many minions arriving here summons, or 0 for a station the boss merely stands at. This is
        /// what makes a station somewhere it goes to <i>do</i> something rather than just somewhere it is.</summary>
        public int SummonCount { get; }

        public bool Equals(BossStation other) =>
            AnchorIndex == other.AnchorIndex
            && EnterAtHealthFraction.Equals(other.EnterAtHealthFraction)
            && SummonCount == other.SummonCount;

        public override bool Equals(object? obj) => obj is BossStation other && Equals(other);

        public override int GetHashCode() => (AnchorIndex * 397) ^ EnterAtHealthFraction.GetHashCode();

        public override string ToString() => SummonCount > 0
            ? $"anchor {AnchorIndex} at <= {EnterAtHealthFraction:P0} health, summoning {SummonCount}"
            : $"anchor {AnchorIndex} at <= {EnterAtHealthFraction:P0} health";
    }
}
