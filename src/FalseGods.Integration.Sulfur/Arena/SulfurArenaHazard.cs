#nullable disable

using System;
using System.Collections.Generic;
using FalseGods.Integration.Sulfur.Combat;
using PerfectRandom.Sulfur.Core;
using PerfectRandom.Sulfur.Core.Stats;
using PerfectRandom.Sulfur.Core.Units;
using PerfectRandom.Sulfur.Core.Weapons;
using UnityEngine;
using ILogger = FalseGods.RuntimeContracts.Diagnostics.ILogger;

namespace FalseGods.Integration.Sulfur.Arena
{
    /// <summary>
    /// The arena's standing hazards: places that hurt a player for being in them. Today that is the sludge the
    /// boss rises out of — stand in the mud and it burns.
    /// </summary>
    /// <remarks>
    /// <para><b>The shape is the donor's, the damage is ours.</b> Each volume is a sphere a vanilla prop already
    /// authored, cloned in with the prop and re-centred over it, so nobody had to invent where the mud is or how
    /// far it reaches. The amounts come from the same donor — five points every quarter second, and only to
    /// players, so the boss's own minions wade through their master's pool unharmed. What is not reused is the
    /// donor's <i>mechanism</i>: its component damages whatever a physics trigger reports to it, and cloned into
    /// this arena it reported nothing. Rather than keep guessing at a component we cannot instrument, the hit goes
    /// through the path this project already damages players with — the game's own <c>Unit.ReceiveDamage</c>, the
    /// same one the boss and its crates use.</para>
    /// <para><b>Only where the world is ours.</b> Hurting a player is a shared-world decision, settled on the host
    /// exactly as the boss's own hits are; the composition root ticks this only there. A client's copy of the pool
    /// is scenery.</para>
    /// <para>A player who is down is not burned: they are out of the fight, and the same gate that stops the boss
    /// attacking them stops the mud.</para>
    /// </remarks>
    public sealed class SulfurArenaHazard
    {
        private readonly Func<GameObject> _arenaRoot;
        private readonly string _volumeParentPath;
        private readonly string _volumeName;
        private readonly int _damage;
        private readonly float _interval;
        private readonly ILogger _logger;

        private readonly List<SphereCollider> _volumes = new List<SphereCollider>();

        // Who is standing in it, and what their health was when they stepped in — so leaving reports what the
        // visit actually cost. A hazard's number is only tunable if somebody can see what it does.
        private readonly Dictionary<int, Visit> _burning = new Dictionary<int, Visit>();

        private GameObject _knownRoot;
        private float _sinceLastTick;

        public SulfurArenaHazard(
            Func<GameObject> arenaRoot,
            string volumeParentPath,
            string volumeName,
            int damage,
            float interval,
            ILogger logger = null)
        {
            _arenaRoot = arenaRoot ?? throw new ArgumentNullException(nameof(arenaRoot));
            _volumeParentPath = volumeParentPath;
            _volumeName = volumeName;
            _damage = damage;
            _interval = Mathf.Max(0.02f, interval);
            _logger = logger;
        }

        /// <summary>Advance the hazards by one frame. Cheap when there is no arena standing, and cheap between
        /// ticks: the volumes are only searched for when the arena changes, and players are only measured on the
        /// interval the damage actually lands on.</summary>
        public void Advance(float deltaTime)
        {
            var root = _arenaRoot();
            if (root == null)
            {
                Forget();
                return;
            }

            if (!ReferenceEquals(root, _knownRoot))
            {
                Collect(root);
            }

            if (_volumes.Count == 0)
            {
                return;
            }

            _sinceLastTick += deltaTime;
            if (_sinceLastTick < _interval)
            {
                return;
            }

            _sinceLastTick = 0f;
            Burn();
        }

        /// <summary>Drop what was found for an arena that is gone, so the next one is searched afresh.</summary>
        public void Forget()
        {
            _knownRoot = null;
            _volumes.Clear();
            _burning.Clear();
            _sinceLastTick = 0f;
        }

