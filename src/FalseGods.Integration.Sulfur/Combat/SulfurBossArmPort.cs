using System;
using System.Collections.Generic;
using FalseGods.Application.Combat;
using FalseGods.RuntimeContracts.Arena;
using Pathfinding;
using PerfectRandom.Sulfur.Core;
using PerfectRandom.Sulfur.Core.Units;
using UnityEngine;
using ILogger = FalseGods.RuntimeContracts.Diagnostics.ILogger;

namespace FalseGods.Integration.Sulfur.Combat
{
    /// <summary>
    /// The SULFUR implementation of <see cref="IBossArmPort"/>: the arms a starved boss puts up are the cave
    /// boss's own, raised through the game's own entry point.
    /// </summary>
    /// <remarks>
    /// <para><b>Nothing here drives the arm.</b> Measured on v0.18.5: the creature's animator starts in
    /// <c>Submerged</c>, rises into <c>Shoot</c> the moment its behaviour tree raises <c>Attack</c>, throws on an
    /// animation event, and sinks again when the clip ends — so appearing, throwing and sinking are the game's,
    /// repeating for as long as it has someone to aim at. The tree is already running when the unit is spawned
    /// (<c>SpawnUnit</c> activates it), and its only condition is a target within the creature's own ranged
    /// attack range. So raising an arm is a placement decision and nothing else.</para>
    /// <para><b>Which is why placement is the whole design.</b> The arm has no navigation agent at all — it cannot
    /// take one step — and it acquires its target through the game's ordinary line-of-sight detection. An arm put
    /// somewhere it cannot see the fight from is an arm that never throws, and there is no behaviour to fall back
    /// on. It is placed beside the boss, flanking the line between the boss and the fight, and brought down onto
    /// navigable ground the way the game's own arm spawner does.</para>
    /// <para><b>Nothing is invented about its damage or its aim.</b> The mud ball, its arc, its damage and its
    /// sound belong to the creature. A session layer that turns that one ball into one per player is likewise the
    /// session layer's business, and this asks for none of it.</para>
    /// <para><b>Host only.</b> The host owns enemies; a client's arms are mirrored puppets.</para>
    /// </remarks>
    public sealed class SulfurBossArmPort : IBossArmPort
    {
        /// <summary>How far above a candidate point to start looking for the floor, and how far down to look —
        /// the same shape of search the game's own arm spawner falls back to when it is off the graph.</summary>
        private const float GroundProbeRise = 2f;
        private const float GroundProbeReach = 5f;

        private readonly MonoBehaviour _host;
        private readonly ILogger? _logger;
        private readonly List<Unit> _raised = new List<Unit>();

        // Which raising the arms in flight belong to. An arm is loaded asynchronously, so a rage that ends while
        // one is still on its way would otherwise leave it standing and throwing after the boss had calmed: the
        // arrival checks that it is still wanted before it is kept.
        private int _generation;

        /// <param name="host">The behaviour whose lifetime scopes the asynchronous load — the game cancels the
        /// spawn if it is destroyed first, which is what should happen on a plugin unload.</param>
        public SulfurBossArmPort(MonoBehaviour host, ILogger? logger = null)
        {
            _host = host ?? throw new ArgumentNullException(nameof(host));
            _logger = logger;
        }

        public int Raised
        {
            get
            {
                Forget();
                return _raised.Count;
            }
        }

        public void Raise(ArenaWorldPoint around, int count, float sideDistance)
        {
            if (count <= 0)
            {
                return;
            }

            if (Raised > 0)
            {
                return; // already up; the rage raises them once and keeps them
            }

            var definition = UnitIds.GoblinCousinArm.GetAsset();
            if (definition == null)
            {
                _logger?.LogWarning("[arm] the game has no definition for the cousin's arm; the boss rages "
                    + "empty-handed.");
                return;
            }

            var centre = new Vector3(around.X, around.Y, around.Z);
            var sideways = SidewaysFrom(centre);
            var generation = ++_generation;

            for (var i = 0; i < count; i++)
            {
                // Alternate sides and step outwards, so two arms flank the boss and four make two ranks.
                var side = (i % 2 == 0) ? 1f : -1f;
                var rank = (i / 2) + 1;
                var wanted = centre + sideways * (side * sideDistance * rank);
                RaiseOne(definition, OnGround(wanted), generation);
            }

            _logger?.Log($"[arm] {count} arm(s) rising beside the boss at "
                + $"({centre.x:0.#}, {centre.y:0.#}, {centre.z:0.#}); they throw until it is supplied again.");
        }

