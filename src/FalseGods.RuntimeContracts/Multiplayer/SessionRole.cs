namespace FalseGods.RuntimeContracts.Multiplayer
{
    /// <summary>This peer's authority role in the session.</summary>
    /// <remarks>
    /// The host runs the authoritative <c>BossSimulation</c> and broadcasts; a client runs presentation only and
    /// applies replicated results (Docs/OriginalBossNetworkingArchitecture.md §9.3). Connection method never
    /// changes this (project invariant 5).
    /// </remarks>
    public enum SessionRole
    {
        /// <summary>Authoritative host.</summary>
        Host = 0,

        /// <summary>Non-authoritative client.</summary>
        Client = 1,
    }
}
