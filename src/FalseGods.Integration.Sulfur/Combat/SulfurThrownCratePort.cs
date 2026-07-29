// Heavy Unity / game-type interop (none of those APIs carry nullable annotations), so this file opts out of
// the nullable-reference context like the other game-facing implementations.
#nullable disable

using System;
using System.Collections.Generic;
using System.Reflection;
using FalseGods.Application.Combat;
using FalseGods.Core.Bosses.Combat;
using FalseGods.RuntimeContracts.Arena;
using PerfectRandom.Sulfur.Core;
using PerfectRandom.Sulfur.Core.Units;
using UnityEngine;
using UnityEngine.AddressableAssets;
using UnityEngine.ResourceManagement.AsyncOperations;
using UnityEngine.ResourceManagement.ResourceLocations;
using ILogger = FalseGods.RuntimeContracts.Diagnostics.ILogger;

namespace FalseGods.Integration.Sulfur.Combat
{
    /// <summary>
    /// <see cref="IThrownCratePort"/> over SULFUR's own destructible: it assembles a real <c>Breakable</c> unit at
    /// runtime, carries it along the simulation's arc, and lets it break the game's way when a player shoots it.
    /// </summary>
    /// <remarks>
    /// <para><b>Why a runtime-assembled unit and not a loaded prefab.</b> A vanilla breakable is a <i>unit</i> —
    /// health, weapon-fire on the game's own hit path, loot on break, and the session's own loot-sharing rules,
    /// all for free — but the game never spawns barrels or crates dynamically, so the prefab their definition
    /// points at has no entry in the shipped content catalog and cannot be loaded while the cave level is up. What
    /// <i>is</i> reachable is the barrel's mesh (addressable) and the definition itself
    /// (<c>UnitIds.WoodenBarrel.GetAsset()</c> — the definition lives in the loaded unit database, only its dead
    /// prefab handle is missing). So the unit is built from those: the real mesh on a body carrying the game's own
    /// <c>Breakable</c> and <c>Hitmesh</c>, spawned through the game's own <c>UnitSO.SpawnUnit</c>. Loot follows
    /// because <c>SpawnLoot</c> reads the definition's global loot, not anything prefab-specific, so a shot-down
    /// barrel drops what a vanilla one drops and a multiplayer session shares it by its own rules.</para>
    /// <para><b>A template built once, then cloned.</b> The body — mesh, collider, rigidbody, the wired
    /// <c>Breakable</c>/<c>Hitmesh</c> pair — is assembled a single time in <see cref="Prepare"/> as an inactive
    /// template. Unity re-points a component's references to their clones on <c>Instantiate</c>, so every thrown
    /// barrel's <c>Hitmesh</c> owns and hits its own body without any per-throw wiring.</para>
    /// <para><b>Two vanilla behaviours are switched off for the flight.</b> A breakable normally shatters on first
    /// contact or takes damage from its own collision speed; either would destroy a barrel the moment it grazed
    /// anything, and both are decided by the physics we are deliberately not using. With them off and the body
    /// kinematic, arrival is ours to declare.</para>
    /// <para><b>Landing breaks it without paying out.</b> The loot is gated inside the game's break by a private
    /// flag, so landing sets that flag before breaking — keeping the real break, sound and debris, and taking away
    /// only the reward. That asymmetry is the point: loot rewards shooting a barrel out of the air, and a boss
    /// with an endless supply cannot be farmed by letting them land. If that private flag ever moves, the fallback
    /// destroys the barrel quietly — the effect is lost, the rule is not.</para>
    /// </remarks>
    public sealed class SulfurThrownCratePort : IThrownCratePort
    {
        // The destructibles the boss throws. Each is an ordinary vanilla unit whose definition is loadable
        // (GetAsset) but whose own prefab is not, so it is assembled the same way — the only differences are the
        // unit definition, the body mesh, and which material and break effect dress it. The barrel wears its real
        // model (addressable); the crate is a plain cube, which suits a box and needs no model at all, dressed in
        // the crate's own material. ExplosiveBarrel is the obvious later addition.
        private static readonly DestructibleSpec[] Specs =
        {
            new DestructibleSpec
            {
                // The barrel's model is found in the live catalog by its asset path (a fixed GUID cannot be used —
                // the reverse-engineered project's keys are not the game's real ones, and the path survives a
                // re-key). Its material is folder-qualified so the search skips the wood PARTICLE material of the
                // same stem.
                Unit = UnitIds.WoodenBarrel,
                Name = "barrel",
                MeshPathFragment = "Barrel_Wood",
                MaterialPathFragment = "Barrels/BarrelWood",
                BreakEffectPathFragment = "BarrelBreakEffect",
            },
            new DestructibleSpec
            {
                // A box needs no imported model — a unit cube is the right shape — so the crate is built on a
                // primitive and dressed in its own material from the catalog.
                Unit = UnitIds.WoodenCrate,
                Name = "crate",
                CubeMesh = true,
                MaterialPathFragment = "Crate",
                BreakEffectPathFragment = "CrateBreakEffect",
            },
            new DestructibleSpec
            {
                // The one that goes off. Assembled exactly like the others and then given the game's own
                // explosion to die with, so every way it can die — shot out of the air, shot where it stands,
                // caught by another blast — ends in the same bang without any of those paths knowing about it.
                //
                // Every value here is read off a real one in the game rather than guessed, and two of them are not
                // what the names suggest: it wears the SAME wood the ordinary barrel does (what marks it out is
                // the heap of sulfur on its lid, below), and it goes off as DYNAMITE — the explosion type actually
                // called ExplosiveBarrel is something else and is not what the game's own barrels use.
                Unit = UnitIds.ExplosiveBarrel,
                Name = "explosive barrel",
                MeshPathFragment = "Barrel_Explosion",
                MaterialPathFragment = "Barrels/BarrelWood",
                BreakEffectPathFragment = "BarrelBreakEffect (Exploding)",
                Explosion = BarrelExplosion,
                TopMeshPathFragment = "PileSulfur",
                TopMaterialPathFragment = "SulfurYellow",
            },
        };

        /// <summary>
        /// What one of the game's own explosive barrels goes off as. <b>Not</b> <c>ExplosiveBarrel</c>, which is a
        /// different explosion the game's barrels do not use — read off a configured one rather than picked by
        /// name, and the boss's share of a blast is measured from this same definition.
        /// </summary>
        private const ExplosionTypes BarrelExplosion = ExplosionTypes.Dynamite;

        /// <summary>
        /// How often a produced destructible is an explosive barrel rather than ordinary cargo.
        /// </summary>
        /// <remarks>
        /// Low on purpose: the barrels are the thing that makes a barrage worth watching rather than the thing a
        /// barrage is made of. At the rates the village supplies, one in twelve is roughly a barrel every few
        /// seconds at full production — enough that a player learns to look for them, far from enough that the
        /// fight becomes about them.
        /// </remarks>
        private const float ExplosiveChance = 1f / 12f;

        /// <summary>
        /// Salt for the explosive roll. Kept clear of the volley's salts because this is rolled per <b>crate
        /// ordinal</b>, not per volley — see <see cref="PickKind"/> for why that is what makes it agree across
        /// peers without anything being sent.
        /// </summary>
        private const int ExplosiveSeed = 5150;

        /// <summary>
        /// How long a thrown barrel lies on the ground before it goes off.
        /// </summary>
        /// <remarks>
        /// <b>Ours, not the game's.</b> Vanilla has no fuse: a barrel explodes the instant its health runs out,
        /// and the delay a player remembers is the seconds they spend shooting it. A barrel arriving as part of a
        /// barrage needs something else — a moment where it has landed, is plainly about to go off, and can still
        /// be run away from. Without it a barrel is just a crate that does more damage.
        /// </remarks>
        private const float LandedFuseSeconds = 3f;

        // The layer the vanilla destructibles sit on, so the game's weapon fire finds our body the same way.
        private const string BreakableLayerName = "Breakable";

        /// <summary>Ceiling on crates lying on nobody's pile — cargo spilled by carriers who died holding it.
        /// The carriers themselves are what normally keep this down; this only stops a level from silting up when
        /// they are being killed faster than they can collect.</summary>
        private const int MaxLooseCrates = 60;

        /// <summary>How often a crate the boss actually threw pays out when a player shoots it out of the air.
        /// A barrage is a lot of crates, and every one of them paying buried the floor in pickups.</summary>
        private const float LootChance = 0.1f;

        /// <summary>Salt for the per-crate loot roll, clear of the scatter's salts (0..2*count+2) and the lead
        /// coin's, so which crates pay is independent of where they land and what they aim at.</summary>
        private const int LootSalt = 70001;

        /// <summary>How a set-down load lands: a ring around the spot, a short drop onto it, and the little arc it
        /// rides out of the carrier's hands to get there. Constants rather than wire fields — every peer runs the
        /// same build, so the same seed lays the same load out on all of them.</summary>
        private const float SetDownMinRadius = 1.2f;
        private const float SetDownMaxRadius = 4f;
        private const float SetDownHeight = 1.2f;
        private const float SetDownFlightSeconds = 0.55f;
        private const float SetDownApexHeight = 1.6f;

        // The game's solid-geometry layers a flying crate should break on — walls and props, but NOT the walkable
        // floor (Geometry), whose contact is the crate's normal landing. The arena's boundary walls are colliders
        // on GeometryNoNavMesh; the rest are included defensively and skipped if absent.
        private static readonly string[] WallLayerNames =
        {
            "GeometryNoNavMesh", "StaticDoodad", "InvisibleGeometry", "LevelGenBlock",
        };

