using System;

namespace FalseGods.Core.Bosses.Combat
{
    /// <summary>
    /// How hard the room works for the boss as the fight turns against it: how many carriers are on the route, and
    /// how much each one hauls.
    /// </summary>
    /// <remarks>
    /// <para><b>Why these two numbers and not a rate.</b> What a player actually faces is a rate — crates per
    /// second arriving at the boss — but a rate is not something the room can be given, only something that falls
    /// out of it: <c>carriers × load ÷ round trip</c>. Setting the two ends of that means the barrage thins when a
    /// player kills carriers or lengthens the route, which is the whole point of the supply line. Setting a rate
    /// directly would make the walk decorative again.</para>
    /// <para><b>Why the product is what matters.</b> Doubling the barrage means doubling <c>carriers × load</c>.
    /// Splitting that growth between more goblins and heavier loads is a taste decision — more goblins reads as
    /// the village mobilising, heavier loads as it straining — so both are authored per step rather than derived
    /// from one another.</para>
    /// </remarks>
    public readonly struct SupplyStep
    {
        public SupplyStep(float aboveHealthFraction, int carriers, int loadPerCarrier)
        {
            if (aboveHealthFraction < 0f || aboveHealthFraction > 1f)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(aboveHealthFraction), aboveHealthFraction, "A health fraction lies in [0, 1].");
            }

            if (carriers < 0)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(carriers), carriers, "A step cannot put a negative number of carriers on the route.");
            }

            if (loadPerCarrier < 0)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(loadPerCarrier), loadPerCarrier, "A carrier cannot haul a negative load.");
            }

            AboveHealthFraction = aboveHealthFraction;
            Carriers = carriers;
            LoadPerCarrier = loadPerCarrier;
        }

        /// <summary>This step applies while the boss's health is above this fraction. The steps are read in
        /// authored order, so the first one whose threshold the boss is still above wins.</summary>
        public float AboveHealthFraction { get; }

        /// <summary>How many carriers should be on the route.</summary>
        public int Carriers { get; }

        /// <summary>How many destructibles each carrier hauls in one trip.</summary>
        public int LoadPerCarrier { get; }

        /// <summary>Carriers times load — the quantity that decides the barrage. Diagnostic and design aid: two
        /// steps with the same product supply the same rate however they split it.</summary>
        public int Throughput => Carriers * LoadPerCarrier;
    }

    /// <summary>
    /// The authored ladder of <see cref="SupplyStep"/>s, read by the boss's health.
    /// </summary>
    /// <remarks>
    /// Boss design rather than room content, like the itinerary: the room says where crates come from and go, and
    /// this says how hard it works. Pure and health-driven, so it can be reasoned about without a fight running.
    /// </remarks>
    public sealed class SupplyEscalation
    {
        private readonly SupplyStep[] _steps;

        /// <param name="steps">In descending threshold order — the first step the boss's health is still above is
        /// the one in force. An empty ladder means no carriers at all.</param>
        public SupplyEscalation(params SupplyStep[] steps)
        {
            _steps = steps ?? Array.Empty<SupplyStep>();
            for (var i = 1; i < _steps.Length; i++)
            {
                if (_steps[i].AboveHealthFraction > _steps[i - 1].AboveHealthFraction)
                {
                    throw new ArgumentException(
                        "Supply steps are read in order as the boss loses health, so their thresholds must "
                        + $"descend; step {i} sits above step {i - 1}.",
                        nameof(steps));
                }
            }
        }

        /// <summary>How many steps the ladder has.</summary>
        public int Count => _steps.Length;

        /// <summary>
        /// The step in force at <paramref name="healthFraction"/>. Below the last threshold the last step stands,
        /// so a boss on its last sliver is supplied by the hardest step rather than by nobody.
        /// </summary>
        public SupplyStep At(float healthFraction)
        {
            if (_steps.Length == 0)
            {
                return new SupplyStep(0f, 0, 0);
            }

            for (var i = 0; i < _steps.Length; i++)
            {
                if (healthFraction > _steps[i].AboveHealthFraction)
                {
                    return _steps[i];
                }
            }

            return _steps[_steps.Length - 1];
        }

        /// <summary>
        /// The rate this step supplies, in destructibles per second, given how long one round trip takes. The
        /// number a fight is actually tuned against — and the one that cannot be authored directly, because it
        /// depends on a route the room decides and a walking speed the game decides.
        /// </summary>
        public static float RatePerSecond(SupplyStep step, float roundTripSeconds) =>
            roundTripSeconds > 0f ? step.Throughput / roundTripSeconds : 0f;
    }
}
