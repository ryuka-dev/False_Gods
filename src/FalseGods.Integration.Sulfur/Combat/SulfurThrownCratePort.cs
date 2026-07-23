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
        };

        // The layer the vanilla destructibles sit on, so the game's weapon fire finds our body the same way.
        private const string BreakableLayerName = "Breakable";

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
        private bool _prepared;
        private int _nextKind;
        private FieldInfo _preventDroppingLoot;
        private bool _warnedAboutLootFlag;
        private int _wallMask;
        private bool _wallMaskBuilt;

        public SulfurThrownCratePort(ILogger logger = null, IThrownCrateImpact impact = null)
        {
            _logger = logger;
            _impact = impact;
        }

        public int InFlight => CountWhere(phase => phase != Phase.Resting);

        public int Resting => CountWhere(phase => phase == Phase.Resting);

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
            if (body == null)
            {
                _logger?.LogWarning($"[crate] the {spec.Name} template could not be assembled ({templateError}); skipped.");
                template.Release();
                return null;
            }

            template.Template = body;
            return template;
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

        /// <summary>The next kind to spawn, cycled so a pile or a volley mixes the kinds evenly.</summary>
        private DestructibleTemplate PickKind() => _templates[_nextKind++ % _templates.Count];

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

                var unit = SpawnFrom(PickKind(), start, out var breakable);
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

                var crate = new ManagedCrate(unit, breakable);
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

        public bool Drop(ArenaWorldPoint at)
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
                var unit = SpawnFrom(PickKind(), where, out var breakable);
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

                // A new crate is resting by default — the game's physics owns it until it is lifted.
                _crates.Add(new ManagedCrate(unit, breakable));
                return true;
            }
            catch (Exception exception)
            {
                _logger?.LogWarning($"[crate] the crate could not be dropped: {exception}");
                return false;
            }
        }

        // Salt for the per-crate "lead this one or not" coin, kept clear of the scatter's salts (0..2*count+2) so
        // the choice is independent of where a crate lands within its slice.
        private const int LeadChoiceSalt = 40009;

        public int LaunchVolley(ArenaWorldPoint currentCenter, ArenaWorldPoint leadCenter, CrateVolleyShape shape)
        {
            if (!Prepare())
            {
                return 0;
            }

            // Only crates already resting on the pile are lifted; a crate already in the air stays there.
            var chosen = new List<ManagedCrate>();
            for (var index = 0; index < _crates.Count && chosen.Count < shape.Count; index++)
            {
                var candidate = _crates[index];
                if (candidate.Phase == Phase.Resting && candidate.Unit != null)
                {
                    chosen.Add(candidate);
                }
            }

            if (chosen.Count == 0)
            {
                return 0;
            }

            for (var index = 0; index < chosen.Count; index++)
            {
                var crate = chosen[index];

                // Each crate independently aims at where the player is now or where they are predicted to be, a
                // seeded coin so both spots are threatened in every volley — no single way of moving dodges it all.
                var leads = SeededRandom.Unit01(shape.Seed, LeadChoiceSalt + index) < shape.LeadShare;
                var center = leads ? leadCenter : currentCenter;

                // The scatter is seeded so every peer throwing this volley lands the crates the same way; the count
                // handed to the pattern is what actually flew, so a short pile still rings the target evenly.
                var offset = ShotgunSpread.Offset(
                    shape.Seed, index, chosen.Count, shape.SpreadMinRadius, shape.SpreadMaxRadius);
                var target = new Vector3(center.X + offset.X, center.Y, center.Z + offset.Z);

                var from = crate.Unit.transform.position;
                crate.BeginLift(from, from + Vector3.up * shape.LiftHeight, target, shape);

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

        public void Advance(float deltaSeconds)
        {
            for (var index = _crates.Count - 1; index >= 0; index--)
            {
                var crate = _crates[index];

                // Shot out of the air (or off the pile): the game already broke it, dropped its loot, and
                // destroyed it, in whatever phase it was in.
                if (crate.Unit == null)
                {
                    _crates.RemoveAt(index);
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

                    case Phase.Flying:
                    {
                        crate.Elapsed += deltaSeconds;
                        var progress = crate.Elapsed / crate.FlightSeconds;

                        if (progress >= 1f)
                        {
                            // Reached its landing spot: splash there, then break it, no loot.
                            _crates.RemoveAt(index);
                            Land(crate);
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

                        // Reached a player's body in the air: detonate on them.
                        if (_impact != null && _impact.Contact(ToPoint(to)))
                        {
                            _crates.RemoveAt(index);
                            BreakNoLoot(crate);
                        }

                        break;
                    }
                }
            }
        }

        /// <summary>Raise a crate off the pile to its hover point, hold it there a beat, then hand it to the arc.
        /// Returns nothing — a lift never resolves the crate; it becomes a flight, which the next tick advances.</summary>
        private static void AdvanceLift(ManagedCrate crate, float deltaSeconds)
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
        private void Land(ManagedCrate crate)
        {
            crate.Unit.transform.position = crate.Target;
            _impact?.Splash(ToPoint(crate.Target));
            BreakNoLoot(crate);
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
        private sealed class DestructibleSpec
        {
            public UnitId Unit;
            public string Name;
            public bool CubeMesh;
            public string MeshPathFragment;
            public string MaterialPathFragment;
            public string BreakEffectPathFragment;
        }

        /// <summary>One assembled kind: the inactive template every unit of it is cloned from, its definition, and
        /// the addressable handles held alive for as long as the template lives.</summary>
        private sealed class DestructibleTemplate
        {
            public UnitSO Definition;
            public GameObject Template;
            public string Name;
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
        }

        /// <summary>
        /// One crate and everything the port needs to carry it through its life. A single object holds the crate in
        /// every phase, so there is one authority for it and phases change in place — a resting crate is lifted, a
        /// lifted crate is fired — rather than the crate migrating between separate lists.
        /// </summary>
        private sealed class ManagedCrate
        {
            public ManagedCrate(Unit unit, Breakable breakable)
            {
                Unit = unit;
                Breakable = breakable;
            }

            public Unit Unit { get; }

            public Breakable Breakable { get; }

            /// <summary>Which phase the crate is in. A crate starts resting (the enum default); the motion-begin
            /// methods below are the only things that move it on, so a phase can never be entered without its
            /// motion being set up in the same step.</summary>
            public Phase Phase { get; private set; }

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

            /// <summary>Enter the lifting phase: the rise off the pile, remembering the scattered
            /// <paramref name="target"/> the crate will be fired at once it has lifted and held.</summary>
            public void BeginLift(Vector3 liftFrom, Vector3 hover, Vector3 target, CrateVolleyShape shape)
            {
                LiftFrom = liftFrom;
                Hover = hover;
                Target = target;
                LiftSeconds = shape.LiftSeconds > 0f ? shape.LiftSeconds : 0.01f;
                HoldSeconds = shape.HoldSeconds > 0f ? shape.HoldSeconds : 0f;
                FlightSeconds = shape.FlightSeconds > 0f ? shape.FlightSeconds : 0.01f;
                ApexHeight = shape.ApexHeight;
                Elapsed = 0f;
                Phase = Phase.Lifting;
            }
        }
    }
}
