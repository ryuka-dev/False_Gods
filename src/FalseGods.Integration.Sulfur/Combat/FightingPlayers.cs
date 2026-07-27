using System;
using FalseGods.RuntimeContracts.Multiplayer;
using PerfectRandom.Sulfur.Core;
using PerfectRandom.Sulfur.Core.Units;

namespace FalseGods.Integration.Sulfur.Combat
{
    /// <summary>
    /// Who is still worth attacking.
    /// </summary>
    /// <remarks>
    /// <para>Everything the encounter aims at reads the game's own player roster — the boss's attacks, its crates'
    /// splash, its minions' targets, the simulation's list of participants. A player who has gone down and is
    /// waiting to be rescued is still standing in that roster, so without this they keep being attacked: hit while
    /// helpless, and — worse for the fight — soaking attacks that should be threatening the players who can still
    /// answer them.</para>
    /// <para><b>One gate for all four readers</b>, because a boss that spares a downed player with its fists and
    /// then drops a crate on them is not sparing them. It is set by the composition root while a session is live
    /// and cleared with it, so single-player asks nobody and everyone counts as fighting.</para>
    /// <para>Deliberately not a port of its own: it answers the same question for every caller and holds no state
    /// of its own, so threading it through four constructors would buy nothing.</para>
    /// </remarks>
    public static class FightingPlayers
    {
        private static IPlayerLifeQuery? _lives;

        /// <summary>Point the gate at the live session's answer, or pass null to go back to "everybody is
        /// fighting" — which is what single-player means.</summary>
        public static void AskedOf(IPlayerLifeQuery? lives) => _lives = lives;

        /// <summary>
        /// Whether this player should be attacked at all. False for a player who is down; true for everyone else,
        /// including whenever nothing can answer the question.
        /// </summary>
        public static bool IsFighting(Unit? playerUnit)
        {
            if (playerUnit == null)
            {
                return false;
            }

            var lives = _lives;
            if (lives == null)
            {
                return true;
            }

            try
            {
                return !lives.IsOutOfTheFight(playerUnit);
            }
            catch (Exception)
            {
                // A question that cannot be answered must not stop the fight; the adapter logs its own failure.
                return true;
            }
        }

        /// <summary>The same question about a <c>Player</c> rather than its unit, for the readers that walk the
        /// game's roster. A player without a unit is nobody to attack.</summary>
        public static bool IsFighting(Player? player) =>
            player != null && IsFighting(player.playerUnit);
    }
}
