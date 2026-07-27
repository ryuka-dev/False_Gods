using System;
using System.Collections.Generic;

namespace FalseGods.Core.Bosses.Combat
{
    /// <summary>
    /// The authored shape of a boss's supply line: how fast its ammunition is produced, and how much of it the
    /// room will hold.
    /// </summary>
    /// <remarks>
    /// Both ceilings exist to stop a fight that goes long from turning into a room full of crates. They are
    /// different ceilings because they bound different failures: <see cref="SourceCapacity"/> bounds production
    /// running ahead of carrying (nobody is collecting), and <see cref="DeliveryCapacity"/> bounds carrying
    /// running ahead of firing (the boss is not shooting). First-pass numbers, tuned in game like the boss's own.
    /// </remarks>
    public readonly struct SupplyLineShape
    {
        public SupplyLineShape(float secondsPerCrate, int sourceCapacity, int deliveryCapacity)
        {
            if (!(secondsPerCrate > 0f))
            {
                throw new ArgumentOutOfRangeException(
                    nameof(secondsPerCrate), secondsPerCrate, "A production interval must be a positive number of "
                    + "seconds; zero would produce a crate every frame.");
            }

            if (sourceCapacity < 0)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(sourceCapacity), sourceCapacity, "A capacity cannot be negative.");
            }

            if (deliveryCapacity < 0)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(deliveryCapacity), deliveryCapacity, "A capacity cannot be negative.");
            }

            SecondsPerCrate = secondsPerCrate;
            SourceCapacity = sourceCapacity;
            DeliveryCapacity = deliveryCapacity;
        }

        /// <summary>Seconds between one production point yielding one destructible. Each point keeps its own
        /// clock, so a room with two of them supplies twice as fast.</summary>
        public float SecondsPerCrate { get; }

        /// <summary>How many destructibles may be resting at one production point before it stops producing.</summary>
        public int SourceCapacity { get; }

        /// <summary>How many destructibles may be resting on one delivery pile before carrying there is
        /// pointless.</summary>
        public int DeliveryCapacity { get; }
    }

    /// <summary>
    /// The boss's supply line: production points that yield destructibles on their own clocks, up to what the
    /// room will hold.
    /// </summary>
    /// <remarks>
    /// <para><b>What this owns and what it does not.</b> It owns the <i>timing</i> — when a production point is due
    /// — and nothing else. It does not know where the points are, what a destructible is, or whether anyone came to
    /// collect: it is told how many are already standing at each point and answers which points should yield now.
    /// That keeps the decision pure and testable while the world stays the adapter's business, the same split the
    /// boss's own simulation uses.</para>
    /// <para><b>A production is a command, not an event</b> — drained like <see cref="SummonRequest"/>, never
    /// rendered and never replicated as itself. Only single-player and the host run one: the host owns the world
    /// (SULFUR Together invariant 1), and a client that produced its own would double every crate.</para>
    /// <para><b>A full point does not bank its wait.</b> Its clock is held at the ready mark while it is full, so a
    /// player who clears a long-untouched pile gets one crate and then the ordinary interval — not a burst of
    /// everything that would have been produced while nobody was looking.</para>
    /// </remarks>
    public sealed class SupplyLine
    {
        private readonly SupplyLineShape _shape;
        private readonly float[] _sinceProduced;
        private readonly List<int> _due = new List<int>();

        /// <param name="sourceCount">How many production points the room authored. Zero is a room with no supply
        /// line, which produces nothing rather than failing.</param>
        public SupplyLine(SupplyLineShape shape, int sourceCount)
        {
            if (sourceCount < 0)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(sourceCount), sourceCount, "A room cannot have a negative number of production points.");
            }

            _shape = shape;
            _sinceProduced = new float[sourceCount];
        }

        /// <summary>How many production points this line drives.</summary>
        public int SourceCount => _sinceProduced.Length;

        /// <summary>
        /// Advance every production point's clock by <paramref name="deltaSeconds"/> and decide which of them
        /// yield now. <paramref name="restingAtSource"/> is how many destructibles are currently standing at each
        /// point, in the room's authored order; a point at or over
        /// <see cref="SupplyLineShape.SourceCapacity"/> holds instead of producing. Points the list does not cover
        /// are treated as empty, so a short list under-reports rather than stalling the line.
        /// </summary>
        public void Advance(float deltaSeconds, IReadOnlyList<int> restingAtSource)
        {
            _due.Clear();
            if (deltaSeconds <= 0f)
            {
                return;
            }

            for (var source = 0; source < _sinceProduced.Length; source++)
            {
                var resting = restingAtSource != null && source < restingAtSource.Count
                    ? restingAtSource[source]
                    : 0;

                if (resting >= _shape.SourceCapacity)
                {
                    // Full: the clock restarts rather than waiting poised at the ready mark. Holding it there made
                    // a full point refill the instant anything was broken off it, so shooting a crate off a full
                    // pile achieved nothing — measured in game, and the reason breaking one must now cost the
                    // room a whole interval.
                    _sinceProduced[source] = 0f;
                    continue;
                }

                _sinceProduced[source] += deltaSeconds;
                if (_sinceProduced[source] < _shape.SecondsPerCrate)
                {
                    continue;
                }

                // One crate per point per advance, whatever the frame cost: a stalled frame should not fire a
                // backlog of crates into the room at once.
                _sinceProduced[source] = 0f;
                _due.Add(source);
            }
        }

        /// <summary>Which production points yielded on the last <see cref="Advance"/>, by authored index. Valid
        /// until the next advance.</summary>
        public IReadOnlyList<int> DrainProductionRequests() => _due;

        /// <summary>Whether a delivery pile holding <paramref name="resting"/> destructibles has room for another.
        /// Asked by whoever decides to carry, so the answer to "is it worth walking there" lives with the shape
        /// that decided the ceiling.</summary>
        public bool AcceptsDelivery(int resting) => resting < _shape.DeliveryCapacity;
    }
}
