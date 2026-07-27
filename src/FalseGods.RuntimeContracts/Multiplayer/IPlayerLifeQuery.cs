namespace FalseGods.RuntimeContracts.Multiplayer
{
    /// <summary>
    /// Asking whether a player is still in the fight.
    /// </summary>
    /// <remarks>
    /// <para>In cooperative play a player who runs out of health is not immediately gone: they go down and wait to
    /// be rescued. Someone lying on the floor is not a target — a boss that keeps attacking them is hitting
    /// something that cannot fight back, and worse, is spending attacks that should be threatening the players who
    /// still can.</para>
    /// <para><b>Only a session can answer this</b>, because the state is the session's invention: single-player has
    /// no such thing, so without one every player is in the fight and nothing changes.</para>
    /// <para>The player crosses this seam as <see cref="object"/> for the same reason a spawn owner does in
    /// <see cref="ISpawnOwnership"/>: what identifies a player is a game-engine type this assembly may not name,
    /// and only the adapter needs to understand it.</para>
    /// </remarks>
    public interface IPlayerLifeQuery
    {
        /// <summary>
        /// True when <paramref name="playerUnit"/> is a player who is out of the fight — downed awaiting rescue,
        /// or dead. False for a player still fighting, for anything that is not a player, and whenever the
        /// integration cannot tell: a boss that attacks someone it should have spared is a worse fight, and one
        /// that spares everyone because the question went unanswered is no fight at all.
        /// </summary>
        bool IsOutOfTheFight(object playerUnit);
    }
}
