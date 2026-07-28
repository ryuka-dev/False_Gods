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
    /// there has to be something on the pile again. Either alone would be too cheap — one crate delivered while
    /// its guard is still standing would end it, and so would killing the guard while the route is still cut.
    /// Both together mean the players have to answer the band and stop starving it, which is exactly the pair of
    /// jobs the rage is meant to cost. Nothing here asks the normal waves to stand aside: the emergency band is
    /// counted separately precisely so the two can be on the floor at once.</para>
    /// <para>Pure, and driven by the caller's frame rather than a clock, so it is testable and behaves the same
    /// wherever it is ticked.</para>
    /// </remarks>
    public sealed class StarvationWatch
    {
        private readonly float _enterAfterSeconds;

        private float _starvingFor;

        /// <param name="enterAfterSeconds">How long the boss must have had nothing to throw before it answers.
        /// Long enough that an ordinary gap between deliveries passes unremarked, short enough that a cut route is
        /// felt.</param>
        public StarvationWatch(float enterAfterSeconds)
        {
            _enterAfterSeconds = enterAfterSeconds > 0f ? enterAfterSeconds : 0f;
        }

        /// <summary>Whether the boss is currently answering being starved.</summary>
        public bool Enraged { get; private set; }

        /// <summary>How long the pile has been empty, or 0 while it is not. Diagnostic.</summary>
        public float StarvingFor => _starvingFor;

        /// <summary>
        /// Advance the watch one frame and report whether the boss's state changed.
        /// </summary>
        /// <param name="deltaSeconds">The frame.</param>
        /// <param name="hasAmmunition">Whether there is anything on the boss's pile to throw.</param>
        /// <param name="emergencyBandAlive">How many of the band summoned by the last rage are still standing.
        /// Only that band counts — the ordinary waves are a different fight going on at the same time.</param>
        public StarvationChange Advance(float deltaSeconds, bool hasAmmunition, int emergencyBandAlive)
        {
            if (hasAmmunition)
            {
                _starvingFor = 0f;
            }
            else
            {
                _starvingFor += deltaSeconds > 0f ? deltaSeconds : 0f;
            }

            if (!Enraged)
            {
                if (_starvingFor < _enterAfterSeconds)
                {
                    return StarvationChange.Nothing;
                }

                Enraged = true;
                return StarvationChange.Enraged;
            }

            // Supplied again AND the band it summoned is gone: both, or the rage is bought off too cheaply.
            if (hasAmmunition && emergencyBandAlive <= 0)
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
            _starvingFor = 0f;
        }
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
