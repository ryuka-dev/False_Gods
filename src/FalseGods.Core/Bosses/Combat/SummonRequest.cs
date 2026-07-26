namespace FalseGods.Core.Bosses.Combat
{
    /// <summary>
    /// The boss asks the world for <see cref="Count"/> minions, having arrived at the station that summons them.
    /// </summary>
    /// <remarks>
    /// Drained separately from the boss's presentation/wire events (<see cref="BossSimulation.DrainSummonRequests"/>,
    /// not <see cref="BossSimulation.DrainEvents"/>), for the same reason a <see cref="DamageRequest"/> is: it is a
    /// <b>command to another system</b>, not a boss-state fact to render or replicate. It never enters the
    /// presentation or wire mapper — and it does not need to. The minions are the game's own units, so the session
    /// layer replicates them exactly as it replicates every other enemy; a client that spawned its own would be
    /// making authoritative decisions and would double them.
    /// <para>Only single-player and the host produce these (SULFUR Together invariant 1: the host owns enemies).
    /// <see cref="StationIndex"/> says which step of the itinerary asked, so a station that summons twice because
    /// the boss visited it twice is distinguishable from one that summoned once.</para>
    /// </remarks>
    public sealed record SummonRequest(int StationIndex, int Count);
}