        private readonly ILogger _logger;
        private readonly IThrownCrateImpact _impact;

        // Every crate this port owns, in whatever phase of its life. One crate has a single authority here: it is
        // resting (the game's physics owns its position), lifting off the pile, or flying an arc we drive — and it
        // moves between those phases in place rather than migrating between lists. A crate a player shoots is
        // broken and destroyed by the game, leaving a null we prune on the next tick, in any phase.
        private readonly List<ManagedCrate> _crates = new List<ManagedCrate>();

        // One assembled template per destructible kind that built successfully; every thrown or dropped unit is
        // cloned from one of these. A kind whose content could not be sourced is simply absent, so the rest still
        // work.
        private readonly List<DestructibleTemplate> _templates = new List<DestructibleTemplate>();

        // The same kinds, split by what they do when they die: the cargo the village cycles through, and the one
        // that goes off (null when it could not be assembled, which simply means no barrels).
        private readonly List<DestructibleTemplate> _ordinary = new List<DestructibleTemplate>();
        private DestructibleTemplate _explosive;

        private FieldInfo _explosionOnDeath;
        private bool _prepared;
        private int _nextKind;

        // Counts the destructibles this peer has made, so each gets the number its twin has on every other peer.
        // Never reset while a session runs: the peers' counters only agree because they count the same commands.
        private int _cratesMade;

        // True while a destruction that arrived from another peer is being carried out, so applying it does not
        // report it straight back out again.
        private bool _applyingRemoteDeath;

        // Crates this peer reported the death of. A host's answer to a client's request comes back to the client
        // that asked, naming a crate it has already destroyed — expected, and told apart here from the one thing
        // worth noticing: a destruction naming a crate this peer never had, which is the two peers' piles having
        // drifted apart.
        private readonly HashSet<int> _reportedDeaths = new HashSet<int>();
        private FieldInfo _preventDroppingLoot;
        private bool _warnedAboutLootFlag;
        private int _wallMask;
        private bool _wallMaskBuilt;
        private bool _looseNeedsCapping;
        private int _nextThrowSeed = 1;

        public SulfurThrownCratePort(ILogger logger = null, IThrownCrateImpact impact = null)
        {
            _logger = logger;
            _impact = impact;
        }

        public int InFlight => CountWhere(phase => phase != Phase.Resting);

        public int Resting => CountWhere(phase => phase == Phase.Resting);

        public int RestingOn(CratePileId pile)
        {
            var total = 0;
            for (var index = 0; index < _crates.Count; index++)
            {
                if (IsOn(_crates[index], pile))
                {
                    total++;
                }
            }

            return total;
        }

        /// <summary>
        /// Whether a crate belongs to <paramref name="pile"/> right now — resting on it, or still in the air on its
        /// way down to it.
        /// </summary>
        /// <remarks>
        /// <b>The moment it is thrown toward a pile, it is that pile's.</b> Counting only what had already settled
        /// made the two peers disagree: a delivery command reaches a client a little later than the host acts on
        /// it, so a volley fired in between found the client's crates still mid-air and lifted nothing — measured
        /// on two peers, fourteen volleys in a hundred and sixteen. Whether a crate has finished falling is a
        /// question of local timing, and local timing must not decide what the boss is holding.
        /// </remarks>
        /// <summary>
        /// Whether this crate is part of <paramref name="pile"/> — collectable by a carrier, fireable by the boss,
        /// and countable towards what is lying about.
        /// </summary>
        /// <remarks>
        /// <b>A barrel with a lit fuse is on nobody's pile.</b> It is lying on the ground and it is about to go
        /// off, so it is neither cargo to be carried away nor litter to be tidied up: without this a carrier could
        /// walk over and pocket a live barrel, or the loose-cargo ceiling could quietly delete one, and either way
        /// the bang the players were backing away from never comes.
        /// </remarks>
        private static bool IsOn(ManagedCrate crate, CratePileId pile) =>
            crate.Unit != null
            && crate.Fuse <= 0f
            && crate.Pile == pile
            && (crate.Phase == Phase.Resting || crate.Phase == Phase.Tossing);

        private int CountWhere(Func<Phase, bool> predicate)
        {
            var total = 0;
            for (var index = 0; index < _crates.Count; index++)
            {
                if (predicate(_crates[index].Phase))
                {
                    total++;
                }
            }

            return total;
        }

        public bool Prepare()
        {
            if (_prepared)
            {
                return true;
            }

            try
            {
                // Looked up once: the loot gate is private, and finding it per throw would be wasteful.
                if (_preventDroppingLoot == null)
                {
                    _preventDroppingLoot = PrivateField(typeof(Breakable), "preventDroppingLoot");
                    if (_preventDroppingLoot == null && !_warnedAboutLootFlag)
                    {
                        _warnedAboutLootFlag = true;
                        _logger?.LogWarning("[crate] could not find the loot gate on the game's breakable; a landing "
                            + "crate will be removed quietly instead of breaking. Landing still drops nothing.");
                    }
                }

                var built = new List<string>();
                foreach (var spec in Specs)
                {
                    var template = BuildKind(spec);
                    if (template != null)
                    {
                        _templates.Add(template);
                        built.Add(spec.Name);
                    }
                }

                if (_templates.Count == 0)
                {
                    _logger?.LogWarning("[crate] no destructible kind could be assembled; throwing is unavailable.");
                    return false;
                }

                // Split once: what the village hauls, and the one that goes off. A kind whose explosion could not
                // be wired counts as ordinary cargo rather than as a barrel that quietly does not explode.
                foreach (var kind in _templates)
                {
                    if (kind.Explodes)
                    {
                        _explosive = kind;
                    }
                    else
                    {
                        _ordinary.Add(kind);
                    }
                }

                _prepared = true;
                _logger?.Log($"[crate] destructible content ready: {string.Join(", ", built)}.");
                return true;
            }
            catch (Exception exception)
            {
                _logger?.LogWarning($"[crate] destructible content could not be prepared: {exception}");
                return false;
            }
        }

        /// <summary>Assemble the inactive template one destructible kind is cloned from, sourcing its mesh (a real
        /// model or a plain cube), material and break effect. Returns null — and warns — if the kind cannot be
        /// built, so the others still work.</summary>
        private DestructibleTemplate BuildKind(DestructibleSpec spec)
        {
            var definition = spec.Unit.GetAsset();
            if (definition == null)
            {
                _logger?.LogWarning($"[crate] the game has no definition for the {spec.Name}; skipped.");
                return null;
            }

            var template = new DestructibleTemplate { Definition = definition, Name = spec.Name };

            Material meshMaterial = null;
            Mesh mesh;
            if (spec.CubeMesh)
            {
                mesh = CubeMesh();
            }
            else
            {
                mesh = LoadAddressableMesh(spec.MeshPathFragment, template, out meshMaterial, out var meshError);
                if (mesh == null)
                {
                    _logger?.LogWarning($"[crate] no {spec.Name} mesh could be sourced ({meshError}); skipped.");
                    template.Release();
                    return null;
                }
            }

            // The break effect is a nicety, not a requirement: without it the unit still flies, breaks, and drops
            // loot — it just vanishes without debris. It also carries the kind's own break sound and a material,
            // neither of which an assembled unit has otherwise.
            var breakEffect = LoadBreakEffect(spec.BreakEffectPathFragment, template, out var debrisMaterial, out var breakSound, out var effectError);
            if (breakEffect == null)
            {
                _logger?.LogWarning($"[crate] no {spec.Name} break effect could be sourced ({effectError}); it will vanish without debris.");
            }

            // Prefer the kind's own body material straight from the catalog; else the material scavenged from the
            // break debris; else the model's own import material (a cube has none).
            var realMaterial = LoadBodyMaterial(spec.MaterialPathFragment, template);
            var bodyMaterial = realMaterial != null ? realMaterial
                : debrisMaterial != null ? debrisMaterial
                : meshMaterial;

            var body = BuildTemplate(definition, mesh, bodyMaterial, breakEffect, breakSound, out var templateError);
            if (body != null)
            {
                AddTheMarkingOnTop(spec, template, body);
            }

            if (body == null)
            {
                _logger?.LogWarning($"[crate] the {spec.Name} template could not be assembled ({templateError}); skipped.");
                template.Release();
                return null;
            }

            template.Template = body;
            template.Explodes = spec.Explosion != ExplosionTypes.None
                && GiveItAnExplosion(body, spec.Explosion);
            template.Look = LookOf(body);
            return template;
        }

        /// <summary>
        /// Make this kind die in an explosion, by writing the game's own "what I go off as" onto the template.
        /// </summary>
        /// <remarks>
        /// <para><b>One field, and then every way of dying is covered.</b> A unit queues this explosion from its
        /// own death, so a barrel shot out of the air, shot where it stands, or caught in another blast all end
        /// the same way without any of those paths being taught about barrels. It is written onto the
        /// <i>template</i>, so every clone is born with it.</para>
        /// <para>Private, because the game only ever sets it in the editor. Failing to find it costs the bang and
        /// nothing else — the barrel still breaks like cargo — so it is reported and carried on.</para>
        /// </remarks>
        private bool GiveItAnExplosion(GameObject template, ExplosionTypes explosion)
        {
            var breakable = template.GetComponent<Breakable>();
            if (breakable == null)
            {
                return false;
            }

            if (_explosionOnDeath == null)
            {
                _explosionOnDeath = PrivateField(typeof(Unit), "explosionOnDeath");
                if (_explosionOnDeath == null)
                {
                    _logger?.LogWarning("[crate] the game's 'explosion on death' is not where it was; explosive "
                        + "barrels will break like ordinary cargo.");
                    return false;
                }
            }

            _explosionOnDeath.SetValue(breakable, explosion);
            return true;
        }

