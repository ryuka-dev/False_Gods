using System;
using FalseGods.RuntimeContracts.Multiplayer;
using PerfectRandom.Sulfur.Core;
using PerfectRandom.Sulfur.Core.Units;
using UnityEngine;

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

        /// <summary>
        /// The unit of the player closest to <paramref name="from"/> who is still in the fight, or null when the
        /// game lists nobody.
        /// </summary>
        /// <remarks>
        /// Read from the game's own player <i>roster</i> rather than its local-player singleton, because that
        /// roster is what a session registers its remote players into — so "nearest player" means nearest of
        /// everyone, on a host as well as in single-player. The singleton would quietly make every such decision
        /// about the host's own player and no one else's.
        /// </remarks>
        public static Unit? NearestTo(Vector3 from)
        {
            var gameManager = StaticInstance<GameManager>.Instance;
            var players = gameManager != null ? gameManager.Players : null;
            if (players == null)
            {
                return null;
            }

            Unit? nearest = null;
            var nearestDistance = float.MaxValue;
            for (var i = 0; i < players.Count; i++)
            {
                var player = players[i];
                var playerUnit = player != null ? player.playerUnit : null;
                if (playerUnit == null || !IsFighting(player))
                {
                    continue; // nobody aims at someone already lying on the floor
                }

                var distance = (playerUnit.transform.position - from).sqrMagnitude;
                if (distance < nearestDistance)
                {
                    nearestDistance = distance;
                    nearest = playerUnit;
                }
            }

            return nearest;
        }
    }

    /// <summary>
    /// Claiming the destructibles we make, so the session layer does not replicate them on top of us.
    /// </summary>
    /// <remarks>
    /// <para>A session layer mirrors destructible breaks by matching where an object was spawned — sound for a
    /// level's own breakables, wrong for ours: a boss's ammunition is heaped in one place, so the key matches
    /// whichever of the pile is nearest and a break on one peer destroyed an unrelated crate on the other. It was
    /// measured as crates bursting in mid-air and loot appearing with no shot fired.</para>
    /// <para>Set by the composition root while a session is live, like <see cref="FightingPlayers"/>. Without one
    /// there is nobody to claim them from, and claiming is a no-op.</para>
    /// </remarks>
    public static class OurDestructibles
    {
        private static IDestructibleOwnership? _ownership;

        public static void ClaimedWith(IDestructibleOwnership? ownership) => _ownership = ownership;

        /// <summary>Say that this destructible is ours to replicate. Safe with no session and safe to repeat.</summary>
        public static void Claim(GameObject? destructible)
        {
            if (destructible == null)
            {
                return;
            }

            try
            {
                _ownership?.ClaimAsOurs(destructible);
            }
            catch (Exception)
            {
                // The adapter reports its own failure; an unclaimed crate is a worse fight, not a broken one.
            }
        }
    }
}
