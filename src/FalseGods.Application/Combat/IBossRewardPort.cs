using FalseGods.RuntimeContracts.Arena;

namespace FalseGods.Application.Combat
{
    /// <summary>
    /// What the boss leaves behind, and the roster of items that answers for it.
    /// </summary>
    /// <remarks>
    /// <para>Same seam as the minions': the encounter decides <i>that</i> a dead boss pays out, and this side
    /// decides <i>what</i> — because an item is a game type and the layers that design the fight cannot name one.
    /// The tuning lives with the table for the same reason it does there: how many draws and how much money are
    /// meaningless apart from the table they are drawn against.</para>
    /// <para><b>Every peer drops its own.</b> Loot in this game is a local pickup, and the session layer does not
    /// mirror it — which is right, and is the same rule the project already keeps: automatically generated loot is
    /// personal, only a player's own deliberate drop is a shared-world object. So each peer answers the replicated
    /// death by paying its own player, exactly as each peer plays its own music and fog off that fact. Two players
    /// therefore get two independent rolls, which is the only version of this that is fair in co-op.</para>
    /// </remarks>
    public interface IBossRewardPort
    {
        /// <summary>
        /// Pay out at <paramref name="at"/> — normally where the body went down. Safe to call when the game is not
        /// ready to answer: a reward that cannot be paid is logged and dropped, never an exception into the
        /// teardown that called it.
        /// </summary>
        void DropReward(ArenaWorldPoint at);
    }
}
