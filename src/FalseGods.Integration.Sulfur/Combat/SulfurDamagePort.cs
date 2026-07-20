using System;
using FalseGods.Application.Combat;
using FalseGods.Core.Simulation;
using PerfectRandom.Sulfur.Core;
using PerfectRandom.Sulfur.Core.Stats;
using PerfectRandom.Sulfur.Core.Units;
using PerfectRandom.Sulfur.Core.Weapons;
using UnityEngine;
using ILogger = FalseGods.RuntimeContracts.Diagnostics.ILogger;

namespace FalseGods.Integration.Sulfur.Combat
{
    /// <summary>
    /// The single-player / host implementation of <see cref="IDamagePort"/>: it maps a boss
    /// <see cref="ParticipantId"/> to the game <c>Player</c> and applies the hit through the game's own
    /// <c>Unit.ReceiveDamage</c> — the canonical player-damage entry point (Docs/Architecture.md §6).
    /// </summary>
    /// <remarks>
    /// The mapping is <c>ParticipantId.Value == Player.playerIndex</c> (the same projection
    /// <see cref="Simulation.SulfurParticipantQuery"/> reads), against the <b>public</b> <c>GameManager.Players</c>
    /// surface — compile-time typed, no reflection. A <c>Player</c> holds its live <c>Unit</c> in the public
    /// <c>playerUnit</c> field; that is what takes damage.
    ///
    /// <para>
    /// The boss is not a game <c>Unit</c>, so it damages the player through a minimal non-player
    /// <see cref="IDamager"/> source. SULFUR's <c>Unit.ReceiveDamage(IDamager)</c> uses the source only to decide
    /// the hit is not friendly fire (SULFUR Together's <c>NetPlayerLifeManager</c> confirms this against the same
    /// build): a source that was not created by a player passes, and a non-<c>None</c> damage type is required. The
    /// game then applies its own model (type reduction, armour, resistances, safe-zone/amulet immunity) — the boss
    /// decides how much it <i>deals</i>, the game decides how much health is <i>lost</i>. The call is wrapped
    /// defensively; a surprise in the base-game damage path is logged and swallowed rather than tearing down the
    /// boss loop (Docs/DependencyRules.md §9, §13).
    /// </para>
    /// </remarks>
    public sealed class SulfurDamagePort : IDamagePort
    {
        private readonly ILogger? _logger;

        public SulfurDamagePort(ILogger? logger)
        {
            _logger = logger;
        }

        public void ApplyDamage(ParticipantId target, int amount)
        {
            if (amount <= 0)
            {
                return;
            }

            var player = FindPlayer(target.Value);
            if (player == null || player.playerUnit == null)
            {
                _logger?.LogWarning($"Boss damage: no live player for participant {target.Value}; hit not applied.");
                return;
            }

            try
            {
                // A non-player IDamager satisfies ReceiveDamage's not-friendly-fire requirement; the victim's own
                // transform is handed in so any position use in the damage path is null-safe. Physics is a plain
                // hit; the game reduces it by its own rules.
                player.playerUnit.ReceiveDamage(
                    amount, DamageTypes.Physics, new BossDamager(player.transform), Hitmesh.Data.Default);
            }
            catch (Exception exception)
            {
                _logger?.LogWarning($"Boss damage: applying {amount} to player {target.Value} threw ({exception.Message}); skipped.");
            }
        }

        private static Player? FindPlayer(int playerIndex)
        {
            var gameManager = StaticInstance<GameManager>.Instance;
            if (gameManager == null)
            {
                return null;
            }

            var players = gameManager.Players;
            if (players == null)
            {
                return null;
            }

            for (var i = 0; i < players.Count; i++)
            {
                var player = players[i];
                if (player != null && player.playerIndex == playerIndex)
                {
                    return player;
                }
            }

            return null;
        }

        /// <summary>
        /// The boss as a damage source: a non-player <see cref="IDamager"/> with no owning unit or weapon. It exists
        /// only so <c>Unit.ReceiveDamage</c> attributes the hit to a non-player and does not friendly-fire-block it;
        /// <see cref="Transform"/> is the victim's, so any position read in the damage path is null-safe.
        /// </summary>
        private sealed class BossDamager : IDamager
        {
            private readonly Transform _transform;

            public BossDamager(Transform transform) => _transform = transform;

            public string SourceName => "False Gods boss";

            public Unit SourceUnit => null;

            public Weapon SourceWeapon => null;

            public Transform Transform => _transform;

            public bool CreatedByPlayer => false;

            public void SetOwner(Unit unit)
            {
            }
        }
    }
}