        /// <summary>What an assembled kind looks like — its own mesh and material, or nothing when it was built
        /// on something that carries neither.</summary>
        private static CrateLook LookOf(GameObject template)
        {
            var filter = template.GetComponentInChildren<MeshFilter>(true);
            var renderer = template.GetComponentInChildren<MeshRenderer>(true);
            return filter == null || renderer == null
                ? default(CrateLook)
                : new CrateLook(filter.sharedMesh, renderer.sharedMaterial);
        }

        /// <summary>
        /// Put a kind's second piece on top of its body — the heap of sulfur that says a barrel is the one that
        /// goes off.
        /// </summary>
        /// <remarks>
        /// <para><b>Why a kind needs one at all.</b> The game's barrels share a single material over a palette
        /// texture, and which colour a barrel is comes from its own mesh's UVs — so the explosive one is already
        /// red without anything being dressed differently. What it does not get from that is the load on its lid,
        /// and that is the part a player reads at a distance, on a goblin's back, before it is thrown.</para>
        /// <para>Optional and fail-soft: a kind that names no marking gets none, and one whose marking cannot be
        /// sourced is still a working barrel that still explodes. Its collider is not extended to cover it — the
        /// pile is scenery on top of the thing being shot.</para>
        /// </remarks>
        private void AddTheMarkingOnTop(DestructibleSpec spec, DestructibleTemplate template, GameObject body)
        {
            if (string.IsNullOrEmpty(spec.TopMeshPathFragment))
            {
                return;
            }

            var top = LoadAddressableMesh(spec.TopMeshPathFragment, template, out _, out var topError);
            if (top == null)
            {
                _logger?.LogWarning($"[crate] the {spec.Name}'s marking could not be sourced ({topError}); it will "
                    + "look like ordinary cargo.");
                return;
            }

            var piece = new GameObject("Marking");
            piece.transform.SetParent(body.transform, worldPositionStays: false);
            piece.layer = body.layer;
            piece.AddComponent<MeshFilter>().sharedMesh = top;
            var renderer = piece.AddComponent<MeshRenderer>();
            var material = LoadBodyMaterial(spec.TopMaterialPathFragment, template);
            if (material != null)
            {
                renderer.sharedMaterial = material;
            }
        }

        /// <summary>The built-in unit cube's mesh, borrowed from a throwaway primitive. The shared mesh is a
        /// persistent engine resource, so it outlives the primitive we read it from.</summary>
        private static Mesh CubeMesh()
        {
            var temp = GameObject.CreatePrimitive(PrimitiveType.Cube);
            temp.SetActive(false);
            var mesh = temp.GetComponent<MeshFilter>().sharedMesh;
            UnityEngine.Object.Destroy(temp);
            return mesh;
        }

        /// <summary>
        /// What the next destructible is: ordinary cargo most of the time, and now and then a barrel that goes off.
        /// </summary>
        /// <remarks>
        /// <para><b>Nothing is sent for this, and nothing needs to be.</b> Every peer builds the same destructibles
        /// from the same broadcast commands in the same order, so the ordinal is already a shared number — the same
        /// fact the crate's own id is built on. Rolling the barrel from that ordinal makes every peer roll the same
        /// answer, which is the same reason a volley sends a seed rather than positions.</para>
        /// <para>The ordinary kinds still cycle, so a pile is an even mix of them; the barrel is drawn out of the
        /// cycle rather than taking a turn in it, or its rate would be whatever the number of kinds happened to be.
        /// </para>
        /// </remarks>
        private DestructibleTemplate PickKind()
        {
            var ordinal = _nextKind++;
            if (_explosive != null && SeededRandom.Unit01(ExplosiveSeed, ordinal) < ExplosiveChance)
            {
                return _explosive;
            }

            if (_ordinary.Count == 0)
            {
                return _templates[ordinal % _templates.Count];
            }

            return _ordinary[ordinal % _ordinary.Count];
        }

