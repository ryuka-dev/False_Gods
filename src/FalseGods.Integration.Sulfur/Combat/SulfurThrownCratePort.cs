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
        // The destructible the boss throws. The wooden crate's mesh is not addressable while in the caves, but the
        // barrel's is; both are ordinary units, so the definition and the mesh are the only difference, and the
        // barrel is the one reachable for a live build. ExplosiveBarrel is the obvious later variation.
        private static readonly UnitId ThrownUnit = UnitIds.WoodenBarrel;

        // The barrel model, found in the live content catalog by its own asset path — a fixed GUID cannot be used
        // because the ones the reverse-engineered project assigns are not the game's real catalog keys, and the
        // path survives a re-key across game versions.
        private const string MeshPathFragment = "Barrel_Wood";

        // The barrel's real body material (a wood+metal atlas), qualified by its folder so the search does not
        // match the wood PARTICLE material of the same stem. Tried directly from the catalog: if it is
        // addressable it dresses the barrel exactly, and no scavenged stand-in is needed.
        private const string BodyMaterialPathFragment = "Barrels/BarrelWood";

        // The break burst the game spawns when a barrel dies — debris and dust. Assembled units carry no such
        // effect (it is authored on the prefab, not derived from the definition), so it is loaded and attached.
        // Its debris also carries a real wood material, which dresses the body better than the raw model import.
        private const string BreakEffectPathFragment = "BarrelBreakEffect";

        // The layer the vanilla destructibles sit on, so the game's weapon fire finds our body the same way.
        private const string BreakableLayerName = "Breakable";

        private readonly ILogger _logger;

        // Every crate this port owns, in whatever phase of its life. One crate has a single authority here: it is
        // resting (the game's physics owns its position), lifting off the pile, or flying an arc we drive — and it
        // moves between those phases in place rather than migrating between lists. A crate a player shoots is
        // broken and destroyed by the game, leaving a null we prune on the next tick, in any phase.
        private readonly List<ManagedCrate> _crates = new List<ManagedCrate>();

        private UnitSO _definition;
        private GameObject _template;
        private AsyncOperationHandle<GameObject> _meshHandle;
        private bool _meshHandleValid;
        private AsyncOperationHandle<GameObject> _breakEffectHandle;
        private bool _breakEffectHandleValid;
        private AsyncOperationHandle<Material> _materialHandle;
        private bool _materialHandleValid;
        private FieldInfo _preventDroppingLoot;
        private bool _warnedAboutLootFlag;

        public SulfurThrownCratePort(ILogger logger = null)
        {
            _logger = logger;
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
            if (_template != null)
            {
                return true;
            }

            try
            {
                _definition = ThrownUnit.GetAsset();
                if (_definition == null)
                {
                    _logger?.LogWarning("[crate] the game has no definition for the destructible; throwing is unavailable.");
                    return false;
                }

                var mesh = LoadBarrelMesh(out var meshMaterial, out var meshError);
                if (mesh == null)
                {
                    _logger?.LogWarning($"[crate] no destructible mesh could be sourced; throwing is unavailable: {meshError}");
                    return false;
                }

                // The break effect is a nicety, not a requirement: without it the barrel still flies, breaks, and
                // drops loot — it just vanishes without debris. A missing one is logged, not fatal. The effect
                // also carries the barrel's own break sound and a wood material, neither of which an assembled
                // unit has otherwise.
                var breakEffect = LoadBreakEffect(out var debrisMaterial, out var breakSound, out var breakEffectError);
                if (breakEffect == null)
                {
                    _logger?.LogWarning($"[crate] no break effect could be sourced; barrels will vanish without debris: {breakEffectError}");
                }

                // Prefer the barrel's own body material if the catalog will give it to us directly (the exact
                // wood+metal look); otherwise the wood material scavenged from the break debris; otherwise the
                // model's own import material.
                var realMaterial = LoadBodyMaterial();
                var bodyMaterial = realMaterial != null ? realMaterial
                    : debrisMaterial != null ? debrisMaterial
                    : meshMaterial;

                _template = BuildTemplate(mesh, bodyMaterial, breakEffect, breakSound, out var templateError);
                if (_template == null)
                {
                    _logger?.LogWarning($"[crate] the destructible template could not be assembled; throwing is unavailable: {templateError}");
                    ReleaseMesh();
                    ReleaseBreakEffect();
                    return false;
                }

                // Looked up once: the loot gate is private, and finding it per throw would be wasteful.
                _preventDroppingLoot = PrivateField(typeof(Breakable), "preventDroppingLoot");
                if (_preventDroppingLoot == null && !_warnedAboutLootFlag)
                {
                    _warnedAboutLootFlag = true;
                    _logger?.LogWarning("[crate] could not find the loot gate on the game's breakable; a landing "
                        + "crate will be removed quietly instead of breaking. Landing still drops nothing.");
                }

                _logger?.Log("[crate] destructible content ready.");
                return true;
            }
            catch (Exception exception)
            {
                _logger?.LogWarning($"[crate] destructible content could not be prepared: {exception}");
                return false;
            }
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

                // The game's own spawn: clones the template, sets its stats from the definition on it, runs Spawn,
                // finds its room, and registers it as a live unit — so it is a real destructible, not a look-alike.
                var unit = UnitSO.SpawnUnit(_definition, _template, start, Quaternion.identity);
                if (unit == null)
                {
                    _logger?.LogWarning("[crate] the game returned no unit for the destructible.");
                    return false;
                }

                // The clone is inactive because the template is; waking it runs the unit's own Start after Spawn
                // has already marked it spawned, so nothing re-initialises.
                unit.gameObject.SetActive(true);

                var breakable = unit as Breakable;
                if (breakable != null)
                {
                    // Ours to decide when it lands, not the physics engine's.
                    breakable.BreakOnFirstContact = false;
                    breakable.TakeDamageOnCollision = false;
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
                var unit = UnitSO.SpawnUnit(_definition, _template, where, Quaternion.identity);
                if (unit == null)
                {
                    _logger?.LogWarning("[crate] the game returned no unit for the resting destructible.");
                    return false;
                }

                unit.gameObject.SetActive(true);

                var breakable = unit as Breakable;
                if (breakable != null)
                {
                    // A dropped crate must survive the fall and the jostle of stacking, so neither the game's
                    // shatter-on-contact nor its collision-speed damage may fire; it stays shootable regardless
                    // (weapon fire reaches the Hitmesh on its own path). Whether a hard drop should shatter is a
                    // later tuning call — the pile has to be reliable first.
                    breakable.BreakOnFirstContact = false;
                    breakable.TakeDamageOnCollision = false;
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
                        if (!AdvanceFlight(crate, deltaSeconds))
                        {
                            _crates.RemoveAt(index);
                            Land(crate);
                        }

                        break;
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

        /// <summary>Advance a flying crate along its arc. Returns false when it has arrived (the caller lands it).</summary>
        private static bool AdvanceFlight(ManagedCrate crate, float deltaSeconds)
        {
            crate.Elapsed += deltaSeconds;
            var progress = crate.Elapsed / crate.FlightSeconds;
            if (progress >= 1f)
            {
                return false;
            }

            var ground = Vector3.Lerp(crate.Start, crate.Target, BallisticArc.HorizontalFraction(progress));
            ground.y += BallisticArc.Height(progress, crate.ApexHeight);
            crate.Unit.transform.position = ground;
            return true;
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

            if (_template != null)
            {
                UnityEngine.Object.Destroy(_template);
                _template = null;
            }

            // The handles were held because every barrel cloned from the template shares their mesh, material, and
            // break effect; only now that the template and its clones are gone is it safe to release them.
            ReleaseMesh();
            ReleaseBreakEffect();
            ReleaseMaterial();
        }

        /// <summary>
        /// Find the barrel model in the game's live content catalog by its own asset path and hand back its mesh
        /// (and a material to render it with). The handle is held so the mesh stays alive for every barrel built
        /// from it; it is released in <see cref="Release"/>. Searching the catalog rather than naming a fixed GUID
        /// is what makes this independent of the reverse-engineered project's invented keys.
        /// </summary>
        private Mesh LoadBarrelMesh(out Material material, out string error)
        {
            material = null;
            error = null;

            var location = FindLocation(MeshPathFragment, null, out var searchDiagnostic);
            if (location == null)
            {
                error = $"no catalog entry whose path contains '{MeshPathFragment}'. {searchDiagnostic}";
                return null;
            }

            try
            {
                var handle = Addressables.LoadAssetAsync<GameObject>(location);
                var model = handle.WaitForCompletion();
                if (handle.Status != AsyncOperationStatus.Succeeded || model == null)
                {
                    error = $"barrel model at '{location.InternalId}' did not load (status={handle.Status})";
                    try { Addressables.Release(handle); } catch (Exception) { }
                    return null;
                }

                var filter = model.GetComponentInChildren<MeshFilter>();
                if (filter == null || filter.sharedMesh == null)
                {
                    error = $"barrel model at '{location.InternalId}' has no mesh";
                    try { Addressables.Release(handle); } catch (Exception) { }
                    return null;
                }

                var renderer = model.GetComponentInChildren<MeshRenderer>();
                material = renderer != null ? renderer.sharedMaterial : null;

                _meshHandle = handle;
                _meshHandleValid = true;
                _logger?.Log($"[crate] barrel mesh found at '{location.InternalId}'.");
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
        private GameObject BuildTemplate(Mesh mesh, Material material, GameObject breakEffect, object breakSound, out string error)
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
                breakable.unitSO = _definition;

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
        private GameObject LoadBreakEffect(out Material debrisMaterial, out object breakSound, out string error)
        {
            debrisMaterial = null;
            breakSound = null;
            error = null;

            // Prefer the plain full-quality effect (bursts outward AND carries the break sound): an exact
            // "...Effect.prefab" match skips both the "_LQ" variant (no sound) and the "(Exploding)" variant
            // (debris drops instead of bursting). Fall back to the LQ burst, then to anything.
            var location = FindLocation(BreakEffectPathFragment + ".prefab", null, out var searchDiagnostic)
                ?? FindLocation(BreakEffectPathFragment + "_LQ", null, out searchDiagnostic)
                ?? FindLocation(BreakEffectPathFragment, null, out searchDiagnostic);
            if (location == null)
            {
                error = $"no catalog entry whose path contains '{BreakEffectPathFragment}'. {searchDiagnostic}";
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

                _breakEffectHandle = handle;
                _breakEffectHandleValid = true;
                _logger?.Log($"[crate] break effect found at '{location.InternalId}' "
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
        private Material LoadBodyMaterial()
        {
            var location = FindLocationOfType(BodyMaterialPathFragment, null, typeof(Material), out _);
            if (location == null)
            {
                _logger?.Log($"[crate] no addressable body material '{BodyMaterialPathFragment}'; using a stand-in.");
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

                _materialHandle = handle;
                _materialHandleValid = true;
                _logger?.Log($"[crate] real body material found at '{location.InternalId}'.");
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

        /// <summary>The barrel arrived: break it the game's way, but with the loot switched off.</summary>
        private void Land(ManagedCrate crate)
        {
            try
            {
                crate.Unit.transform.position = crate.Target;

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
                _logger?.LogWarning($"[crate] a landing crate could not be broken cleanly: {exception}");
                if (crate.Unit != null)
                {
                    UnityEngine.Object.Destroy(crate.Unit.gameObject);
                }
            }
        }

        private int ResolveBreakableLayer()
        {
            var layer = LayerMask.NameToLayer(BreakableLayerName);
            // A missing layer is not fatal — the barrel still flies and breaks; only weapon fire might not register,
            // which the in-game test will show. Default layer keeps it visible rather than dropping it.
            return layer >= 0 ? layer : 0;
        }

        private void ReleaseMesh()
        {
            if (_meshHandleValid)
            {
                try { Addressables.Release(_meshHandle); }
                catch (Exception) { /* not loaded / already released */ }
                _meshHandleValid = false;
            }
        }

        private void ReleaseBreakEffect()
        {
            if (_breakEffectHandleValid)
            {
                try { Addressables.Release(_breakEffectHandle); }
                catch (Exception) { /* not loaded / already released */ }
                _breakEffectHandleValid = false;
            }
        }

        private void ReleaseMaterial()
        {
            if (_materialHandleValid)
            {
                try { Addressables.Release(_materialHandle); }
                catch (Exception) { /* not loaded / already released */ }
                _materialHandleValid = false;
            }
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
