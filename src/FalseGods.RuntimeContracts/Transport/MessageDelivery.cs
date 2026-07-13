namespace FalseGods.RuntimeContracts.Transport
{
    /// <summary>
    /// How an <see cref="EncodedPayload"/> should be delivered — the project-neutral delivery guarantee an
    /// adapter maps onto its transport's flags.
    /// </summary>
    /// <remarks>
    /// Two modes, matching the replication model (Docs/OriginalBossNetworkingArchitecture.md §9.6): reliable,
    /// ordered discrete events, and unreliable continuous snapshots whose loss is tolerated. The ST adapter maps
    /// these onto the bridge's own delivery flags; gameplay never names a vendor flag (Docs/DependencyRules.md §5).
    /// </remarks>
    public enum MessageDelivery
    {
        /// <summary>Delivered reliably and in order within the channel — for discrete, sequenced events and baselines.</summary>
        ReliableOrdered = 0,

        /// <summary>Best-effort, unordered — for continuous snapshots where loss is corrected by the next one.</summary>
        Unreliable = 1,
    }
}