        /// <summary>
        /// The mesh and material of one destructible kind, for something that needs to <i>look</i> like a crate
        /// without being one — a carried load riding on a goblin's back.
        /// </summary>
        /// <remarks>
        /// Shared rather than resolved a second time: this port already located and holds the game's own crate
        /// mesh and material, and loading them again elsewhere would mean a second set of handles to release and a
        /// second chance to render magenta. Returns false until <see cref="Prepare"/> has succeeded.
        /// </remarks>
        internal bool TryGetLook(out Mesh mesh, out Material material)
        {
            mesh = null;
            material = null;
            if (!_prepared || _templates.Count == 0)
            {
                return false;
            }

            for (var i = 0; i < _templates.Count; i++)
            {
                var template = _templates[i].Template;
                if (template == null)
                {
                    continue;
                }

                var filter = template.GetComponentInChildren<MeshFilter>(true);
                var renderer = template.GetComponentInChildren<MeshRenderer>(true);
                if (filter == null || renderer == null)
                {
                    continue;
                }

                mesh = filter.sharedMesh;
                material = renderer.sharedMaterial;
                if (mesh != null && material != null)
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>Clone one destructible from a kind's template through the game's own spawn — a real unit, with
        /// weapon fire and loot — wake it, and switch off the vanilla break-on-contact rules so we own its life.
        /// The caller sets its rigidbody for flight or for rest.</summary>
        private Unit SpawnFrom(DestructibleTemplate kind, Vector3 position, out Breakable breakable)
        {
            breakable = null;

            var unit = UnitSO.SpawnUnit(kind.Definition, kind.Template, position, Quaternion.identity);
            if (unit == null)
            {
                return null;
            }

            // The clone is inactive because the template is; waking it runs the unit's own Start after Spawn has
            // already marked it spawned, so nothing re-initialises.
            unit.gameObject.SetActive(true);

            // Ours, not the session layer's. It matches destructible breaks by spawn position, and every one of
            // these is made on the same heap — so its match picks the wrong crate and breaks something that was
            // mid-throw. We already replicate these from the command that made them.
            OurDestructibles.Claim(unit.gameObject);

            breakable = unit as Breakable;
            if (breakable != null)
            {
                // Ours to decide when it breaks, not the physics engine's — neither shatter-on-contact nor
                // collision-speed damage may fire while we carry or pile it. It stays shootable regardless.
                breakable.BreakOnFirstContact = false;
                breakable.TakeDamageOnCollision = false;
            }

            return unit;
        }

        public bool Throw(ArenaWorldPoint from, ArenaWorldPoint to, float flightSeconds, float apexHeight)
        {
            if (flightSeconds <= 0f)
            {
                _logger?.LogWarning("[crate] a throw needs a positive flight time.");
                return false;
            }

            if (!Prepare())
            {
                return false;
            }

            try
            {
                var start = new Vector3(from.X, from.Y, from.Z);
                var target = new Vector3(to.X, to.Y, to.Z);

                var kind = PickKind();
                var unit = SpawnFrom(kind, start, out var breakable);
                if (unit == null)
                {
                    _logger?.LogWarning("[crate] the game returned no unit for the destructible.");
                    return false;
                }

                if (unit.Rigidbody != null)
                {
                    unit.Rigidbody.useGravity = false;
                    unit.Rigidbody.isKinematic = true;
                }

                // Thrown at somebody, so it can pay — on the same odds as a crate out of a volley.
                SetLootAllowed(breakable, SeededRandom.Unit01(_nextThrowSeed++, LootSalt) < LootChance);

                var crate = new ManagedCrate(unit, breakable, NextCrateId(), kind.Explodes, kind.Look);
                crate.BeginFlight(start, target, flightSeconds, apexHeight);
                _crates.Add(crate);
                return true;
            }
            catch (Exception exception)
            {
                _logger?.LogWarning($"[crate] the crate could not be thrown: {exception}");
                return false;
            }
        }

        public bool Drop(ArenaWorldPoint at, CratePileId pile)
        {
            if (!Prepare())
            {
                return false;
            }

            try
            {
                var where = new Vector3(at.X, at.Y, at.Z);

                // Same real spawn as a throw — a live destructible, weapon-fire and loot and all — but from here on
                // the game's physics owns it, not our arc.
                var kind = PickKind();
                var unit = SpawnFrom(kind, where, out var breakable);
                if (unit == null)
                {
                    _logger?.LogWarning("[crate] the game returned no unit for the resting destructible.");
                    return false;
                }

                if (unit.Rigidbody != null)
                {
                    // The template spawns kinematic for flight; a resting crate is the opposite — real gravity,
                    // driven by nothing but the physics engine, so it falls, rests, and piles like a vanilla barrel.
                    unit.Rigidbody.isKinematic = false;
                    unit.Rigidbody.useGravity = true;
                }

                // A new crate is resting by default — the game's physics owns it until it is lifted — and belongs
                // to the pile it was dropped onto, which is what decides whether the boss may ever fire it.
                // Standing on a pile it is worth nothing to shoot; only what the boss sends can pay.
                SetLootAllowed(breakable, false);
                _crates.Add(new ManagedCrate(unit, breakable, NextCrateId(), kind.Explodes, kind.Look) { Pile = pile });
                if (pile.Kind == CratePileKind.Loose)
                {
                    CapLooseCrates();
                }

                return true;
            }
            catch (Exception exception)
            {
                _logger?.LogWarning($"[crate] the crate could not be dropped: {exception}");
                return false;
            }
        }

        public int TossRing(ArenaWorldPoint from, ArenaWorldPoint at, CratePileId pile, int count, int seed)
        {
            if (count <= 0 || !Prepare())
            {
                return 0;
            }

            var placed = 0;
            for (var i = 0; i < count; i++)
            {
                var ring = ShotgunSpread.Offset(seed, i, count, SetDownMinRadius, SetDownMaxRadius);
                var to = new ArenaWorldPoint(at.X + ring.X, at.Y + SetDownHeight, at.Z + ring.Z);
                if (Toss(from, to, pile, SetDownFlightSeconds, SetDownApexHeight))
                {
                    placed++;
                }
            }

            return placed;
        }

        private bool Toss(ArenaWorldPoint from, ArenaWorldPoint to, CratePileId pile, float flightSeconds, float apexHeight)
        {
            if (!Prepare())
            {
                return false;
            }

            try
            {
                var start = new Vector3(from.X, from.Y, from.Z);
                var kind = PickKind();
                var unit = SpawnFrom(kind, start, out var breakable);
                if (unit == null)
                {
                    _logger?.LogWarning("[crate] the game returned no unit for the tossed destructible.");
                    return false;
                }

                // Our arc drives it, so physics must not fight us on the way down; Settle hands it back.
                if (unit.Rigidbody != null)
                {
                    unit.Rigidbody.isKinematic = true;
                    unit.Rigidbody.useGravity = false;
                }

                // On its way to a pile, so worth nothing to shoot — the same rule as one already standing there.
                SetLootAllowed(breakable, false);

                var crate = new ManagedCrate(unit, breakable, NextCrateId(), kind.Explodes, kind.Look);
                crate.BeginToss(start, new Vector3(to.X, to.Y, to.Z), flightSeconds, apexHeight, pile);
                _crates.Add(crate);
                return true;
            }
            catch (Exception exception)
            {
                _logger?.LogWarning($"[crate] the crate could not be tossed: {exception}");
                return false;
            }
        }

        // Salt for the per-crate "lead this one or not" coin, kept clear of the scatter's salts (0..2*count+2) so
        // the choice is independent of where a crate lands within its slice.
        private const int LeadChoiceSalt = 40009;

        /// <summary>Salt for the per-crate "which player is this one for" draw, kept clear of the scatter's salts
        /// and the lead coin's so who a crate is aimed at is independent of where it lands and whether it leads.</summary>
        private const int TargetChoiceSalt = 50021;

        public int LaunchVolley(CratePileId pile, IReadOnlyList<CrateVolleyAim> aims, CrateVolleyShape shape)
        {
            if (aims == null || aims.Count == 0 || !Prepare())
            {
                return 0; // nobody to throw at
            }

            // Only crates already resting ON THIS PILE are lifted: a crate in the air stays there, and one still
            // standing at a production point is not the boss's to fire — somebody has to bring it first.
            var chosen = new List<ManagedCrate>();
            for (var index = 0; index < _crates.Count && chosen.Count < shape.Count; index++)
            {
                if (IsOn(_crates[index], pile))
                {
                    chosen.Add(_crates[index]);
                }
            }

            if (chosen.Count == 0)
            {
                return 0;
            }

            for (var index = 0; index < chosen.Count; index++)
            {
                var crate = chosen[index];

                // Which player this crate is for. Per crate rather than per volley, so a barrage threatens the
                // whole room at once instead of burying one player while the others walk through it.
                var who = aims.Count == 1
                    ? 0
                    : (int)(SeededRandom.Unit01(shape.Seed, TargetChoiceSalt + index) * aims.Count) % aims.Count;
                var aim = aims[who];

                // Each crate independently aims at where that player is now or where they are predicted to be, a
                // seeded coin so both spots are threatened in every volley — no single way of moving dodges it all.
                var leads = SeededRandom.Unit01(shape.Seed, LeadChoiceSalt + index) < shape.LeadShare;
                var center = leads ? aim.Lead : aim.Current;

                // The scatter is seeded so every peer throwing this volley lands the crates the same way; the count
                // handed to the pattern is what actually flew, so a short pile still rings the target evenly.
                var offset = ShotgunSpread.Offset(
                    shape.Seed, index, chosen.Count, shape.SpreadMinRadius, shape.SpreadMaxRadius);
                var target = new Vector3(center.X + offset.X, center.Y, center.Z + offset.Z);

                var from = crate.Unit.transform.position;
                crate.BeginLift(from, from + Vector3.up * shape.LiftHeight, target, shape, index);

                // Whether this one pays if a player shoots it down is decided here, seeded from the volley so
                // every peer agrees — but it is not granted until the crate is actually released. A crate still
                // hovering is part of the telegraph, and nothing that happens to it there should pay.
                crate.LootWhenFired = SeededRandom.Unit01(shape.Seed, LootSalt + index) < LootChance;

                // We drive it from here on, so it leaves the physics engine's hands — and, like a thrown crate, it
                // must not shatter on contact or on its own speed while we carry it.
                if (crate.Breakable != null)
                {
                    crate.Breakable.BreakOnFirstContact = false;
                    crate.Breakable.TakeDamageOnCollision = false;
                }

                if (crate.Unit.Rigidbody != null)
                {
                    crate.Unit.Rigidbody.isKinematic = true;
                    crate.Unit.Rigidbody.useGravity = false;
                }
            }

            return chosen.Count;
        }

        /// <summary>The number the next destructible made here will carry.</summary>
        private int NextCrateId() => ++_cratesMade;

        /// <summary>
        /// A destructible died in a way the other peers cannot work out for themselves — a player broke it, or it
        /// burst on one. Everything else (a landing, a wall, a lift into a volley) follows the same arc from the
        /// same seed everywhere and is already agreed on.
        /// </summary>
        /// <remarks>Set by the composition root, which knows whether this peer settles the shared world: a host
        /// broadcasts what it saw, a client asks for it. Never raised while a destruction that arrived from
        /// somewhere else is being carried out.</remarks>
        public Action<int, CrateDeath> Died { get; set; }

        /// <summary>
        /// A barrel went off, and where. For the one thing the game's own blast cannot reach: our boss is not a
        /// creature the game runs, so no overlap check will ever find it and its share of an explosion has to be
        /// worked out by whoever owns it.
        /// </summary>
        /// <remarks>Raised on every peer, because every peer sets its own barrels off from the same commands and
        /// the same seed. Nothing here decides anything — what it costs the boss is settled where the boss is.
        /// </remarks>
        public Action<ArenaWorldPoint> Exploded { get; set; }

        /// <summary>
        /// What one of these barrels is worth, and how far it reaches — read from the game's own explosion
        /// definition rather than guessed, so the share our boss takes is the share everything else takes.
        /// </summary>
        /// <remarks>Zero radius when the definition cannot be read, which reads as "the blast reaches nobody we
        /// have to hand-damage" — the game's own victims are unaffected either way.</remarks>
        public void ReadBlast(out float damage, out float radius)
        {
            damage = 0f;
            radius = 0f;
            try
            {
                var definition = BarrelExplosion.GetAsset();
                if (definition == null)
                {
                    return;
                }

                damage = definition.data.damage;
                radius = definition.data.damageRadius;
            }
            catch (Exception exception)
            {
                _logger?.LogWarning($"[crate] the game's barrel blast could not be measured ({exception.Message}); "
                    + "the boss will not take a share of one.");
            }
        }

        /// <summary>
        /// Carry out a destruction decided elsewhere: destroy the crate numbered <paramref name="crateId"/> the
        /// way <paramref name="death"/> says. A number this peer does not have is nothing to do — piles settle
        /// under physics rather than under the commands alone, so two peers can disagree about which crate a
        /// carrier picked up, and destroying nothing is the right answer to that disagreement. Returns whether
        /// anything was destroyed, which is the measurement of how often they disagree.
        /// </summary>
        public bool Destroy(int crateId, CrateDeath death)
        {
            for (var index = _crates.Count - 1; index >= 0; index--)
            {
                var crate = _crates[index];
                if (crate.Id != crateId)
                {
                    continue;
                }

                _crates.RemoveAt(index);
                if (crate.Unit == null)
                {
                    return false; // already gone here; the two peers agree, just not about when
                }

                _applyingRemoteDeath = true;
                try
                {
                    if (death == CrateDeath.Shot)
                    {
                        BreakAsShot(crate);
                    }
                    else
                    {
                        BreakNoLoot(crate);
                    }
                }
                finally
                {
                    _applyingRemoteDeath = false;
                }

                return true;
            }

            if (!_reportedDeaths.Remove(crateId))
            {
                // Not ours, and never here: the peers disagree about which crates they hold. Harmless in itself —
                // there is nothing to destroy — but it is the tell that the piles have drifted, so it is said
                // plainly rather than swallowed.
                _logger?.LogWarning($"[crate] a destruction named crate {crateId}, which this peer never had.");
            }

            return false;
        }

        /// <summary>Say that a crate died in a way the peers have to be told about, unless we are carrying out
        /// somebody else's word for it.</summary>
        private void Report(ManagedCrate crate, CrateDeath death)
        {
            if (_applyingRemoteDeath)
            {
                return;
            }

            _reportedDeaths.Add(crate.Id);
            try { Died?.Invoke(crate.Id, death); }
            catch (Exception exception)
            {
                _logger?.LogWarning($"[crate] a destruction could not be reported ({exception.Message}); skipped.");
            }
        }

        public void Advance(float deltaSeconds)
        {
            BurnFuses(deltaSeconds);

            for (var index = _crates.Count - 1; index >= 0; index--)
            {
                var crate = _crates[index];

                // The game broke it without us asking: a player shot it, in whatever phase it was in. Ordinary,
                // and already correct on its own — a crate that has not been thrown was never granted a payout,
                // so shooting one off the pile costs the boss its ammunition and pays nothing.
                // This is also the only place a player-caused break can be noticed: the shot goes through the
                // game's own damage path, which destroys the object without telling us. Nobody else can work it
                // out, so it is said out loud.
                if (crate.Unit == null)
                {
                    _crates.RemoveAt(index);
                    Report(crate, CrateDeath.Shot);
                    continue;
                }

                switch (crate.Phase)
                {
                    case Phase.Resting:
                        // The game's physics owns a resting crate's position; we only hold it for teardown and lift.
                        break;

                    case Phase.Lifting:
                        AdvanceLift(crate, deltaSeconds);
                        break;

                    case Phase.Tossing:
                    {
                        crate.Elapsed += deltaSeconds;
                        var progress = crate.Elapsed / crate.FlightSeconds;
                        if (progress >= 1f)
                        {
                            Settle(crate);
                            break;
                        }

                        crate.Unit.transform.position = ArcPoint(crate, progress);
                        break;
                    }

                    case Phase.Flying:
                    {
                        crate.Elapsed += deltaSeconds;
                        var progress = crate.Elapsed / crate.FlightSeconds;

                        if (progress >= 1f)
                        {
                            // Reached its landing spot: splash there, then break it, no loot — unless it is a
                            // barrel, which lies there with a lit fuse and therefore has to stay in our hands, or
                            // nothing is left to burn it down.
                            if (Land(crate))
                            {
                                _crates.RemoveAt(index);
                            }

                            break;
                        }

                        var from = crate.Unit.transform.position;
                        var to = ArcPoint(crate, progress);

                        // A wall (or any solid geometry) between where it was and where the arc takes it next:
                        // detonate against it instead of passing through. Kinematic flight ignores physics, so the
                        // crate would otherwise sail through the arena's walls.
                        if (HitsGeometry(from, to, out var wallPoint))
                        {
                            crate.Unit.transform.position = wallPoint;
                            _crates.RemoveAt(index);
                            _impact?.Splash(ToPoint(wallPoint));
                            BreakNoLoot(crate);
                            break;
                        }

                        crate.Unit.transform.position = to;

                        // Reached a player's body in the air: detonate on them. Where a player is standing is the
                        // other thing no peer can derive, so this one is said out loud too.
                        if (_impact != null && _impact.Contact(ToPoint(to)))
                        {
                            _crates.RemoveAt(index);
                            BreakNoLoot(crate);
                            Report(crate, CrateDeath.Struck);
                        }

                        break;
                    }
                }
            }

            // Safe here, and only here: the walk above is finished, so removing entries cannot pull the ground out
            // from under it.
            if (_looseNeedsCapping)
            {
                _looseNeedsCapping = false;
                CapLooseCrates();
            }
        }

        /// <summary>
        /// A tossed crate has arrived: hand it back to the game's physics, resting on the pile it was thrown onto.
        /// The opposite end of a flight — nothing splashes, nothing breaks, the crate simply comes down and stays.
        /// </summary>
        private void Settle(ManagedCrate crate)
        {
            crate.Settle();

            if (crate.Unit != null && crate.Unit.Rigidbody != null)
            {
                // Real gravity again, so it drops the last inch onto whatever is already there and stacks.
                crate.Unit.Rigidbody.isKinematic = false;
                crate.Unit.Rigidbody.useGravity = true;
            }

            // NOT capped here: this runs inside Advance's walk over the crate list, and capping removes entries
            // from it. The request is deferred to the end of that walk instead.
            if (crate.Pile.Kind == CratePileKind.Loose)
            {
                _looseNeedsCapping = true;
            }
        }

        /// <summary>Raise a crate off the pile to its hover point, hold it there a beat, then hand it to the arc.
        /// Returns nothing — a lift never resolves the crate; it becomes a flight, which the next tick advances.</summary>
        private void AdvanceLift(ManagedCrate crate, float deltaSeconds)
        {
            crate.Elapsed += deltaSeconds;

            if (crate.Elapsed < crate.LiftSeconds)
            {
                var rising = Vector3.Lerp(crate.LiftFrom, crate.Hover, crate.Elapsed / crate.LiftSeconds);
                crate.Unit.transform.position = rising;
                return;
            }

            if (crate.Elapsed < crate.LiftSeconds + crate.HoldSeconds)
            {
                // The telegraph: crates hang at the top so the player can read the volley before it fires.
                crate.Unit.transform.position = crate.Hover;
                return;
            }

            // Fire: the arc starts from where the crate now hovers and ends at the scattered target chosen at lift.
            // Only now is it the boss's throw, and only now can shooting it down pay.
            SetLootAllowed(crate.Breakable, crate.LootWhenFired);
            crate.BeginFlight(crate.Hover, crate.Target, crate.FlightSeconds, crate.ApexHeight);
        }

        /// <summary>The point on a crate's arc at <paramref name="progress"/> (0 at the throw, 1 at the target).</summary>
        private static Vector3 ArcPoint(ManagedCrate crate, float progress)
        {
            var ground = Vector3.Lerp(crate.Start, crate.Target, BallisticArc.HorizontalFraction(progress));
            ground.y += BallisticArc.Height(progress, crate.ApexHeight);
            return ground;
        }

        /// <summary>Whether the segment from <paramref name="from"/> to <paramref name="to"/> crosses solid arena
        /// geometry, and where. A thin ray suffices for the arena's thick walls; triggers are ignored so only real
        /// collision surfaces stop a crate.</summary>
        private bool HitsGeometry(Vector3 from, Vector3 to, out Vector3 point)
        {
            point = to;

            var mask = WallMask();
            if (mask == 0)
            {
                return false;
            }

            var delta = to - from;
            var distance = delta.magnitude;
            if (distance < 1e-4f)
            {
                return false;
            }

            if (Physics.Raycast(from, delta / distance, out var hit, distance, mask, QueryTriggerInteraction.Ignore))
            {
                point = hit.point;
                return true;
            }

            return false;
        }

        /// <summary>The physics layer mask of the arena's solid walls and props — built once from the game's own
        /// geometry layer names, and deliberately excluding the walkable floor, whose contact is a normal landing.
        /// A crate on the "Breakable" layer is not in the mask, so crates neither block nor break on each other.</summary>
        private int WallMask()
        {
            if (_wallMaskBuilt)
            {
                return _wallMask;
            }

            var mask = 0;
            foreach (var name in WallLayerNames)
            {
                var layer = LayerMask.NameToLayer(name);
                if (layer >= 0)
                {
                    mask |= 1 << layer;
                }
            }

            if (mask == 0)
            {
                _logger?.LogWarning("[crate] none of the arena's wall layers were found; flying crates will pass "
                    + "through walls instead of breaking on them.");
            }

            _wallMask = mask;
            _wallMaskBuilt = true;
            return _wallMask;
        }

        public void Release()
        {
            for (var index = 0; index < _crates.Count; index++)
            {
                var crate = _crates[index];
                if (crate.Unit != null)
                {
                    // Tearing down is not a landing and certainly not a kill: no loot, no noise, in any phase.
                    UnityEngine.Object.Destroy(crate.Unit.gameObject);
                }
            }

            _crates.Clear();

            // Every unit cloned from a template shared its mesh, material, and break effect; only now that the
            // templates and their clones are gone is it safe to destroy each template and release its handles.
            foreach (var template in _templates)
            {
                template.Release();
            }

            _templates.Clear();
            _prepared = false;
        }

        /// <summary>
        /// Find a model in the game's live content catalog by its own asset path and hand back its mesh (and a
        /// material to render it with). The handle is held on the template so the mesh stays alive for every unit
        /// built from it, and is released when the template is. Searching the catalog rather than naming a fixed
        /// GUID is what makes this independent of the reverse-engineered project's invented keys.
        /// </summary>
        private Mesh LoadAddressableMesh(string meshPathFragment, DestructibleTemplate template, out Material material, out string error)
        {
            material = null;
            error = null;

            var location = FindLocation(meshPathFragment, null, out var searchDiagnostic);
            if (location == null)
            {
                error = $"no catalog entry whose path contains '{meshPathFragment}'. {searchDiagnostic}";
                return null;
            }

            try
            {
                var handle = Addressables.LoadAssetAsync<GameObject>(location);
                var model = handle.WaitForCompletion();
                if (handle.Status != AsyncOperationStatus.Succeeded || model == null)
                {
                    error = $"model at '{location.InternalId}' did not load (status={handle.Status})";
                    try { Addressables.Release(handle); } catch (Exception) { }
                    return null;
                }

                var filter = model.GetComponentInChildren<MeshFilter>();
                if (filter == null || filter.sharedMesh == null)
                {
                    error = $"model at '{location.InternalId}' has no mesh";
                    try { Addressables.Release(handle); } catch (Exception) { }
                    return null;
                }

                var renderer = model.GetComponentInChildren<MeshRenderer>();
                material = renderer != null ? renderer.sharedMaterial : null;

                template.Handles.Add(handle);
                _logger?.Log($"[crate] {template.Name} mesh found at '{location.InternalId}'.");
                return filter.sharedMesh;
            }
            catch (Exception exception)
            {
                error = exception.Message;
                return null;
            }
        }

        /// <summary>
        /// Assemble the inactive template every thrown barrel is cloned from: the real mesh on a body that carries
        /// the game's own <see cref="Breakable"/> and <see cref="Hitmesh"/>, wired to each other, with the unit
        /// definition set so the game's spawn can build the barrel's stats and loot from it.
        /// </summary>
        private GameObject BuildTemplate(UnitSO definition, Mesh mesh, Material material, GameObject breakEffect, object breakSound, out string error)
        {
            error = null;
            try
            {
                var template = new GameObject("FalseGodsThrownCrate");
                template.SetActive(false);
                template.layer = ResolveBreakableLayer();

                var filter = template.AddComponent<MeshFilter>();
                filter.sharedMesh = mesh;

                var renderer = template.AddComponent<MeshRenderer>();
                if (material != null)
                {
                    renderer.sharedMaterial = material;
                }

                // The collider is both the physical body and the target weapon fire is tested against.
                var collider = template.AddComponent<BoxCollider>();
                collider.center = mesh.bounds.center;
                collider.size = mesh.bounds.size;

                var body = template.AddComponent<Rigidbody>();
                body.useGravity = false;
                body.isKinematic = true;

                // Breakable is a Unit; the definition goes on the field SetStats actually reads (it reads the
                // component's own unitSO, not the argument it is passed — a quirk of the game's SetStats).
                var breakable = template.AddComponent<Breakable>();
                breakable.unitSO = definition;

                // The debris burst the game plays on death. spawnOnDeath is public; the LQ list is set empty (not
                // null) so the game's death path, which reads its Count, is safe on an assembled unit.
                breakable.spawnOnDeath = breakEffect != null
                    ? new List<GameObject> { breakEffect }
                    : new List<GameObject>();
                breakable.spawnOnDeath_LQ = new List<GameObject>();

                // The break sound is a private field on Breakable; set it (from the sound the effect carries) so
                // the game's own PlayBreakSound has something to play. A missing one is simply silence.
                if (breakSound != null)
                {
                    var soundField = PrivateField(typeof(Breakable), "soundEventBreak");
                    soundField?.SetValue(breakable, breakSound);
                }

                // The hit path: the game routes a weapon hit on the collider to this Hitmesh, which carries it to
                // its owner unit. Both fields are public; Unity re-points them to each clone's own components on
                // Instantiate, so the template is wired once and every barrel is self-consistent.
                var hitmesh = template.AddComponent<Hitmesh>();
                hitmesh.owner = breakable;
                hitmesh.hitmeshCollider = collider;
                hitmesh.hitShapes = Array.Empty<Hitmesh.Data>();

                return template;
            }
            catch (Exception exception)
            {
                error = exception.Message;
                return null;
            }
        }

        /// <summary>
        /// Load the barrel break effect from the live catalog and, from its debris renderers, a real wood material
        /// to dress the body with. The handle is held so the effect and its material stay alive for every barrel;
        /// released in <see cref="Release"/>. A miss is not fatal — the barrel simply breaks without debris.
        /// </summary>
        private GameObject LoadBreakEffect(string breakEffectPathFragment, DestructibleTemplate template, out Material debrisMaterial, out object breakSound, out string error)
        {
            debrisMaterial = null;
            breakSound = null;
            error = null;

            // Prefer the plain full-quality effect (bursts outward AND carries the break sound): an exact
            // "...Effect.prefab" match skips both the "_LQ" variant (no sound) and the "(Exploding)" variant
            // (debris drops instead of bursting). Fall back to the LQ burst, then to anything.
            var location = FindLocation(breakEffectPathFragment + ".prefab", null, out var searchDiagnostic)
                ?? FindLocation(breakEffectPathFragment + "_LQ", null, out searchDiagnostic)
                ?? FindLocation(breakEffectPathFragment, null, out searchDiagnostic);
            if (location == null)
            {
                error = $"no catalog entry whose path contains '{breakEffectPathFragment}'. {searchDiagnostic}";
                return null;
            }

            try
            {
                var handle = Addressables.LoadAssetAsync<GameObject>(location);
                var effect = handle.WaitForCompletion();
                if (handle.Status != AsyncOperationStatus.Succeeded || effect == null)
                {
                    error = $"break effect at '{location.InternalId}' did not load (status={handle.Status})";
                    try { Addressables.Release(handle); } catch (Exception) { }
                    return null;
                }

                // The LQ effect is particle-based, so a MeshRenderer alone finds nothing; take any renderer's
                // material (a MeshRenderer's opaque debris material for preference, else whatever is there).
                debrisMaterial = ScavengeBodyMaterial(effect, out var materialName);
                breakSound = ScavengeBreakSound(effect);

                template.Handles.Add(handle);
                _logger?.Log($"[crate] {template.Name} break effect found at '{location.InternalId}' "
                    + $"(body material: {(debrisMaterial != null ? $"'{materialName}'" : "none")}, "
                    + $"break sound: {(breakSound != null ? "yes" : "none")}).");
                return effect;
            }
            catch (Exception exception)
            {
                error = exception.Message;
                return null;
            }
        }

        /// <summary>
        /// Try to load the barrel's real body material straight from the catalog. Returns null (and holds
        /// nothing) when the material is not addressable — the whole reason the scavenged stand-in exists. When it
        /// works, the barrel is dressed exactly, wood bands and metal both.
        /// </summary>
        private Material LoadBodyMaterial(string bodyMaterialPathFragment, DestructibleTemplate template)
        {
            var location = FindLocationOfType(bodyMaterialPathFragment, null, typeof(Material), out _);
            if (location == null)
            {
                _logger?.Log($"[crate] no addressable {template.Name} body material '{bodyMaterialPathFragment}'; using a stand-in.");
                return null;
            }

            try
            {
                var handle = Addressables.LoadAssetAsync<Material>(location);
                var material = handle.WaitForCompletion();
                if (handle.Status != AsyncOperationStatus.Succeeded || material == null)
                {
                    try { Addressables.Release(handle); } catch (Exception) { }
                    return null;
                }

                template.Handles.Add(handle);
                _logger?.Log($"[crate] {template.Name} body material found at '{location.InternalId}'.");
                return material;
            }
            catch (Exception)
            {
                return null;
            }
        }

        /// <summary>A material to dress the barrel body with, scavenged from the break effect's renderers — a
        /// mesh renderer's opaque debris material for preference, else the first renderer's material.</summary>
        private static Material ScavengeBodyMaterial(GameObject effect, out string name)
        {
            name = null;

            // Prefer a mesh renderer (opaque debris chunk) over a particle renderer (often additive/transparent).
            foreach (var meshRenderer in effect.GetComponentsInChildren<MeshRenderer>(includeInactive: true))
            {
                if (meshRenderer.sharedMaterial != null)
                {
                    name = meshRenderer.sharedMaterial.name;
                    return meshRenderer.sharedMaterial;
                }
            }

            foreach (var renderer in effect.GetComponentsInChildren<Renderer>(includeInactive: true))
            {
                if (renderer.sharedMaterial != null)
                {
                    name = renderer.sharedMaterial.name;
                    return renderer.sharedMaterial;
                }
            }

            return null;
        }

        /// <summary>The barrel's own break sound, scavenged from any <c>soundEventBreak</c> field on the break
        /// effect's components. Kept as an <see cref="object"/> so this adapter needs no direct reference to the
        /// game's sound type; the reflected assignment onto the breakable is type-checked at runtime.</summary>
        private static object ScavengeBreakSound(GameObject effect)
        {
            foreach (var component in effect.GetComponentsInChildren<Component>(includeInactive: true))
            {
                if (component == null)
                {
                    continue;
                }

                var field = PrivateField(component.GetType(), "soundEventBreak");
                var value = field?.GetValue(component);
                if (value != null)
                {
                    return value;
                }
            }

            return null;
        }

        /// <summary>The barrel arrived at its target: settle it there, splash the players around the landing point,
        /// then break it the game's way with the loot switched off.</summary>
        /// <summary>Arrive. Returns whether the crate is finished with — false for one that is now lying on the
        /// ground burning down, which this port still owns.</summary>
        private bool Land(ManagedCrate crate)
        {
            crate.Unit.transform.position = crate.Target;
            _impact?.Splash(ToPoint(crate.Target));

            // A barrel that arrives does not break — it lies where it fell with a lit fuse, which is the one
            // moment in a barrage a player can do something about. Everything else about it stays the game's: it
            // is still shootable while it sits there, and shooting it simply brings the bang forward.
            if (crate.Explodes)
            {
                crate.Fuse = LandedFuseSeconds;
                crate.RestWhereItLanded();
                _logger?.Log($"[crate] a barrel landed at ({crate.Target.x:0.0}, {crate.Target.y:0.0}, "
                    + $"{crate.Target.z:0.0}); {LandedFuseSeconds:0.#}s to get clear.");
                return false;
            }

            BreakNoLoot(crate);
            return true;
        }

        /// <summary>
        /// Burn down the fuse of every barrel lying on the ground, and set off the ones that reach zero.
        /// </summary>
        /// <remarks>
        /// Driven by the caller's frame like everything else here, so a paused game holds the fuse rather than
        /// letting it run in the background. Detonating is just killing it: the explosion is the unit's own, from
        /// the field its template was born with.
        /// </remarks>
        private void BurnFuses(float deltaSeconds)
        {
            for (var index = _crates.Count - 1; index >= 0; index--)
            {
                var crate = _crates[index];
                if (crate.Fuse <= 0f || crate.Unit == null)
                {
                    continue;
                }

                crate.Fuse -= deltaSeconds;
                if (crate.Fuse > 0f)
                {
                    continue;
                }

                crate.Fuse = 0f;
                _crates.RemoveAt(index);
                Detonate(crate);
            }
        }

        /// <summary>
        /// Set a barrel off where it lies, and tell whoever is listening where the blast was.
        /// </summary>
        /// <remarks>
        /// <b>The blast itself is the game's</b> — killing the unit queues the game's own explosion, which finds
        /// its victims the way every other explosion in the game does. What is announced is only for the things
        /// the game's search cannot see: our boss is not a creature the game runs, so nothing in an overlap check
        /// will ever find it, and its share has to be worked out by whoever owns it.
        /// </remarks>
        private void Detonate(ManagedCrate crate)
        {
            var at = crate.Unit != null ? crate.Unit.transform.position : crate.Target;
            BreakNoLoot(crate);
            Exploded?.Invoke(ToPoint(at));
        }

        /// <summary>
        /// Break a crate the way a player's shot would have: the game's own break, with the loot rule left exactly
        /// as the commands that made this crate left it.
        /// </summary>
        /// <remarks>
        /// The rule is not decided here, and deliberately not carried on the wire either. Whether a particular
        /// crate pays was settled when it was thrown, from the volley's seed — so every peer already set the same
        /// flag on its own copy, and the peer that watched a player shoot it broke it under that same flag. Sending
        /// the answer as well would be sending something both ends already agree on, with the added risk of
        /// disagreeing.
        /// <para>What the break then <i>does</i> is the session's business, not ours: with loot shared, a client's
        /// roll is suppressed and the host's mirrors down; without, each peer rolls its own, exactly as it would
        /// for any barrel in the game.</para>
        /// </remarks>
        private void BreakAsShot(ManagedCrate crate)
        {
            try
            {
                if (crate.Breakable != null)
                {
                    crate.Breakable.Break();
                    return;
                }

                UnityEngine.Object.Destroy(crate.Unit.gameObject);
            }
            catch (Exception exception)
            {
                _logger?.LogWarning($"[crate] a crate could not be broken cleanly: {exception}");
                if (crate.Unit != null)
                {
                    UnityEngine.Object.Destroy(crate.Unit.gameObject);
                }
            }
        }

        /// <summary>Break a crate where it is — its real break, sound and debris — but without paying out loot.
        /// This is what both a quiet landing and a hit on a player do: only shooting a crate out of the air pays.</summary>
        private void BreakNoLoot(ManagedCrate crate)
        {
            try
            {
                if (crate.Breakable != null && _preventDroppingLoot != null)
                {
                    _preventDroppingLoot.SetValue(crate.Breakable, true);
                    crate.Breakable.Break();
                    return;
                }

                UnityEngine.Object.Destroy(crate.Unit.gameObject);
            }
            catch (Exception exception)
            {
                _logger?.LogWarning($"[crate] a crate could not be broken cleanly: {exception}");
                if (crate.Unit != null)
                {
                    UnityEngine.Object.Destroy(crate.Unit.gameObject);
                }
            }
        }

        public int TakeFrom(CratePileId pile, int count, ArenaWorldPoint near, float radius) =>
            TakeFrom(pile, count, near, radius, null);

        /// <summary>
        /// The same collection, additionally reporting what was picked up into <paramref name="looks"/>.
        /// </summary>
        /// <remarks>
        /// So a carrier can show its real load. What is on a goblin's back is a stack of whatever it actually took
        /// off the pile — barrels, boxes, and the occasional thing that should not be dropped — and a stack that
        /// showed one kind for all of them was hiding the one piece of information a player most wants from the
        /// route. The kinds stay entirely inside this adapter: the carrier asks for a look, never for a "kind".
        /// </remarks>
        internal int TakeFrom(
            CratePileId pile, int count, ArenaWorldPoint near, float radius, List<CrateLook> looks)
        {
            if (count <= 0)
            {
                return 0;
            }

            var from = new Vector3(near.X, 0f, near.Z);
            var reach = radius * radius;
            var taken = 0;
            for (var index = _crates.Count - 1; index >= 0 && taken < count; index--)
            {
                var crate = _crates[index];
                if (!IsOn(crate, pile))
                {
                    continue;
                }

                // Only what is within arm's reach of the taker, so collecting one heap does not empty another.
                if (crate.Unit != null)
                {
                    var here = crate.Unit.transform.position;
                    if ((new Vector3(here.x, 0f, here.z) - from).sqrMagnitude > reach)
                    {
                        continue;
                    }
                }

                // Picked up, not destroyed: no loot, no break effect, no sound. The crate reappears where the
                // carrier sets it down.
                if (crate.Unit != null)
                {
                    try
                    {
                        UnityEngine.Object.Destroy(crate.Unit.gameObject);
                    }
                    catch (Exception exception)
                    {
                        _logger?.LogWarning($"[crate] a crate could not be picked up: {exception.Message}");
                        continue;
                    }
                }

                looks?.Add(crate.Look);
                _crates.RemoveAt(index);
                taken++;
            }

            return taken;
        }

        public bool TryFindNearestResting(CratePileId pile, ArenaWorldPoint near, out ArenaWorldPoint at)
        {
            at = default(ArenaWorldPoint);
            var from = new Vector3(near.X, 0f, near.Z);
            var nearest = float.MaxValue;
            var found = false;

            for (var index = 0; index < _crates.Count; index++)
            {
                var crate = _crates[index];
                if (!IsOn(crate, pile))
                {
                    continue;
                }

                var here = crate.Unit.transform.position;
                var distance = (new Vector3(here.x, 0f, here.z) - from).sqrMagnitude;
                if (distance >= nearest)
                {
                    continue;
                }

                nearest = distance;
                at = ToPoint(here);
                found = true;
            }

            return found;
        }

        /// <summary>
        /// Keep the abandoned cargo bounded. Carriers reclaim what they can reach, but a fight where they are
        /// killed faster than they collect would still silt the level up with bodies, so the oldest loose crates
        /// are quietly removed past a ceiling. A backstop, not the mechanism: at any sane rate the goblins get
        /// there first and this never fires.
        /// </summary>
        private void CapLooseCrates()
        {
            var loose = RestingOn(CratePileId.Loose);
            if (loose <= MaxLooseCrates)
            {
                return;
            }

            var over = loose - MaxLooseCrates;
            for (var index = 0; index < _crates.Count && over > 0; index++)
            {
                var crate = _crates[index];
                if (!IsOn(crate, CratePileId.Loose))
                {
                    continue;
                }

                if (crate.Unit != null)
                {
                    UnityEngine.Object.Destroy(crate.Unit.gameObject);
                }

                _crates.RemoveAt(index);
                index--;
                over--;
            }

            _logger?.Log($"[crate] {loose - MaxLooseCrates} abandoned crate(s) cleared; the loose pile was over "
                + $"its ceiling of {MaxLooseCrates}.");
        }

        /// <summary>
        /// Say whether breaking this crate should pay out the game's own loot.
        /// </summary>
        /// <remarks>
        /// <para><b>A crate on a pile never pays.</b> The room produces destructibles continuously and carries them
        /// to the boss, so a standing pile is an endless supply of breakables: paying for those would turn the
        /// supply line into a loot farm and make ignoring the fight the best way to play it. Only what the boss has
        /// actually sent at a player is worth anything.</para>
        /// <para><b>And then only sometimes.</b> Even a barrage is a lot of crates; at a full pay-out the fight
        /// buries the floor in pickups. See <see cref="LootChance"/>.</para>
        /// </remarks>
        private void SetLootAllowed(Breakable breakable, bool allowed)
        {
            if (breakable == null || _preventDroppingLoot == null)
            {
                return;
            }

            try
            {
                _preventDroppingLoot.SetValue(breakable, !allowed);
            }
            catch (Exception exception)
            {
                _logger?.LogWarning($"[crate] a crate's loot rule could not be set: {exception.Message}");
            }
        }

        private static ArenaWorldPoint ToPoint(Vector3 position) =>
            new ArenaWorldPoint(position.x, position.y, position.z);

        private int ResolveBreakableLayer()
        {
            var layer = LayerMask.NameToLayer(BreakableLayerName);
            // A missing layer is not fatal — the barrel still flies and breaks; only weapon fire might not register,
            // which the in-game test will show. Default layer keeps it visible rather than dropping it.
            return layer >= 0 ? layer : 0;
        }

        /// <summary>The first GameObject location in the live catalog whose asset path contains
        /// <paramref name="pathFragment"/>. On a miss, <paramref name="diagnostic"/> reports a few nearby
        /// destructible-looking paths so the real name is visible in one log rather than another guess.</summary>
        private static IResourceLocation FindLocation(string pathFragment, string avoidFragment, out string diagnostic) =>
            FindLocationOfType(pathFragment, avoidFragment, typeof(GameObject), out diagnostic);

        private static IResourceLocation FindLocationOfType(
            string pathFragment, string avoidFragment, Type resourceType, out string diagnostic)
        {
            diagnostic = null;
            var nearby = new List<string>();

            foreach (var locator in Addressables.ResourceLocators)
            {
                IEnumerable<IResourceLocation> locations;
                try
                {
                    locations = locator.AllLocations;
                }
                catch (Exception)
                {
                    continue; // some locators do not enumerate; skip them
                }

                if (locations == null)
                {
                    continue;
                }

                foreach (var location in locations)
                {
                    if (location?.InternalId == null || location.ResourceType != resourceType)
                    {
                        continue;
                    }

                    var id = location.InternalId;
                    if (id.IndexOf(pathFragment, StringComparison.OrdinalIgnoreCase) >= 0
                        && (avoidFragment == null || id.IndexOf(avoidFragment, StringComparison.OrdinalIgnoreCase) < 0))
                    {
                        return location;
                    }

                    if (nearby.Count < 12 && LooksLikeADestructible(id))
                    {
                        nearby.Add(id);
                    }
                }
            }

            diagnostic = nearby.Count > 0
                ? "nearby destructible-looking paths: " + string.Join("; ", nearby)
                : "no destructible-looking paths were found in the catalog either.";
            return null;
        }

        private static bool LooksLikeADestructible(string id) =>
            id.IndexOf("Crate", StringComparison.OrdinalIgnoreCase) >= 0
            || id.IndexOf("Barrel", StringComparison.OrdinalIgnoreCase) >= 0
            || id.IndexOf("Breakable", StringComparison.OrdinalIgnoreCase) >= 0;

        private static FieldInfo PrivateField(Type type, string name) =>
            type?.GetField(name, BindingFlags.Instance | BindingFlags.NonPublic);

        /// <summary>How to build one kind of destructible: which vanilla unit it is, and where its mesh, material,
        /// and break effect come from. A cube kind needs no model at all.</summary>
        /// <summary>
        /// What one destructible looks like: the game's own mesh and material for its kind.
        /// </summary>
        /// <remarks>
        /// Shared rather than resolved a second time — this port already located and holds them, and loading them
        /// again elsewhere would mean a second set of handles to release and a second chance to render magenta. A
        /// kind built on something with neither (a plain cube) carries an empty look, and whoever draws it falls
        /// back to a box.
        /// </remarks>
        internal readonly struct CrateLook
        {
            public CrateLook(Mesh mesh, Material material)
            {
                Mesh = mesh;
                Material = material;
            }

            public Mesh Mesh { get; }

            public Material Material { get; }

            public bool Known => Mesh != null && Material != null;
        }

        private sealed class DestructibleSpec
        {
            public UnitId Unit;
            public string Name;
            public bool CubeMesh;
            public string MeshPathFragment;
            public string MaterialPathFragment;
            public string BreakEffectPathFragment;

            /// <summary>What this kind explodes as when it dies, or <c>None</c> for one that simply breaks.</summary>
            public ExplosionTypes Explosion;

            /// <summary>A second mesh sitting on top of the body, for a kind whose whole point is that a player can
            /// tell it apart at a glance. Optional: most kinds are one piece.</summary>
            public string TopMeshPathFragment;

            public string TopMaterialPathFragment;
        }

        /// <summary>One assembled kind: the inactive template every unit of it is cloned from, its definition, and
        /// the addressable handles held alive for as long as the template lives.</summary>
        private sealed class DestructibleTemplate
        {
            public UnitSO Definition;
            public GameObject Template;
            public string Name;

            /// <summary>Whether units of this kind go off when they die.</summary>
            public bool Explodes;

            /// <summary>What one of these looks like, for something that has to <i>look</i> like cargo without
            /// being any — a load riding on a goblin's back. Read off the assembled template once.</summary>
            public CrateLook Look;
            public readonly List<AsyncOperationHandle> Handles = new List<AsyncOperationHandle>();

            /// <summary>Destroy the template and release its held content. Safe to call on a half-built kind.</summary>
            public void Release()
            {
                if (Template != null)
                {
                    UnityEngine.Object.Destroy(Template);
                    Template = null;
                }

                foreach (var handle in Handles)
                {
                    try { Addressables.Release(handle); }
                    catch (Exception) { /* not loaded / already released */ }
                }

                Handles.Clear();
            }
        }

        /// <summary>Which of its three lives a crate is living right now.</summary>
        private enum Phase
        {
            /// <summary>Dropped and left to the game's physics — falling, at rest, or piling.</summary>
            Resting,

            /// <summary>Lifted off the pile and rising to its hover point under our control, before it fires.</summary>
            Lifting,

            /// <summary>Riding the arc we drive toward its landing spot.</summary>
            Flying,

            /// <summary>Riding a short arc out of a carrier's hands to the spot it will rest on. Unlike a flight
            /// this hurts nobody and breaks nothing: at the end of it the crate is simply on the ground.</summary>
            Tossing,
        }

        /// <summary>
        /// One crate and everything the port needs to carry it through its life. A single object holds the crate in
        /// every phase, so there is one authority for it and phases change in place — a resting crate is lifted, a
        /// lifted crate is fired — rather than the crate migrating between separate lists.
        /// </summary>
        private sealed class ManagedCrate
        {
            public ManagedCrate(Unit unit, Breakable breakable, int id, bool explodes, CrateLook look)
            {
                Unit = unit;
                Breakable = breakable;
                Id = id;
                Explodes = explodes;
                Look = look;
            }

            /// <summary>What this one looks like, so a carrier hauling it can show the thing it is carrying rather
            /// than a stand-in for it.</summary>
            public CrateLook Look { get; }

            /// <summary>Whether this one goes off when it dies rather than simply breaking.</summary>
            public bool Explodes { get; }

            /// <summary>Seconds left on the fuse of a barrel that has landed, or 0 when nothing is burning down.
            /// Only a barrel the boss <i>threw</i> ever gets one: one standing where it was made is the game's
            /// ordinary barrel and goes off when somebody destroys it.</summary>
            public float Fuse { get; set; }

            /// <summary>Which destructible this is, counted in the order they were made. Every peer makes the
            /// same ones from the same commands in the same order, so the number means the same crate on all of
            /// them — the identity a session layer matching by spawn position cannot have for crates that are all
            /// heaped in the same few spots.</summary>
            public int Id { get; }

            public Unit Unit { get; }

            public Breakable Breakable { get; }

            /// <summary>Which phase the crate is in. A crate starts resting (the enum default); the motion-begin
            /// methods below are the only things that move it on, so a phase can never be entered without its
            /// motion being set up in the same step.</summary>
            public Phase Phase { get; private set; }

            /// <summary>Which heap this crate belongs to while it rests, and therefore whether the boss may fire
            /// it. Meaningless once it is in the air — it is nobody's pile then, and it never rests again.</summary>
            public CratePileId Pile { get; set; }

            /// <summary>Whether shooting this crate down will pay, once it has actually been thrown. Decided when
            /// it is lifted and granted when it is released, so the telegraph never pays.</summary>
            public bool LootWhenFired { get; set; }

            /// <summary>Time spent in the current phase's motion; reset when a phase begins.</summary>
            public float Elapsed { get; set; }

            // The flight (also the fired half of a volley): a parabola from Start to Target.
            public Vector3 Start { get; private set; }

            public Vector3 Target { get; private set; }

            public float FlightSeconds { get; private set; }

            public float ApexHeight { get; private set; }

            // The lift off the pile: straight up from LiftFrom to Hover, then a hold, then the flight to Target.
            public Vector3 LiftFrom { get; private set; }

            public Vector3 Hover { get; private set; }

            public float LiftSeconds { get; private set; }

            public float HoldSeconds { get; private set; }

            /// <summary>Enter the flying phase: the arc from <paramref name="start"/> to
            /// <paramref name="target"/>.</summary>
            public void BeginFlight(Vector3 start, Vector3 target, float flightSeconds, float apexHeight)
            {
                Start = start;
                Target = target;
                FlightSeconds = flightSeconds > 0f ? flightSeconds : 0.01f;
                ApexHeight = apexHeight;
                Elapsed = 0f;
                Phase = Phase.Flying;
            }

            /// <summary>Enter the tossing phase: a short arc out of a carrier's hands onto the ground, ending in
            /// the crate resting on <paramref name="pile"/>.</summary>
            public void BeginToss(
                Vector3 start, Vector3 target, float flightSeconds, float apexHeight, CratePileId pile)
            {
                Start = start;
                Target = target;
                FlightSeconds = flightSeconds > 0f ? flightSeconds : 0.01f;
                ApexHeight = apexHeight;
                Pile = pile;
                Elapsed = 0f;
                Phase = Phase.Tossing;
            }

            /// <summary>Lie where a throw put it, on nobody's pile: what a barrel does while its fuse burns.
            /// The boss cannot fire it again — it is not on a pile — and a player can still shoot it.</summary>
            public void RestWhereItLanded()
            {
                Elapsed = 0f;
                Pile = CratePileId.Loose;
                Phase = Phase.Resting;
            }

            /// <summary>Come to rest where the toss ended — back under the game's own physics, on its pile.</summary>
            public void Settle()
            {
                Elapsed = 0f;
                Phase = Phase.Resting;
            }

            /// <summary>Enter the lifting phase: the rise off the pile, remembering the scattered
            /// <paramref name="target"/> the crate will be fired at once it has lifted and held.</summary>
            /// <param name="place">This crate's place in the volley, which is how long past the shared telegraph
            /// it waits before being released — the whole hover rises together and is then fired at the shape's
            /// rate rather than all in one instant.</param>
            public void BeginLift(Vector3 liftFrom, Vector3 hover, Vector3 target, CrateVolleyShape shape, int place)
            {
                LiftFrom = liftFrom;
                Hover = hover;
                Target = target;
                LiftSeconds = shape.LiftSeconds > 0f ? shape.LiftSeconds : 0.01f;

                var stagger = shape.FireIntervalSeconds > 0f ? shape.FireIntervalSeconds * place : 0f;
                var hold = (shape.HoldSeconds > 0f ? shape.HoldSeconds : 0f) + stagger;
                HoldSeconds = hold;
                FlightSeconds = shape.FlightSeconds > 0f ? shape.FlightSeconds : 0.01f;
                ApexHeight = shape.ApexHeight;
                Elapsed = 0f;
                Phase = Phase.Lifting;
            }
        }
    }
}
