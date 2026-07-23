#nullable disable

using System;
using FalseGods.Application.Combat;
using FalseGods.RuntimeContracts.Arena;
using PerfectRandom.Sulfur.Core;
using PerfectRandom.Sulfur.Core.Stats;
using PerfectRandom.Sulfur.Core.Units;
using PerfectRandom.Sulfur.Core.Weapons;
using UnityEngine;
using ILogger = FalseGods.RuntimeContracts.Diagnostics.ILogger;

namespace FalseGods.Integration.Sulfur.Combat
{
    /// <summary>
    /// <see cref="IThrownCrateImpact"/> over the game's own player: where a crate lands it splashes a circle,
    /// dealing damage through <c>Unit.ReceiveDamage</c> (the canonical entry point, the same one the boss uses) and
    /// shoving anyone inside it away by handing a momentum to their movement controller.
    /// </summary>
    /// <remarks>
    /// <para><b>Reach.</b> A flat circle of a fixed radius on the ground, centred on the landing point — the crate
    /// need not land dead on the player, only near them, and a crate arcing high overhead is irrelevant because the
    /// splash only happens where it comes down.</para>
    /// <para><b>Knockback.</b> The movement controller's own <c>SetMomentum</c> is the game's way to launch a
    /// player (the same lever the boss spider's collision uses); the controller then bleeds that momentum off with
    /// its normal friction and gravity, so the push decays on its own with no control taken away and no timer to
    /// restore. A little lift is added so the shove reads as a knock rather than a slide.</para>
    /// <para>Single-player and host apply this directly; wiring it through host-authoritative routing for a real
    /// multiplayer session is the boss-attach step's concern, like the rest of the crate bring-up.</para>
    /// </remarks>
    public sealed class SulfurCrateImpact : IThrownCrateImpact
    {
        private readonly int _damage;
        private readonly float _radius;
        private readonly float _knockbackSpeed;
        private readonly float _knockbackLift;
        private readonly ILogger _logger;

        public SulfurCrateImpact(int damage, float radius, float knockbackSpeed, float knockbackLift, ILogger logger = null)
        {
            _damage = damage;
            _radius = radius;
            _knockbackSpeed = knockbackSpeed;
            _knockbackLift = knockbackLift;
            _logger = logger;
        }

        public bool Splash(ArenaWorldPoint at)
        {
            var gameManager = StaticInstance<GameManager>.Instance;
            var players = gameManager != null ? gameManager.Players : null;
            if (players == null)
            {
                return false;
            }

            var impact = new Vector3(at.X, at.Y, at.Z);
            var radiusSquared = _radius * _radius;
            var caught = false;

            for (var index = 0; index < players.Count; index++)
            {
                var player = players[index];
                if (player == null || player.playerUnit == null)
                {
                    continue;
                }

                // A flat circle on the ground: only the horizontal distance to the landing point matters.
                var toPlayer = player.transform.position - impact;
                toPlayer.y = 0f;
                if (toPlayer.sqrMagnitude > radiusSquared)
                {
                    continue;
                }

                Strike(player, impact);
                caught = true;
            }

            return caught;
        }

        private void Strike(Player player, Vector3 impact)
        {
            try
            {
                // A non-player source so the hit is not friendly-fire-blocked; Physics is a plain hit the game then
                // reduces by its own armour/resistance rules.
                player.playerUnit.ReceiveDamage(
                    _damage, DamageTypes.Physics, new CrateDamager(player.transform), Hitmesh.Data.Default);
            }
            catch (Exception exception)
            {
                _logger?.LogWarning($"[crate] a crate hit could not deal damage ({exception.Message}); skipped.");
            }

            try
            {
                var controller = player.movement;
                if (controller != null)
                {
                    // Away from the crate along the ground, plus a little lift so it launches rather than slides.
                    var away = player.transform.position - impact;
                    away.y = 0f;
                    var direction = away.sqrMagnitude > 1e-4f ? away.normalized : Vector3.forward;
                    controller.SetMomentum(direction * _knockbackSpeed + Vector3.up * _knockbackLift);
                    _logger?.Log($"[crate] splash hit a player for {_damage}, knockback {_knockbackSpeed:0.#} + lift {_knockbackLift:0.#}.");
                }
            }
            catch (Exception exception)
            {
                _logger?.LogWarning($"[crate] a crate hit could not knock the player back ({exception.Message}); skipped.");
            }
        }

        /// <summary>The crate as a damage source: a non-player <see cref="IDamager"/> with no owning unit or weapon,
        /// so <c>ReceiveDamage</c> attributes the hit to a non-player and does not friendly-fire-block it.</summary>
        private sealed class CrateDamager : IDamager
        {
            private readonly Transform _transform;

            public CrateDamager(Transform transform) => _transform = transform;

            public string SourceName => "False Gods crate";

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
