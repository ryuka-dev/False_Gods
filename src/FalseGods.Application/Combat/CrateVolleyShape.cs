namespace FalseGods.Application.Combat
{
    /// <summary>
    /// The few numbers that describe one shotgun volley of crates: how many, how they lift off the pile, how long
    /// they hover, how far they scatter, and the arc they ride to the ground.
    /// </summary>
    /// <remarks>Everything here is data the simulation decides and every peer can be handed, so a volley is
    /// reproducible from a message rather than streamed. The scatter is seeded (see
    /// <c>FalseGods.Core.Bosses.Combat.ShotgunSpread</c>); the lift and the arc are plain durations and heights
    /// (see <c>FalseGods.Core.Bosses.Combat.BallisticArc</c>).</remarks>
    public readonly struct CrateVolleyShape
    {
        public CrateVolleyShape(
            int seed,
            int count,
            float spreadMinRadius,
            float spreadMaxRadius,
            float liftHeight,
            float liftSeconds,
            float holdSeconds,
            float flightSeconds,
            float apexHeight,
            float leadShare)
        {
            Seed = seed;
            Count = count;
            SpreadMinRadius = spreadMinRadius;
            SpreadMaxRadius = spreadMaxRadius;
            LiftHeight = liftHeight;
            LiftSeconds = liftSeconds;
            HoldSeconds = holdSeconds;
            FlightSeconds = flightSeconds;
            ApexHeight = apexHeight;
            LeadShare = leadShare;
        }

        /// <summary>Seeds the scatter pattern; the same seed lays the crates out the same way on every peer.</summary>
        public int Seed { get; }

        /// <summary>How many crates to lift from the pile and throw. Fewer are thrown if the pile is short.</summary>
        public int Count { get; }

        /// <summary>Nearest a crate may land to the centre — kept above zero so none land on the target's head.</summary>
        public float SpreadMinRadius { get; }

        /// <summary>Furthest a crate may land from the centre.</summary>
        public float SpreadMaxRadius { get; }

        /// <summary>How high a crate rises off the pile before it is thrown.</summary>
        public float LiftHeight { get; }

        /// <summary>How long the rise off the pile takes.</summary>
        public float LiftSeconds { get; }

        /// <summary>How long the crates hover at the top before firing — the telegraph the player reads.</summary>
        public float HoldSeconds { get; }

        /// <summary>How long each crate's flight to the ground lasts.</summary>
        public float FlightSeconds { get; }

        /// <summary>Height of each crate's arc above the line from the hover point to its landing spot.</summary>
        public float ApexHeight { get; }

        /// <summary>
        /// The fraction of crates aimed at where the player is predicted to be, the rest at where the player is
        /// now. Each crate decides independently, seeded, so one volley threatens both spots at once: a player who
        /// jinks to bait the lead is still caught by the crates aimed where they stand, and a player who runs
        /// straight is still caught by the crates aimed where they are going. Clamped to [0, 1]; 0 never leads,
        /// 1 always does.
        /// </summary>
        public float LeadShare { get; }
    }
}
