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
using ILogger = FalseGods.RuntimeContracts.Diagnostics.ILogger;

namespace FalseGods.Integration.Sulfur.Combat
{
    /// <summary>
    /// <see cref="IThrownCratePort"/> over SULFUR's own destructibles: it spawns a real <c>Breakable</c> unit,
    /// carries it along the simulation's arc, and lets it break the game's way when a player shoots it.
    /// </summary>
    /// <remarks>
    /// <para><b>Spawning is prepared once and then free.</b> The game's own spawn is
    /// <c>UnitSO.SpawnUnit(unitSo, prefab, position, rotation)</c> — public, synchronous, and the same call its
    /// level generation makes — but reaching the prefab is an addressable load. That load happens once in
    /// <see cref="Prepare"/>, so a volley mid-fight is pure instantiation with nothing to wait for.</para>
    /// <para><b>Two vanilla behaviours are switched off for the flight.</b> A breakable normally shatters on
    /// first contact or takes damage from its own collision speed; either would destroy a crate the moment it
    /// grazed anything, and both are decided by the physics we are deliberately not using. With them off and the
    /// body kinematic, arrival is ours to declare.</para>
    /// <para><b>Landing breaks the crate without paying out.</b> The loot is gated inside the game's break by a
    /// private flag, so landing sets that flag before breaking — which keeps the real break, with its sound and
    /// its debris, and only takes away the reward. Reflection is the price of using the game's own break rather
    /// than a silent destroy; if the flag ever moves, the fallback destroys the crate quietly, which loses the
    /// effect but never the rule that landing pays nothing.</para>
    /// <para><b>A crate shot down needs no help from us.</b> It is a unit on the game's hit path, so it takes the
    /// player's fire, dies the game's way, and drops loot under whatever rules the session has. We only notice
    /// it is gone and stop carrying it.</para>
    /// </remarks>
    public sealed class SulfurThrownCratePort : IThrownCratePort
    {
        // The crate the boss throws. WoodenBarrel is the other stocked destructible, and ExplosiveBarrel is the
        // obvious later variation; all three are ordinary units, so the choice is one identifier.
        private static readonly UnitId CrateUnit = UnitIds.WoodenCrate;

        private readonly ILogger _logger;
        private readonly List<Flight> _flights = new List<Flight>();

        private UnitSO _crateDefinition;
        private GameObject _cratePrefab;
        private FieldInfo _preventDroppingLoot;
        private bool _warnedAboutLootFlag;

        public SulfurThrownCratePort(ILogger logger = null)
        {
            _logger = logger;
        }

        public int InFlight => _flights.Count;

        public bool Prepare()
        {
            if (_cratePrefab != null)
            {
                return true;
            }

            try
            {
                _crateDefinition = CrateUnit.GetAsset();
                if (_crateDefinition == null)
                {
                    _logger?.LogWarning("[crate] the game has no definition for the crate unit; throwing is unavailable.");
                    return false;
                }

                var loader = _crateDefinition.FetchAndLoadUnitLoader();
                _cratePrefab = loader.IsDone ? loader.Result : loader.WaitForCompletion();
                if (_cratePrefab == null)
                {
                    _logger?.LogWarning("[crate] the crate unit failed to load; throwing is unavailable.");
                    return false;
                }

                // Looked up once: the loot gate is private, and finding it per crate would be wasteful.
                _preventDroppingLoot = AccessTools_Field(typeof(Breakable), "preventDroppingLoot");
                if (_preventDroppingLoot == null && !_warnedAboutLootFlag)
                {
                    _warnedAboutLootFlag = true;
                    _logger?.LogWarning("[crate] could not find the loot gate on the game's breakable; a landing "
                        + "crate will be removed quietly instead of breaking. Landing still drops nothing.");
                }

                _logger?.Log("[crate] crate content ready.");
                return true;
            }
            catch (Exception exception)
            {
                _logger?.LogWarning($"[crate] crate content could not be prepared: {exception}");
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

                var unit = UnitSO.SpawnUnit(_crateDefinition, _cratePrefab, start, Quaternion.identity);
                if (unit == null)
                {
                    _logger?.LogWarning("[crate] the game returned no unit for the crate.");
                    return false;
                }

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

                _flights.Add(new Flight(unit, breakable, start, target, flightSeconds, apexHeight));
                return true;
            }
            catch (Exception exception)
            {
                _logger?.LogWarning($"[crate] the crate could not be thrown: {exception}");
                return false;
            }
        }

        public void Advance(float deltaSeconds)
        {
            for (var index = _flights.Count - 1; index >= 0; index--)
            {
                var flight = _flights[index];

                // Shot out of the air: the game already broke it, dropped its loot, and destroyed it.
                if (flight.Unit == null)
                {
                    _flights.RemoveAt(index);
                    continue;
                }

                flight.Elapsed += deltaSeconds;
                var progress = flight.Elapsed / flight.FlightSeconds;

                if (progress >= 1f)
                {
                    _flights.RemoveAt(index);
                    Land(flight);
                    continue;
                }

                var ground = Vector3.Lerp(flight.Start, flight.Target, BallisticArc.HorizontalFraction(progress));
                ground.y += BallisticArc.Height(progress, flight.ApexHeight);
                flight.Unit.transform.position = ground;
            }
        }

        public void Release()
        {
            for (var index = 0; index < _flights.Count; index++)
            {
                var flight = _flights[index];
                if (flight.Unit != null)
                {
                    // Tearing down is not a landing and certainly not a kill: no loot, no noise.
                    UnityEngine.Object.Destroy(flight.Unit.gameObject);
                }
            }

            _flights.Clear();
        }

        /// <summary>The crate arrived: break it the game's way, but with the loot switched off.</summary>
        private void Land(Flight flight)
        {
            try
            {
                flight.Unit.transform.position = flight.Target;

                if (flight.Breakable != null && _preventDroppingLoot != null)
                {
                    _preventDroppingLoot.SetValue(flight.Breakable, true);
                    flight.Breakable.Break();
                    return;
                }

                UnityEngine.Object.Destroy(flight.Unit.gameObject);
            }
            catch (Exception exception)
            {
                _logger?.LogWarning($"[crate] a landing crate could not be broken cleanly: {exception}");
                if (flight.Unit != null)
                {
                    UnityEngine.Object.Destroy(flight.Unit.gameObject);
                }
            }
        }

        /// <summary>The private-instance field lookup, kept in one place so the reflection is easy to find.</summary>
        private static FieldInfo AccessTools_Field(Type type, string name) =>
            type?.GetField(name, BindingFlags.Instance | BindingFlags.NonPublic);

        private sealed class Flight
        {
            public Flight(Unit unit, Breakable breakable, Vector3 start, Vector3 target, float flightSeconds, float apexHeight)
            {
                Unit = unit;
                Breakable = breakable;
                Start = start;
                Target = target;
                FlightSeconds = flightSeconds;
                ApexHeight = apexHeight;
            }

            public Unit Unit { get; }

            public Breakable Breakable { get; }

            public Vector3 Start { get; }

            public Vector3 Target { get; }

            public float FlightSeconds { get; }

            public float ApexHeight { get; }

            public float Elapsed { get; set; }
        }
    }
}
