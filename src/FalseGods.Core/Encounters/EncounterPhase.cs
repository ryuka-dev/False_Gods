namespace FalseGods.Core.Encounters
{
    /// <summary>
    /// The overall stage of an encounter run, owned by the <see cref="EncounterCoordinator"/>.
    /// </summary>
    /// <remarks>
    /// Distinct from a <c>BossPhase</c> (the boss's own two-phase state machine): this is the encounter-level
    /// lifecycle carried by the <c>EncounterBaseline</c> for late join (Docs/MultiplayerLoadingContract.md §5.7).
    /// </remarks>
    public enum EncounterPhase
    {
        /// <summary>Before the fight starts — arena loaded, ready gate not yet passed / boss not yet started.</summary>
        PreFight = 0,

        /// <summary>The fight is live.</summary>
        Fighting = 1,

        /// <summary>The boss is dead; the exit is unlocked.</summary>
        Defeated = 2,

        /// <summary>Players are leaving; teardown is in progress.</summary>
        Exiting = 3,
    }
}
