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

        // The rim built around each marked creature, one object per minion, destroyed with it.
        private readonly Dictionary<Unit, GameObject> _halos = new Dictionary<Unit, GameObject>();

        /// <summary>How much larger the rim copy is than the picture it rings. Enough to read across a room,
        /// little enough that it stays a rim rather than a shadow.</summary>
        private const float HaloScale = 1.12f;

        /// <summary>Flat-colour shaders the build is likely to keep, best first — the same chain the boss's own
        /// art resolves through, because that one was measured against this game rather than assumed.</summary>
        private static readonly string[] HaloShaders =
        {
            "Universal Render Pipeline/Unlit",
            "Sprites/Default",
            "Unlit/Color",
            "Hidden/Internal-Colored",
        };

        private const string BaseColourProperty = "_BaseColor";
        private const string ColourProperty = "_Color";

        // Looked up once for the whole session. Shader.Find walks every shader the build has, which is far too
        // much to do while a creature is arriving — it was measured as a hitch on every summon.
        private static Shader? _haloShader;
        private static bool _haloShaderSearched;

        private static Shader? HaloShaderOrNull()
        {
            if (_haloShaderSearched)
            {
                return _haloShader;
            }

            _haloShaderSearched = true;
            foreach (var name in HaloShaders)
            {
                var shader = Shader.Find(name);
                if (shader != null && shader.isSupported)
                {
                    _haloShader = shader;
                    break;
                }
            }

            return _haloShader;
        }

        // One line per session about how the rim was built, or why it was not. Repeating it once a minion would
        // say nothing new and drown the log.
        private bool _reported;

        private void ReportOnce(string what)
        {
            if (_reported)
            {
                return;
            }

            _reported = true;
            _logger?.Log($"[minion] {what}.");
        }

        /// <summary>What "kill this one first" looks like. Warm and bright against a cave.</summary>
        private static readonly Color PriorityColour = new Color(1f, 0.45f, 0.15f, 1f);

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

            foreach (var halo in _halos.Values)
            {
                if (halo != null)
                {
                    try { UnityEngine.Object.Destroy(halo); } catch (Exception) { /* going away anyway */ }
                }
            }

            _halos.Clear();
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

            Highlight(unit);
        }

        /// <summary>
        /// Give the creature a rim, so it is marked when looked at and not only when something is in the way.
        /// </summary>
        /// <remarks>
        /// <para><b>Why not the game's outline on its own.</b> That one is an <i>occlusion</i> cue: it draws the
        /// silhouette a wall is hiding, which is what finding furniture through a building needs. In plain sight
        /// it has nothing to draw, and in plain sight is where these enemies usually are.</para>
        /// <para><b>Why not the unit shader's own highlight either.</b> It exists, and it works — but it is a wash
        /// of colour over the whole picture rather than a rim, and it is gated behind the same switch that turns on
        /// the hit flash and the burning and frozen looks, so a creature shows nothing until something hits it.
        /// Measured, after trying it.</para>
        /// <para><b>So the rim is built.</b> A copy of the creature's own sprite, a little larger, in flat colour,
        /// drawn just before the creature itself: the middle is covered by the real picture and what is left
        /// showing is an edge. It rides the sprite's own transform, so it turns and leans with it for free, and it
        /// costs one object and one material per marked creature — which die with it.</para>
        /// </remarks>
        private void Highlight(Unit unit)
        {
            try
            {
                var sprite = FindSpriteRenderer(unit);
                if (sprite == null)
                {
                    ReportOnce("no renderer with a mesh to copy");
                    return;
                }

                var mesh = sprite.GetComponent<MeshFilter>();
                var source = sprite.sharedMaterial;
                if (mesh == null || mesh.sharedMesh == null || source == null)
                {
                    ReportOnce($"'{sprite.name}' has no mesh or material to copy");
                    return;
                }

                var shader = HaloShaderOrNull();
                if (shader == null)
                {
                    ReportOnce("this build has none of the flat-colour shaders the rim needs");
                    return;
                }

                var halo = new GameObject("FalseGodsPriorityHalo");
                halo.layer = sprite.gameObject.layer;
                halo.transform.SetParent(sprite.transform, worldPositionStays: false);
                halo.transform.localPosition = Vector3.zero;
                halo.transform.localRotation = Quaternion.identity;
                halo.transform.localScale = Vector3.one * HaloScale;

                halo.AddComponent<MeshFilter>().sharedMesh = mesh.sharedMesh;
                var renderer = halo.AddComponent<MeshRenderer>();
                renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                renderer.receiveShadows = false;
                var material = BuildHaloMaterial(shader);
                // Drawn just before the creature, so the creature paints over its middle and leaves the edge.
                material.renderQueue = source.renderQueue - 1;
                renderer.material = material;

                _halos[unit] = halo;

                // Said once, with everything that decides whether a rim can be seen at all: which renderer was
                // copied, whether its picture was found, and where the copy lands in the drawing order. Guessing
                // at any of those from outside the game is how the last two attempts were spent.
                ReportOnce($"rim on '{sprite.name}' (mesh '{mesh.sharedMesh.name}', {mesh.sharedMesh.vertexCount} verts) "
                    + $"in flat colour via '{shader.name}' (supported={shader.isSupported}), "
                    + $"queue {material.renderQueue} vs the creature's {source.renderQueue}, "
                    + $"layer '{LayerMask.LayerToName(halo.layer)}', scale {HaloScale:0.##}");
            }
            catch (Exception exception)
            {
                _logger?.LogWarning($"[minion] one could not be given a rim ({exception.Message}); it fights unmarked.");
            }
        }

        /// <summary>
        /// A flat-colour copy of the creature's own shape.
        /// </summary>
        /// <remarks>
        /// <b>No texture, deliberately.</b> These sprites are drawn on a mesh cut to the outline of the art -
        /// sixteen hundred vertices for one goblin - so the shape is already in the geometry and painting it one
        /// colour gives the silhouette directly. Sampling the creature's own picture instead would mean matching
        /// how its shader picks a region out of a sheet, which is a guess we do not need to make.
        /// </remarks>
        private static Material BuildHaloMaterial(Shader shader)
        {
            var material = new Material(shader);
            SetColour(material, PriorityColour);
            return material;
        }

        /// <summary>Colour a material whichever way its shader takes one.</summary>
        private static void SetColour(Material material, Color colour)
        {
            if (material.HasProperty(BaseColourProperty))
            {
                material.SetColor(BaseColourProperty, colour);
            }

            if (material.HasProperty(ColourProperty))
            {
                material.SetColor(ColourProperty, colour);
            }
        }

        /// <summary>The renderer carrying the creature's picture — the one with a mesh to copy.</summary>
        private static Renderer? FindSpriteRenderer(Unit unit)
        {
            foreach (var renderer in unit.GetComponentsInChildren<Renderer>(includeInactive: true))
            {
                if (renderer != null && renderer.GetComponent<MeshFilter>() != null)
                {
                    return renderer;
                }
            }

            return null;
        }

        /// <summary>Take a creature's rim away. Idempotent, and safe on one that never had it.</summary>
        private void Unhighlight(Unit? unit)
        {
            if (unit == null || _halos.Count == 0 || !_halos.TryGetValue(unit, out var halo))
            {
                return;
            }

            _halos.Remove(unit);
            if (halo == null)
            {
                return;
            }

            try { UnityEngine.Object.Destroy(halo); }
            catch (Exception exception)
            {
                _logger?.LogWarning($"[minion] a rim could not be cleared ({exception.Message}).");
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
                    Unhighlight(unit);
                    _spawned.RemoveAt(i);
                }
            }
        }
    }
}