        private void Collect(GameObject root)
        {
            _knownRoot = root;
            _volumes.Clear();
            _burning.Clear();

            var parent = string.IsNullOrEmpty(_volumeParentPath)
                ? root.transform
                : root.transform.Find(_volumeParentPath);
            if (parent == null)
            {
                _logger?.Log($"[hazard] no '{_volumeParentPath}' in this arena; nothing burns.");
                return;
            }

            foreach (var child in parent.GetComponentsInChildren<Transform>(includeInactive: true))
            {
                if (!string.Equals(child.name, _volumeName, StringComparison.Ordinal))
                {
                    continue;
                }

                var sphere = child.GetComponent<SphereCollider>();
                if (sphere == null)
                {
                    continue;
                }

                _volumes.Add(sphere);
                _logger?.Log($"[hazard] '{_volumeName}' reaches {WorldRadius(sphere):0.#} around "
                    + $"{sphere.bounds.center.ToString("0.#")}");
            }

            _logger?.Log($"[hazard] {_volumes.Count} volume(s) burning for {_damage} every {_interval:0.##}s");
        }

        private void Burn()
        {
            var gameManager = StaticInstance<GameManager>.Instance;
            var players = gameManager != null ? gameManager.Players : null;
            if (players == null)
            {
                return;
            }

            for (var index = 0; index < players.Count; index++)
            {
                var player = players[index];
                if (player == null || player.playerUnit == null || !FightingPlayers.IsFighting(player))
                {
                    continue;
                }

                var id = player.GetInstanceID();
                if (!Inside(player.transform.position))
                {
                    if (_burning.TryGetValue(id, out var finished))
                    {
                        _burning.Remove(id);
                        _logger?.Log($"[hazard] a player left the sludge after {finished.Ticks} tick(s): "
                            + $"health {finished.HealthOnEntry:0.##} -> {Health(player):0.##}");
                    }

                    continue;
                }

                if (!_burning.TryGetValue(id, out var visit))
                {
                    visit = new Visit(Health(player));
                    _logger?.Log($"[hazard] a player is standing in the sludge at {player.transform.position.ToString("0.#")}.");
                }

                Strike(player);
                _burning[id] = visit.OneMore();
            }
        }

        /// <summary>Whether a point is inside any volume. Measured in three dimensions against the sphere the
        /// donor authored — a pool is a bowl, so its reach upwards is as much a part of its shape as its
        /// width.</summary>
        private bool Inside(Vector3 point)
        {
            for (var index = 0; index < _volumes.Count; index++)
            {
                var sphere = _volumes[index];
                if (sphere == null)
                {
                    continue;
                }

                var radius = WorldRadius(sphere);
                if ((sphere.bounds.center - point).sqrMagnitude <= radius * radius)
                {
                    return true;
                }
            }

            return false;
        }

        private void Strike(Player player)
        {
            try
            {
                // Normal damage from a non-player source, as the donor's own volume dealt it: the game then
                // applies its own armour and resistance rules on top.
                var before = Health(player);
                var accepted = player.playerUnit.ReceiveDamage(
                    _damage, DamageTypes.Normal, new SludgeDamager(player.transform), Hitmesh.Data.Default);
                var after = Health(player);

                // Reported rather than assumed. Asking the game to damage somebody and watching it happen are two
                // different things: the hit can be refused outright, or accepted and then reduced to nothing by
                // the player's own resistances — and from the outside both look identical to a player who says
                // the mud does not hurt.
                if (!accepted || after >= before)
                {
                    _logger?.Log($"[hazard] the game did not take {_damage} off a player: accepted={accepted}, "
                        + $"health {before:0.##} -> {after:0.##}");
                }
            }
            catch (Exception exception)
            {
                _logger?.LogWarning($"[hazard] the sludge could not burn a player ({exception.Message}); skipped.");
            }
        }

        /// <summary>One stay in a hazard: what the player had when they stepped in, and how many times it has
        /// burned them since.</summary>
        private readonly struct Visit
        {
            public Visit(float healthOnEntry) : this(healthOnEntry, 0)
            {
            }

            private Visit(float healthOnEntry, int ticks)
            {
                HealthOnEntry = healthOnEntry;
                Ticks = ticks;
            }

            public float HealthOnEntry { get; }

            public int Ticks { get; }

            public Visit OneMore() => new Visit(HealthOnEntry, Ticks + 1);
        }

        /// <summary>A player's current health, or a negative number when it cannot be read — never mistaken for
        /// a real reading.</summary>
        private static float Health(Player player)
        {
            try
            {
                var stats = player.playerUnit.Stats;
                return stats == null ? -1f : stats.GetStatus(EntityAttributes.Status_CurrentHealth);
            }
            catch (Exception)
            {
                return -1f;
            }
        }

        /// <summary>A sphere collider's radius in world units — Unity scales it by the largest of the three
        /// lossy-scale axes, so a prop placed larger reaches proportionally further.</summary>
        private static float WorldRadius(SphereCollider sphere)
        {
            var scale = sphere.transform.lossyScale;
            return sphere.radius * Mathf.Max(Mathf.Abs(scale.x), Mathf.Max(Mathf.Abs(scale.y), Mathf.Abs(scale.z)));
        }

        /// <summary>The sludge as a damage source: a non-player <see cref="IDamager"/> with no owning unit or
        /// weapon, so the hit is attributed to the world and is not friendly-fire-blocked.</summary>
        private sealed class SludgeDamager : IDamager
        {
            private readonly Transform _transform;

            public SludgeDamager(Transform transform) => _transform = transform;

            public string SourceName => "False Gods sludge";

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
