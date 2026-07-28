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
        /// <summary>
        /// The cave's own rank and file, and deliberately more than one kind of it.
        /// </summary>
        /// <remarks>
        /// A band of identical spearmen is one problem repeated: solve the approach once and every summon after it
        /// is the same fight. A mixed band asks two questions at once — the spearmen close, so the terraces and
        /// their jump links matter, while the archers and casters punish standing still to deal with them. The
        /// vanilla cousin does exactly this, picking each henchman at random from a list of its own, which is also
        /// where the shape of this came from.
        /// <para>Picked per minion rather than per summon, so a wave is mixed rather than uniform. The host
        /// chooses and the session layer mirrors what it spawned, so no shared seed is needed for the peers to
        /// agree — unlike the crates, which every peer builds for itself.</para>
        /// <para>Destined for authored boss content, like the crate constants.</para>
        /// </remarks>
        private static readonly UnitId[] MinionUnits =
        {
            UnitIds.GoblinSpearman,
            UnitIds.GoblinSpearman,   // twice: the melee that uses the room stays the backbone of a wave
            UnitIds.GoblinArcher,
            UnitIds.GoblinWizardFire,
        };

        private readonly MonoBehaviour _host;
        private readonly ILogger? _logger;
        private readonly bool _outlined;
        private readonly List<Unit> _spawned = new List<Unit>();

        // The renderers handed to the game's outline pass, kept per minion so exactly what was added is taken back
        // when it dies. Reading them off a corpse would be too late: the object is on its way out and its
        // renderers may already be gone, and an entry left in that list outlives the fight.
        private readonly Dictionary<Unit, Renderer[]> _outlines = new Dictionary<Unit, Renderer[]>();

        /// <param name="host">The behaviour whose lifetime scopes the asynchronous load — the game cancels the
        /// spawn if it is destroyed first, which is exactly the behaviour we want on a plugin unload.</param>
        /// <param name="outlined">Whether these minions are drawn with the game's own outline, the one the church
        /// puts around a piece of furniture you have just unlocked: a bright silhouette that reads through walls.
        /// A band whose death is the price of calming the boss is worth telling players apart from the ordinary
        /// wave beside it, and the game already has the vocabulary for "this one, first".</param>
        public SulfurMinionSpawnPort(MonoBehaviour host, ILogger? logger = null, bool outlined = false)
        {
            _host = host ?? throw new ArgumentNullException(nameof(host));
            _logger = logger;
            _outlined = outlined;
        }

        public int Alive
        {
            get
            {
                Forget();
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

            var summoned = 0;
            var band = new List<string>(at.Count);
            for (var i = 0; i < at.Count; i++)
            {
                var kind = MinionUnits[UnityEngine.Random.Range(0, MinionUnits.Length)];
                var definition = kind.GetAsset();
                if (definition == null)
                {
                    // One kind the build does not have is one minion missing, not a summon that fails: the rest of
                    // the band still arrives.
                    _logger?.LogWarning($"[minion] the game has no definition for {kind.value}; that one is skipped.");
                    continue;
                }

                SpawnOne(definition, new Vector3(at[i].X, at[i].Y, at[i].Z));
                band.Add(definition.displayName ?? kind.value.ToString());
                summoned++;
            }

            _logger?.Log($"[minion] {summoned} of {at.Count} minion(s) requested ({string.Join(", ", band)}); "
                + $"{_spawned.Count} already alive.");
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

            foreach (var outlined in _outlines.Keys)
            {
                Unoutline(outlined, keepEntry: true);
            }

            _outlines.Clear();
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
            Outline(unit);
        }

        /// <summary>
        /// Draw this minion with the game's own outline — the silhouette the church puts around furniture you have
        /// just unlocked, which reads through walls and through a crowd.
        /// </summary>
        /// <remarks>
        /// Presentation only, and failing at it costs nothing but the hint: a build whose renderer feature is not
        /// there still gets the fight, just without the marker. The renderers are remembered rather than looked up
        /// again later, because by the time a minion is dead its renderers are on their way out and an entry left
        /// in the game's list would outlive the fight.
        /// </remarks>
        private void Outline(Unit unit)
        {
            if (!_outlined || unit == null)
            {
                return;
            }

            try
            {
                var rendering = SulfurCustomRendering.instance;
                if (rendering == null)
                {
                    return;
                }

                var renderers = unit.GetComponentsInChildren<Renderer>(includeInactive: true);
                if (renderers == null || renderers.Length == 0)
                {
                    return;
                }

                rendering.AddOutlinedRenderers(renderers);
                _outlines[unit] = renderers;
            }
            catch (Exception exception)
            {
                _logger?.LogWarning($"[minion] one could not be outlined ({exception.Message}); it fights unmarked.");
            }
        }

        /// <summary>Take a minion's renderers back out of the game's outline list. Idempotent, and safe on a unit
        /// that was never outlined or is already destroyed.</summary>
        private void Unoutline(Unit? unit, bool keepEntry = false)
        {
            if (unit == null || _outlines.Count == 0)
            {
                return;
            }

            if (!_outlines.TryGetValue(unit, out var renderers))
            {
                return;
            }

            try { SulfurCustomRendering.instance?.RemoveOutlinedRenderers(renderers); }
            catch (Exception exception)
            {
                _logger?.LogWarning($"[minion] an outline could not be cleared ({exception.Message}).");
            }

            if (!keepEntry)
            {
                _outlines.Remove(unit);
            }
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

                var nearest = FightingPlayers.NearestTo(unit.transform.position);
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

        /// <summary>Drop the entries whose units have gone — destroyed by us, killed by a player, or taken with a
        /// level.</summary>
        /// <summary>
        /// Drop the ones that are no longer a threat.
        /// </summary>
        /// <remarks>
        /// <b>Dead is not the same as gone.</b> A killed goblin leaves a body behind — the object outlives the
        /// creature by a death animation at least — so counting whatever has not been destroyed yet counts
        /// corpses. That reads as minions still standing: summons that should top a wave up are refused because
        /// the wave looks full, and anything waiting for a band to be finished waits forever.
        /// </remarks>
        private void Forget()
        {
            for (var i = _spawned.Count - 1; i >= 0; i--)
            {
                var unit = _spawned[i];
                if (unit == null || !unit.IsAlive)
                {
                    Unoutline(unit);
                    _spawned.RemoveAt(i);
                }
            }
        }
    }
}
