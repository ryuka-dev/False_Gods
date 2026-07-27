using System;
using System.Collections.Generic;
using FalseGods.Application.Combat;
using FalseGods.RuntimeContracts.Arena;
using PerfectRandom.Sulfur.Core;
using PerfectRandom.Sulfur.Core.Units;
using UnityEngine;
using ILogger = FalseGods.RuntimeContracts.Diagnostics.ILogger;

namespace FalseGods.Integration.Sulfur.Combat
{
    /// <summary>
    /// The SULFUR implementation of <see cref="IMinionSpawnPort"/>: the boss's minions are the game's own goblins,
    /// spawned through the game's own entry point.
    /// </summary>
    /// <remarks>
    /// <para><b>Why real units.</b> <c>UnitId.GetAsset().SpawnUnitAsync(mono, position)</c> is what the vanilla
    /// bosses use for their own henchmen (measured on v0.18.5: the witch's <c>SpawnHenchmenAsync</c>). Everything
    /// that makes a minion interesting comes free from taking that path — it walks the level's navigation, takes
    /// the terrace jump links, is shot by real weapons, drops real loot, and is replicated by the session layer
    /// like any other enemy. The same reason the thrown crates are real <c>Breakable</c>s rather than props.</para>
    /// <para><b>Asynchronous by nature.</b> The unit's asset is loaded on demand, so a summon is a request: the
    /// call returns before the minion exists. A load that fails is logged and dropped — a boss missing a minion is
    /// a worse fight, not a broken one.</para>
    /// <para><b>Ownership.</b> Every minion this port spawned is tracked so the encounter can take them with it
    /// when it ends. Units that died in the meantime are forgotten as they are found.</para>
    /// </remarks>
    public sealed class SulfurMinionSpawnPort : IMinionSpawnPort
    {
        // The cave's own rank and file. A goblin spearman closes the distance, which is what makes the terraces
        // and their jump links part of the fight. Destined for authored boss content, like the crate constants.
        private static readonly UnitId MinionUnit = UnitIds.GoblinSpearman;

        private readonly MonoBehaviour _host;
        private readonly ILogger? _logger;
        private readonly List<Unit> _spawned = new List<Unit>();

        /// <param name="host">The behaviour whose lifetime scopes the asynchronous load — the game cancels the
        /// spawn if it is destroyed first, which is exactly the behaviour we want on a plugin unload.</param>
        public SulfurMinionSpawnPort(MonoBehaviour host, ILogger? logger = null)
        {
            _host = host ?? throw new ArgumentNullException(nameof(host));
            _logger = logger;
        }

        public int Alive
        {
            get
            {
                Forget(dead: true);
                return _spawned.Count;
            }
        }

        public void Summon(IReadOnlyList<ArenaWorldPoint> at)
        {
            if (at == null || at.Count == 0)
            {
                _logger?.LogWarning("[minion] nothing summoned: the room authored no places to put minions.");
                return;
            }

            var definition = MinionUnit.GetAsset();
            if (definition == null)
            {
                _logger?.LogWarning($"[minion] the game has no definition for {MinionUnit.value}; nothing summoned.");
                return;
            }

            for (var i = 0; i < at.Count; i++)
            {
                SpawnOne(definition, new Vector3(at[i].X, at[i].Y, at[i].Z));
            }

            _logger?.Log($"[minion] {at.Count} minion(s) requested; {_spawned.Count} already alive.");
        }

        public void DespawnAll()
        {
            var removed = 0;
            for (var i = 0; i < _spawned.Count; i++)
            {
                var unit = _spawned[i];
                if (unit == null)
                {
                    continue;
                }

                try
                {
                    UnityEngine.Object.Destroy(unit.gameObject);
                    removed++;
                }
                catch (Exception exception)
                {
                    _logger?.LogWarning($"[minion] a minion could not be removed ({exception.Message}); leaving it.");
                }
            }

            _spawned.Clear();
            if (removed > 0)
            {
                _logger?.Log($"[minion] {removed} minion(s) removed with the encounter.");
            }
        }

