namespace FalseGods.Core.Bosses.Combat
{
    /// <summary>
    /// Watches the boss's supply and decides when being starved has gone on long enough to be worth answering.
    /// </summary>
    /// <remarks>
    /// <para><b>Why the boss needs this at all.</b> Its barrage is fed by goblins walking crates across the room,
    /// which makes the route something players can attack — and once killing carriers actually costs the boss
    /// supply, a party that concentrates on the village can switch the barrage off entirely. Without an answer,
    /// the most interesting thing the room offers is also the way to make the fight stop happening.</para>
    /// <para><b>So starving it is a tactic with a price, not an off switch.</b> A boss with nothing to throw comes
    /// at the players itself. That keeps the choice real: shutting down the barrage is worth doing, and it buys a
    /// different and more dangerous fight rather than a safe one.</para>
    /// <para><b>Getting out takes two things at once</b>: the emergency band it summoned has to be dead <i>and</i>
    /// the route has to be running again. Either alone would be too cheap — one load delivered while its guard is
    /// still standing would end it, and so would killing the guard while the route is still cut.
    /// Both together mean the players have to answer the band and stop starving it, which is exactly the pair of
    /// jobs the rage is meant to cost. Nothing here asks the normal waves to stand aside: the emergency band is
    /// counted separately precisely so the two can be on the floor at once.</para>
    /// <para><b>What "starved" is measured by.</b> Not whether the pile has anything on it right now: the boss
    /// empties it every volley by design, so that reading is false almost all the time and would call a working
    /// supply line a cut one. What matters is how long it has been since a load <i>arrived</i>. That also makes
    /// the opening of a fight behave — the pile is legitimately empty until the first carriers walk the length of
    /// the room — provided the caller's patience is longer than the round trip they need to do it.</para>
    /// <para>Pure, and driven by the caller's frame rather than a clock, so it is testable and behaves the same
    /// wherever it is ticked.</para>
    /// </remarks>
    public sealed class StarvationWatch
    {
        private float _sinceDelivery;
        private float _sinceHit;

        /// <summary>Whether the boss is currently answering being starved.</summary>
        public bool Enraged { get; private set; }

        /// <summary>How long since the last load reached the boss. Diagnostic.</summary>
        public float SinceDelivery => _sinceDelivery;

        /// <summary>How long since anything the boss threw actually hurt somebody. Diagnostic.</summary>
        public float SinceHit => _sinceHit;

        /// <summary>Which of the two reasons provoked the boss, once it is enraged.</summary>
        public StarvationReason Reason { get; private set; }

        /// <summary>
        /// Advance the watch one frame and report whether the boss's state changed.
        /// </summary>
        /// <param name="deltaSeconds">The frame.</param>
        /// <param name="deliveryArrived">Whether a load reached the boss this frame.</param>
        /// <param name="patienceSeconds">How long the boss will go without a delivery before answering it.</param>
        /// <param name="emergencyBandAlive">How many of the band summoned by the last rage are still standing.
        /// Only that band counts — the ordinary waves are a different fight going on at the same time.</param>
        /// <param name="hitSomebody">Whether something the boss threw hurt a player this frame.</param>
        /// <param name="futilitySeconds">How long the boss will go on throwing without hitting anybody before it
        /// gives up on throwing. Zero switches this reason off.</param>
        public StarvationChange Advance(
            float deltaSeconds,
            bool deliveryArrived,
            float patienceSeconds,
            int emergencyBandAlive,
            bool hitSomebody = false,
            float futilitySeconds = 0f)
        {
            var frame = deltaSeconds > 0f ? deltaSeconds : 0f;
            _sinceDelivery = deliveryArrived ? 0f : _sinceDelivery + frame;
            _sinceHit = hitSomebody ? 0f : _sinceHit + frame;

            var supplied = _sinceDelivery < patienceSeconds;

            // The other way a barrage stops being a barrage. Being supplied and throwing all of it at players who
            // simply are not there any more — behind cover, up on a terrace, too far to reach — leaves the boss
            // doing nothing while looking busy, which the supply reason cannot see because the route is running.
            var landing = futilitySeconds <= 0f || _sinceHit < futilitySeconds;

            if (!Enraged)
            {
                if (supplied && landing)
                {
                    return StarvationChange.Nothing;
                }

                Enraged = true;
                Reason = supplied ? StarvationReason.NothingLanding : StarvationReason.NothingArriving;
                return StarvationChange.Enraged;
            }

            // Getting out takes everything that provoked it undone AND the band it summoned dead — either alone
            // and the rage is bought off too cheaply.
            if (supplied && landing && emergencyBandAlive <= 0)
            {
                Enraged = false;
                return StarvationChange.Calmed;
            }

            return StarvationChange.Nothing;
        }

        /// <summary>Forget everything: a fight that ended leaves no rage behind for the next one.</summary>
        public void Reset()
        {
            Enraged = false;
            _sinceDelivery = 0f;
            _sinceHit = 0f;
        }
    }

    /// <summary>Why the boss stopped waiting.</summary>
    public enum StarvationReason
    {
        /// <summary>Nothing is reaching it — the route has been cut.</summary>
        NothingArriving = 0,

        /// <summary>Plenty is reaching it and none of it is landing on anybody.</summary>
        NothingLanding = 1,
    }

    /// <summary>What <see cref="StarvationWatch.Advance"/> just decided.</summary>
    public enum StarvationChange
    {
        /// <summary>No change this frame.</summary>
        Nothing = 0,

        /// <summary>The boss has run dry long enough and is answering it — summon the emergency band.</summary>
        Enraged = 1,

        /// <summary>Supplied again and its band is dead: back to throwing.</summary>
        Calmed = 2,
    }
}
