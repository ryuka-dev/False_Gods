using System;
using System.Collections.Generic;
using FalseGods.Application.Combat;
using FalseGods.Core.Bosses.Combat;
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
    /// <para><b>This is also where the roster lives</b> — see <see cref="Roster"/>. The encounter names a band and
    /// this side answers with creatures, because a creature is a game type and the layers that design the fight
    /// cannot name one.</para>
    /// </remarks>
    public sealed class SulfurMinionSpawnPort : IMinionSpawnPort
    {
        /// <summary>
        /// The roster: every hostile squad the boss can summon, in the game's own creatures.
        /// </summary>
        /// <remarks>
        /// <para><b>An explicit list, one entry per minion</b> — the vanilla cave boss's own idiom. Its
        /// <c>henchmenFirstSpawn</c>/<c>henchmenSecondSpawn</c> are authored <c>List&lt;UnitId&gt;</c>s spawned
        /// straight through, so the headcount <i>is</i> the list length and a wave is exactly reproducible. A
        /// weighted pool with a budget is the game's other idiom, and it is the one it uses for procedurally
        /// generated patrols; a designed fight wants the same fight twice.</para>
        /// <para><b>The ladder follows the game's own cave tiers</b>, not a scheme of ours: its
        /// <c>UnitPool_Caves_Tier1</c> is the rank and file (young, spearmen, archers), tier 2 adds the barrel boy,
        /// tier 3 adds the four wizards. So the three waves are those three tiers in order, and every creature
        /// here is one the game already considers native to a cave — which is also why none of them can fail to
        /// load in one.</para>
        /// <para><b>Escalation is composition, not headcount.</b> All three ordinary waves are seven strong,
        /// matching the vanilla boss's two seven-strong lists, which differ only in their mix. Measured in the
        /// game's own currency (<c>UnitSO.SpawnCost</c>, against which one vanilla cave patrol is worth fifteen)
        /// the three waves come to roughly 21, 31 and 45 — the wave gets harder without the floor getting more
        /// crowded, which is what keeps a room this size readable.</para>
        /// <para>Destined for authored boss content, like the crate constants. See <see cref="MinionBands"/>.</para>
        /// </remarks>
        private static readonly Dictionary<MinionBandId, UnitId[]> Roster =
            new Dictionary<MinionBandId, UnitId[]>
            {
                // Tier 1: numbers and spears. Nothing here outranges a player, so the first wave is entirely about
                // whether the party holds its ground while the room is filling up.
                [MinionBands.Vanguard] = new[]
                {
                    UnitIds.GoblinYoung,
                    UnitIds.GoblinYoung,
                    UnitIds.GoblinYoung,
                    UnitIds.GoblinYoung,
                    UnitIds.GoblinSpearman,
                    UnitIds.GoblinSpearman,
                    UnitIds.GoblinArcher,
                },

                // Tier 2: the smallest thin out, the spears thicken, and the heavy arrives. The barrel boy is the
                // one creature in the cave's own roster that does not simply walk at you, and at eight hundred
                // health it is the first summon a party cannot ignore its way past.
                [MinionBands.Warband] = new[]
                {
                    UnitIds.GoblinYoung,
                    UnitIds.GoblinYoung,
                    UnitIds.GoblinSpearman,
                    UnitIds.GoblinSpearman,
                    UnitIds.GoblinSpearman,
                    UnitIds.GoblinArcher,
                    UnitIds.GoblinBarrelBoy,
                },

                // Tier 3: all four casters at once, with just enough melee to stop the party standing still to
                // answer them. Every wizard is a different element, so this wave is four problems rather than one
                // problem four times.
                [MinionBands.Coven] = new[]
                {
                    UnitIds.GoblinSpearman,
                    UnitIds.GoblinSpearman,
                    UnitIds.GoblinArcher,
                    UnitIds.GoblinWizardFire,
                    UnitIds.GoblinWizardFrost,
                    UnitIds.GoblinWizardElectricity,
                    UnitIds.GoblinWizardPoison,
                },

                // The rage's band: small, heavy, and outlined. Killing it is half of what it takes to calm the
                // boss, and it is killed while the boss is hitting three times as hard — so it is five creatures
                // worth thirty-six rather than a seven-strong wave, and two of the five are the heavy, so the job
                // is short but not free.
                [MinionBands.Emergency] = new[]
                {
                    UnitIds.GoblinBarrelBoy,
                    UnitIds.GoblinBarrelBoy,
                    UnitIds.GoblinSpearman,
                    UnitIds.GoblinSpearman,
                    UnitIds.GoblinWizardPoison,
                },
            };

        /// <summary>
        /// How far apart minions are pushed when a band is larger than the room's authored places, and how much
        /// further out each further lap sits.
        /// </summary>
        /// <remarks>
        /// The room authors a handful of places and a band may outnumber them, so the places are reused. Vanilla
        /// lets its henchmen collide — it picks a spawner per unit at random and never checks — but its arena has
        /// far more of them, and two goblins born inside each other in a room with four places is visible. A lap
        /// number times a golden-angle turn spreads them without any randomness, which also keeps a wave's
        /// arrangement the same on every peer for free.
        /// </remarks>
        private const float PlaceReuseSpacing = 1.2f;

        private const float GoldenAngleDegrees = 137.507764f;

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

        public int SizeOf(MinionBandId band) => Roster.TryGetValue(band, out var members) ? members.Length : 0;

        public void Summon(MinionBandId band, IReadOnlyList<ArenaWorldPoint> places)
        {
            if (!Roster.TryGetValue(band, out var members))
            {
                // A band nobody wrote down is a wave that does not arrive, not a fight that stops. It can only
                // happen by naming a band in the itinerary and forgetting to add it here, so say which.
                _logger?.LogWarning($"[minion] the roster has no band called '{band}'; nothing summoned.");
                return;
            }

            if (places == null || places.Count == 0)
            {
                _logger?.LogWarning($"[minion] the {band} band was called up, but the room authored no places to "
                    + "put minions; nothing summoned.");
                return;
            }

            // Counted BEFORE any of this band lands, and through Alive so corpses do not count as a crowd. Both
            // matter: a spawn whose asset is already loaded completes synchronously, so a count taken after the
            // loop has some of the arriving band in it and reads as a floor fuller than it is.
            var standing = Alive;

            var summoned = 0;
            var cost = 0;
            var composition = new List<string>(members.Length);
            for (var i = 0; i < members.Length; i++)
            {
                var kind = members[i];
                var definition = kind.GetAsset();
                if (definition == null)
                {
                    // One kind the build does not have is one minion missing, not a summon that fails: the rest of
                    // the band still arrives.
                    _logger?.LogWarning($"[minion] the game has no definition for {kind.value}; that one is skipped.");
                    continue;
                }

                SpawnOne(definition, PlaceFor(places, i));
                composition.Add(definition.displayName ?? kind.value.ToString());
                cost += definition.SpawnCost;
                summoned++;
            }

            _logger?.Log($"[minion] the {band} band arrives: {summoned} of {members.Length} at {places.Count} "
                + $"authored place(s), worth {cost} (a vanilla cave patrol is 15) — {string.Join(", ", composition)}; "
                + $"{standing} of this port's were still standing when it was called.");
        }

        /// <summary>
        /// Where the <paramref name="index"/>th member of a band stands: the authored places in order, and once
        /// they run out, the same places again pushed a lap further out. See <see cref="PlaceReuseSpacing"/>.
        /// </summary>
        private static Vector3 PlaceFor(IReadOnlyList<ArenaWorldPoint> places, int index)
        {
            var place = places[index % places.Count];
            var at = new Vector3(place.X, place.Y, place.Z);

            var lap = index / places.Count;
            if (lap == 0)
            {
                return at;
            }

            // Height is left alone: the authored place is on the floor the room built, and pushing a minion up or
            // down from it would drop it through a terrace or stand it in the air.
            var turn = index * GoldenAngleDegrees * Mathf.Deg2Rad;
            var reach = PlaceReuseSpacing * lap;
            return new Vector3(at.x + Mathf.Cos(turn) * reach, at.y, at.z + Mathf.Sin(turn) * reach);
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