        /// <summary>Fire the asynchronous spawn and record the result when it lands. Deliberately not awaited: a
        /// summon is a moment in the fight, not something the boss waits on.</summary>
        private async void SpawnOne(UnitSO definition, Vector3 position)
        {
            Unit unit;
            try
            {
                unit = await definition.SpawnUnitAsync(_host, position);
            }
            catch (Exception exception)
            {
                _logger?.LogWarning($"[minion] a minion failed to spawn: {exception.Message}");
                return;
            }

            if (unit == null)
            {
                _logger?.LogWarning("[minion] a minion failed to spawn (the game returned no unit).");
                return;
            }

            SendItAfterThePlayers(unit);
            _spawned.Add(unit);
        }

        /// <summary>
        /// Point a freshly summoned minion at the fight. A goblin that walked into the room on its own has to
        /// notice someone first, which is right for a wandering enemy and wrong for one that was <i>called</i>:
        /// summoned on a terrace forty metres away and across a corner, it would stand there.
        /// </summary>
        /// <remarks>
        /// <para><b>One call, and deliberately nothing else.</b> Reporting a sighting is what the game does to a
        /// unit that should already know where someone is: it hands the agent a target if it has none and records
        /// the position in its memory, so the minion sets off instead of waiting to notice somebody. Everything
        /// after that — who it actually chases once it arrives, and re-targeting — stays with the game's own
        /// detection, which reads the whole player roster and is therefore multiplayer-aware.</para>
        /// <para><b>The two flags the vanilla summon sites set alongside this are a trap and are not set here.</b>
        /// <c>onlyTargetPlayer</c> does not mean "only interested in players": <c>AiAgent.GetTarget</c> reads it as
        /// "the target is <c>GameManager.PlayerUnit</c>", the <i>local</i> player singleton, hardcoded. With
        /// <c>useLineOfSight = false</c> beside it that becomes unconditional, so on a host every minion charges
        /// the host's own player forever and no one else's — measured, after this code did exactly that.
        /// <c>useLineOfSight</c> is also what gates the sighting being propagated to the rest of the group, so
        /// clearing it quietly costs that too.</para>
        /// <para>Host-only, like the spawn itself: a client's minions are puppets whose AI the session layer
        /// disables, so nothing here would mean anything there.</para>
        /// </remarks>
        private void SendItAfterThePlayers(Unit unit)
        {
            try
            {
                var npc = unit as Npc;
                var agent = npc != null ? npc.AiAgent : null;
                if (agent == null)
                {
                    return; // not a unit that chases anything
                }

                var nearest = NearestPlayerUnitTo(unit.transform.position);
                if (nearest != null)
                {
                    agent.ReportLastSeen(nearest, nearest.transform.position, unit.transform.position, true);
                }
            }
            catch (Exception exception)
            {
                // A minion that has to notice the players by itself is a worse fight, not a broken one.
                _logger?.LogWarning($"[minion] a minion could not be pointed at the fight ({exception.Message}); "
                    + "it will have to notice the players on its own.");
            }
        }

        /// <summary>
        /// The player unit closest to <paramref name="from"/>, or null when the game lists none. Read from the
        /// game's own player roster rather than its local-player singleton, because that roster is what a session
        /// registers its remote players into — so "nearest player" means nearest of <i>everyone</i>.
        /// </summary>
        private static Unit? NearestPlayerUnitTo(Vector3 from)
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

            Unit? nearest = null;
            var nearestDistance = float.MaxValue;
            for (var i = 0; i < players.Count; i++)
            {
                var player = players[i];
                var playerUnit = player != null ? player.playerUnit : null;
                if (playerUnit == null || !FightingPlayers.IsFighting(player))
                {
                    continue; // nobody sends minions after someone already lying on the floor
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

        /// <summary>Drop the entries whose units have gone — destroyed by us, killed by a player, or taken with a
        /// level.</summary>
        private void Forget(bool dead)
        {
            for (var i = _spawned.Count - 1; i >= 0; i--)
            {
                if (_spawned[i] == null)
                {
                    _spawned.RemoveAt(i);
                }
            }
        }
    }
}
