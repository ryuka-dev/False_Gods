namespace FalseGods.Core.Bosses.Combat
{
    /// <summary>
    /// The first boss's summoning roster, by name: every hostile squad it can put on the floor.
    /// </summary>
    /// <remarks>
    /// <para><b>One place, named once.</b> Both ends of the seam need these ids — the itinerary says which band a
    /// station calls up, and the adapter's roster says what that band is made of — so they are defined here rather
    /// than agreed on by writing the same string in two projects.</para>
    /// <para><b>The bands escalate by composition, not by headcount</b>, which is how the vanilla cave boss does
    /// it: its two authored henchman lists are both seven strong and differ only in the mix (five young and two
    /// spearmen, then three and four). A wave that is merely bigger asks the same question louder; a wave with a
    /// different shape asks a different question.</para>
    /// <para><b>What the ladder is built from.</b> The game's own cave unit pools say which creatures belong in
    /// this environment and in what order of difficulty — tier 1 is the rank and file, tier 2 adds the heavy, tier
    /// 3 adds the casters — so the three waves follow those three tiers rather than a scheme of ours. Strength is
    /// reported in the game's own currency, <c>UnitSO.SpawnCost</c>, against which one vanilla cave patrol is
    /// worth fifteen.</para>
    /// <para><b>Still code rather than authored content</b>, like the itinerary beside it: there is no
    /// boss-content pipeline yet and one boss does not justify inventing one (Docs/DefinitionOfDone.md §3). This
    /// is the vocabulary that becomes registry data when a second boss lands — see the note on
    /// <c>EncounterCoordinator</c>.</para>
    /// </remarks>
    public static class MinionBands
    {
        /// <summary>The cave's rank and file, sent to meet the players first. Numbers and spears, nothing else.
        /// </summary>
        public static readonly MinionBandId Vanguard = new MinionBandId("vanguard");

        /// <summary>The village commits: fewer of the smallest, more spears, and the first of the heavies.
        /// </summary>
        public static readonly MinionBandId Warband = new MinionBandId("warband");

        /// <summary>What the cave keeps for last — the casters, who punish standing still to deal with the melee
        /// that arrives with them.</summary>
        public static readonly MinionBandId Coven = new MinionBandId("coven");

        /// <summary>
        /// The band a starved boss calls down on the room itself, and the one that has to die before it calms.
        /// </summary>
        /// <remarks>
        /// Kept small and heavy rather than numerous: killing it is half of what it takes to end a rage, so it has
        /// to be a job that a party can actually finish while the boss is hitting three times as hard. It is drawn
        /// with the game's own outline so a player can tell it apart from the ordinary wave beside it.
        /// </remarks>
        public static readonly MinionBandId Emergency = new MinionBandId("emergency");
    }
}
