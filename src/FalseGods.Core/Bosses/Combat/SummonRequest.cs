namespace FalseGods.Core.Bosses.Combat
{
    /// <summary>
    /// The boss calls up the <see cref="Band"/>, having arrived at the station that summons it.
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
    /// <para><b>The band is named, not enumerated.</b> The boss decides that a wave arrives; what a wave is made
    /// of is the roster's answer, given in the game's own creatures, which this layer cannot name.</para>
    /// </remarks>
    public sealed record SummonRequest(int StationIndex, MinionBandId Band);
}