        public void LowerAll()
        {
            // Anything still being loaded belongs to a rage that is over.
            _generation++;

            var lowered = 0;
            for (var i = 0; i < _raised.Count; i++)
            {
                var arm = _raised[i];
                if (arm == null)
                {
                    continue;
                }

                try
                {
                    // The game's own way of taking an arm away: dying is what plays the sink, and it works on a
                    // creature that cannot be killed by damage.
                    arm.Die();
                    lowered++;
                }
                catch (Exception exception)
                {
                    _logger?.LogWarning($"[arm] an arm could not be taken down ({exception.Message}); leaving it.");
                }
            }

            _raised.Clear();
            if (lowered > 0)
            {
                _logger?.Log($"[arm] {lowered} arm(s) sink back into the ground.");
            }
        }

        /// <summary>Fire the asynchronous spawn and keep the arm when it lands, if the rage that asked for it is
        /// still running. Deliberately not awaited: raising is a moment in the fight.</summary>
        private async void RaiseOne(UnitSO definition, Vector3 position, int generation)
        {
            Unit unit;
            try
            {
                unit = await definition.SpawnUnitAsync(_host, position);
            }
            catch (Exception exception)
            {
                _logger?.LogWarning($"[arm] an arm failed to rise: {exception.Message}");
                return;
            }

            if (unit == null)
            {
                _logger?.LogWarning("[arm] an arm failed to rise (the game returned no unit).");
                return;
            }

            if (generation != _generation)
            {
                // The boss was supplied again, or the fight ended, while this one was loading.
                try { unit.Die(); } catch (Exception) { }
                return;
            }

            // Belt and braces: the creature already carries enough health to be unkillable, and the game's own
            // spawners mark it invulnerable besides. An arm is scenery with a throw, not a health bar.
            try { unit.SetInvulnerable(state: true); }
            catch (Exception exception)
            {
                _logger?.LogWarning($"[arm] an arm could not be made invulnerable ({exception.Message}).");
            }

            _raised.Add(unit);
        }

        /// <summary>
        /// The horizontal axis to put arms along: across the line from the boss to the nearest player still in the
        /// fight, so they flank whoever the boss is facing rather than lining up behind it.
        /// </summary>
        /// <remarks>
        /// Falls back to a world axis when there is nobody to face or the boss is standing on top of them — an arm
        /// beside the boss is still an arm, and a degenerate cross product would put both in the same place.
        /// </remarks>
        private static Vector3 SidewaysFrom(Vector3 centre)
        {
            var player = FightingPlayers.NearestTo(centre);
            if (player != null)
            {
                var toPlayer = player.transform.position - centre;
                toPlayer.y = 0f;
                if (toPlayer.sqrMagnitude > 0.01f)
                {
                    return Vector3.Cross(Vector3.up, toPlayer).normalized;
                }
            }

            return Vector3.right;
        }

        /// <summary>
        /// Bring a candidate point down onto ground an arm can stand in, the way the game's own arm spawner does:
        /// the navigation graph first, because that is the surface the fight happens on, then a drop onto the
        /// level's geometry, and the point itself if the room answers neither.
        /// </summary>
        private Vector3 OnGround(Vector3 wanted)
        {
            try
            {
                var astar = AstarPath.active;
                if (astar != null)
                {
                    var nearest = astar.GetNearest(wanted, NNConstraint.Walkable);
                    if (nearest.node != null)
                    {
                        return nearest.node.ClosestPointOnNode(wanted);
                    }
                }

                var gameManager = StaticInstance<GameManager>.Instance;
                if (gameManager != null && Physics.Raycast(
                        wanted + Vector3.up * GroundProbeRise,
                        Vector3.down,
                        out var hit,
                        GroundProbeRise + GroundProbeReach,
                        gameManager.geometryLayer))
                {
                    return hit.point;
                }
            }
            catch (Exception exception)
            {
                _logger?.LogWarning($"[arm] an arm's footing could not be found ({exception.Message}); "
                    + "it rises where it was asked to.");
            }

            return wanted;
        }

        /// <summary>Drop the arms that are gone — taken down by us, or taken with a level.</summary>
        private void Forget()
        {
            for (var i = _raised.Count - 1; i >= 0; i--)
            {
                var arm = _raised[i];
                if (arm == null || !arm.IsAlive)
                {
                    _raised.RemoveAt(i);
                }
            }
        }
    }
}
