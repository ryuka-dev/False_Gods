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
    /// The SULFUR implementation of <see cref="ICarrierPort"/>: the boss's ammunition is carried across the room
    /// by the game's own goblin civilians.
    /// </summary>
    /// <remarks>
    /// <para><b>The walk is the game's own scripted-walk path</b> (measured on v0.18.5):
    /// <c>Npc.SetForcedDestination(point)</c> followed by <c>Npc.ActivateBehaviour()</c>, which switches the
    /// creature's behaviour tree off and hands the destination to its agent; arrival is
    /// <c>AiAgent.onDestination</c>; the errand is ended with <c>AiAgent.StopOnCurrentPosition()</c> and a forced
    /// destination of <c>Vector3.zero</c>. The game drives its own units this way in
    /// <c>MakeUnitGoToPointTrigger</c> and in the Sniper boss's repositioning.</para>
    /// <para><b>Panic needs no suppressing.</b> A civilian flees because its behaviour tree tells it to, and the
    /// forced-destination path turns that tree off for the duration — so a goblin on an errand simply walks.
    /// The one thing that can defeat this is a unit whose <c>unitSO.canBeDeactivated</c> is false, because the
    /// game's own guard then refuses to switch the tree off; that is reported on the first carrier rather than
    /// left to show up as mysterious wandering.</para>
    /// <para><b>A carried load is not real crates.</b> A dozen carriers each holding a dozen live breakables would
    /// be hundreds of bodies walking around for something a player cannot shoot off a back anyway. The crates are
    /// taken out of the world at the production point, ride as a stack of plain meshes, and are made again as real
    /// destructibles when they are set down. Kill a loaded carrier and the whole load hits the floor as real,
    /// shootable crates — the interference the supply line is there to invite.</para>
    /// </remarks>
    public sealed class SulfurCarrierPort : ICarrierPort
    {
        /// <summary>The village. A civilian is the right unit for this: it is not a fighter, so a carrier is a
        /// logistics problem for the player rather than another enemy.</summary>
        private static readonly UnitId CarrierUnit = UnitIds.GoblinCivilian;

        /// <summary>How close to its destination a carrier counts as arrived, in metres. The agent's own
        /// <c>onDestination</c> is the primary signal; this is the fallback for a goblin that stopped just short
        /// of a point — against a wall, on the lip of a terrace — so a supply line cannot deadlock on one stuck
        /// carrier.</summary>
        private const float ArrivalRadius = 2.5f;

        /// <summary>How long a carrier may spend on one leg before it is assumed stuck and given the leg again.
        /// Long enough for the longest authored route plus a climb.</summary>
        private const float LegTimeoutSeconds = 45f;

        /// <summary>How long a carrier pauses at each end, so loading and unloading read as actions rather than
        /// the load teleporting between piles.</summary>
        private const float HandlingSeconds = 0.75f;

        /// <summary>Vertical gap between two crates of a carried stack, and how high the stack floats above the
        /// carrier's feet. Purely how the load looks.</summary>
        private const float StackSpacing = 0.55f;
        private const float StackBase = 1.9f;

        /// <summary>How a set-down load is laid out on the ground: a ring around the spot, and a short drop onto
        /// it. Crates are solid bodies, so a load released down one column interpenetrates and flings itself
        /// apart; ringed, each lands on its own patch and settles.</summary>
        private const float SetDownMinRadius = 1.2f;
        private const float SetDownMaxRadius = 4f;
        private const float SetDownHeight = 1.2f;

        /// <summary>The little arc a crate rides out of a carrier's hands to the ground. Short and high enough to
        /// read as the load being thrown down rather than crates appearing beside a goblin.</summary>
        private const float SetDownFlightSeconds = 0.55f;
        private const float SetDownApexHeight = 1.6f;

        /// <summary>How far a carrier can gather from where it stands. Comfortably wider than a set-down ring, so
        /// one heap is collected in one visit, and far short of the room, so it does not empty another.</summary>
        private const float PickUpReach = 9f;

        /// <summary>The tallest a stack is drawn. A carrier hauling a dozen crates would otherwise wear a mast;
        /// beyond this the load is still carried, just not all of it drawn.</summary>
        private const int MaxDrawnStack = 5;

        private readonly MonoBehaviour _host;
        private readonly IThrownCratePort _crates;
        private readonly ILogger _logger;
        private readonly List<Carrier> _carriers = new List<Carrier>();

        private bool _reportedDeactivationTrap;
        private bool _pending; // a spawn is in flight; don't ask for a second one in the same breath

        // The look a carried load wears, borrowed from the crate port rather than resolved again — see
        // SulfurThrownCratePort.TryGetLook. Read once the crate content is ready.
        private Mesh _look;
        private Material _lookMaterial;
        private bool _lookResolved;
        private int _nextSetDownSeed = 1;
        private float _observedWalkSpeed;

        public SulfurCarrierPort(MonoBehaviour host, IThrownCratePort crates, ILogger logger = null)
        {
            _host = host ?? throw new ArgumentNullException(nameof(host));
            _crates = crates ?? throw new ArgumentNullException(nameof(crates));
            _logger = logger;
        }

        public int Working
        {
            get
            {
                Forget();
                return _carriers.Count;
            }
        }

        public float ObservedWalkSpeed => _observedWalkSpeed;

        public int Carried
        {
            get
            {
                Forget();
                var total = 0;
                for (var i = 0; i < _carriers.Count; i++)
                {
                    total += _carriers[i].Load;
                }

                return total;
            }
        }

        public void Advance(
            float deltaSeconds,
            int wanted,
            int loadPerCarrier,
            IReadOnlyList<ArenaWorldPoint> sources,
            ArenaWorldPoint deliverTo,
            CratePileId deliverPile)
        {
            Forget();

            if (sources == null || sources.Count == 0)
            {
                return; // a room with nowhere to fetch from has no supply line
            }

            ResolveTheLoadsLook();

            PutOnTheRoute(wanted, sources);
            TakeOffTheRoute(wanted);

            for (var i = 0; i < _carriers.Count; i++)
            {
                Advance(_carriers[i], deltaSeconds, loadPerCarrier, sources, deliverTo, deliverPile);
            }
        }

        public void DismissAll()
        {
            var dismissed = 0;
            for (var i = 0; i < _carriers.Count; i++)
            {
                if (Remove(_carriers[i], spillLoad: false))
                {
                    dismissed++;
                }
            }

            _carriers.Clear();
            if (dismissed > 0)
            {
                _logger?.Log($"[carrier] {dismissed} carrier(s) taken off the route with the encounter.");
            }
        }

        /// <summary>
        /// Take the crate mesh and material off the crate port once it has them, so a carried load looks like the
        /// thing it will become. Tried once: the content is prepared on the first crate and does not change, and a
        /// carrier whose load has no look still carries it — the stack simply falls back to a plain box.
        /// </summary>
        private void ResolveTheLoadsLook()
        {
            if (_lookResolved)
            {
                return;
            }

            var port = _crates as SulfurThrownCratePort;
            if (port == null || !port.TryGetLook(out var mesh, out var material))
            {
                return; // not ready yet, or a port that has no look to lend; try again next frame
            }

            _look = mesh;
            _lookMaterial = material;
            _lookResolved = true;
        }

        // ------------------------------------------------------------------ the route

        /// <summary>One leg at a time: fetch from a production point, load, walk it to the boss, set it down.</summary>
        private void Advance(
            Carrier carrier,
            float deltaSeconds,
            int loadPerCarrier,
            IReadOnlyList<ArenaWorldPoint> sources,
            ArenaWorldPoint deliverTo,
            CratePileId deliverPile)
        {
            carrier.OnLeg += deltaSeconds;
            carrier.DrawLoad(_look, _lookMaterial);

            switch (carrier.Leg)
            {
                case Leg.ToSource:
                {
                    if (!carrier.Aimed)
                    {
                        // A carrier that has just arrived in the world has not been told where to fetch from yet.
                        AimAtSomethingToFetch(carrier, sources);
                        Begin(carrier, Leg.ToSource, carrier.Fetch);
                        carrier.Aimed = true;
                        return;
                    }

                    if (!HasArrived(carrier, carrier.Fetch))
                    {
                        RepeatIfStuck(carrier, carrier.Fetch);
                        return;
                    }

                    carrier.Handling += deltaSeconds;
                    if (carrier.Handling < HandlingSeconds)
                    {
                        return;
                    }

                    // Take what is standing there, up to a full load. An empty point is not an error: the carrier
                    // waits for the room to produce, which is exactly the pressure a player creates by breaking
                    // the near end of the line.
                    var room = loadPerCarrier - carrier.Load;
                    if (room > 0)
                    {
                        var here = carrier.Unit != null ? carrier.Unit.transform.position : carrier.LastPosition;
                        carrier.Load += _crates.TakeFrom(
                            carrier.FetchPile,
                            room,
                            new ArenaWorldPoint(here.x, here.y, here.z),
                            PickUpReach);
                    }

                    if (carrier.Load <= 0)
                    {
                        // Nothing here after all — spilled cargo somebody else already collected, or a point that
                        // has not produced yet. Re-decide and always begin the leg again, even when the answer is
                        // the same place: the game clears a forced destination the moment it is reached, so a
                        // carrier that is not re-sent simply stands there, and the stuck-check cannot rescue it
                        // because as far as it is concerned the carrier has arrived.
                        AimAtSomethingToFetch(carrier, sources);
                        Begin(carrier, Leg.ToSource, carrier.Fetch);
                        return;
                    }

                    // A part load waits a little to be topped up rather than walking half empty — but only at a
                    // production point, which is the only thing that produces. Waiting on a heap of spilled cargo
                    // waits for something that is never coming.
                    if (carrier.Load < loadPerCarrier
                        && carrier.FetchPile.Kind == CratePileKind.Source
                        && carrier.Waited < HandlingSeconds * 4f)
                    {
                        carrier.Waited += deltaSeconds;
                        return;
                    }

                    Begin(carrier, Leg.ToPile, deliverTo);
                    return;
                }

                case Leg.ToPile:
                {
                    if (!HasArrived(carrier, deliverTo))
                    {
                        // The boss moves, and its pile with it: a carrier already walking is re-aimed rather than
                        // delivering to where the boss used to be.
                        Retarget(carrier, deliverTo);
                        RepeatIfStuck(carrier, deliverTo);
                        return;
                    }

                    carrier.Handling += deltaSeconds;
                    if (carrier.Handling < HandlingSeconds)
                    {
                        return;
                    }

                    SetDown(carrier, deliverTo, deliverPile);

                    // Next trip: rotate to the following production point, then decide whether anything spilled
                    // is closer than it. Aiming has to happen BEFORE the leg begins, or the goblin sets off for
                    // wherever it was going last time.
                    carrier.SourceIndex++;
                    AimAtSomethingToFetch(carrier, sources);
                    Begin(carrier, Leg.ToSource, carrier.Fetch);
                    return;
                }
            }
        }

        /// <summary>
        /// Decide what this carrier walks to for its next load: its production point, or a heap of cargo somebody
        /// spilled on the way — whichever is closer.
        /// </summary>
        /// <remarks>
        /// This is what stops a long fight from silting up. Crates dropped by a carrier who died holding them
        /// belong to nobody and the boss cannot fire them, so without this they would lie there for the rest of
        /// the fight; with it, the village comes and collects, and killing a loaded carrier costs the boss the
        /// walk rather than the cargo. A player who keeps killing carriers still wins the exchange — the goblins
        /// spend their trips reclaiming instead of fetching.
        /// </remarks>
        private void AimAtSomethingToFetch(Carrier carrier, IReadOnlyList<ArenaWorldPoint> sources)
        {
            var source = sources[carrier.SourceIndex % sources.Count];
            carrier.Fetch = source;
            carrier.FetchPile = CratePileId.Source(carrier.SourceIndex % sources.Count);

            var here = carrier.Unit != null ? carrier.Unit.transform.position : Vector3.zero;
            var from = new ArenaWorldPoint(here.x, here.y, here.z);
            if (!_crates.TryFindNearestResting(CratePileId.Loose, from, out var spilled))
            {
                return;
            }

            if (Flat(spilled, from) < Flat(source, from))
            {
                carrier.Fetch = spilled;
                carrier.FetchPile = CratePileId.Loose;
            }
        }

        /// <summary>
        /// Throw a carrier's whole load off its shoulders onto the ground around <paramref name="at"/>, each crate
        /// arcing out of its hands to its own patch of floor. Returns how many made it out.
        /// </summary>
        /// <remarks>
        /// <para><b>Why an arc and not an appearance.</b> Crates that simply materialised beside a goblin read as
        /// a bookkeeping event; thrown from its hands and tumbling out, the same crates read as it putting its
        /// load down — and, when it dies, as the load bursting out of it.</para>
        /// <para><b>Why a ring and not a column.</b> Crates are solid bodies with real mass. A load released down
        /// one column spawns them inside each other, and physics resolves that by firing them across the room.
        /// The ring gives each its own patch, and reuses the volley's own scatter rather than a second one.</para>
        /// </remarks>
        private int TossLoadAround(Carrier carrier, ArenaWorldPoint at, CratePileId pile)
        {
            var load = carrier.Load;
            if (load <= 0)
            {
                return 0;
            }

            // Out of its hands, not out of the floor: the load leaves from where it was being carried.
            var head = carrier.Unit != null ? carrier.Unit.transform.position : carrier.LastPosition;
            var from = new ArenaWorldPoint(head.x, head.y + StackBase, head.z);

            var seed = _nextSetDownSeed++;
            var placed = 0;
            for (var i = 0; i < load; i++)
            {
                var ring = ShotgunSpread.Offset(seed, i, load, SetDownMinRadius, SetDownMaxRadius);
                var to = new ArenaWorldPoint(at.X + ring.X, at.Y + SetDownHeight, at.Z + ring.Z);
                if (_crates.Toss(from, to, pile, SetDownFlightSeconds, SetDownApexHeight))
                {
                    placed++;
                }
            }

            return placed;
        }

        /// <summary>Ground-plane distance, squared — heights differ across the room's terraces and would otherwise
        /// make a pile one floor up look further away than it is to walk to.</summary>
        private static float Flat(ArenaWorldPoint a, ArenaWorldPoint b)
        {
            var dx = a.X - b.X;
            var dz = a.Z - b.Z;
            return (dx * dx) + (dz * dz);
        }

        /// <summary>Put the load on the ground as real destructibles — this is the moment a carried stack becomes
        /// the boss's ammunition.</summary>
        private void SetDown(Carrier carrier, ArenaWorldPoint at, CratePileId pile)
        {
            var placed = TossLoadAround(carrier, at, pile);

            _logger?.Log($"[carrier] delivered {placed} to {pile}; {_crates.RestingOn(pile)} now on that pile.");
            carrier.Load = 0;
            carrier.ClearDrawnLoad();
        }

        /// <summary>Spill a dead carrier's load where it fell: real crates, on nobody's pile, so the boss cannot
        /// fire them and a player can. This is what killing a loaded carrier is worth.</summary>
        private void Spill(Carrier carrier)
        {
            if (carrier.Load <= 0 || carrier.LastPosition == Vector3.zero)
            {
                return;
            }

            var where = new ArenaWorldPoint(
                carrier.LastPosition.x, carrier.LastPosition.y, carrier.LastPosition.z);
            var load = carrier.Load;
            var spilled = TossLoadAround(carrier, where, CratePileId.Loose);

            _logger?.Log($"[carrier] a carrier died holding {load}; {spilled} spilled where it fell — "
                + "the boss cannot fire those.");
            carrier.Load = 0;
        }

        // ------------------------------------------------------------------ the roster

        private void PutOnTheRoute(int wanted, IReadOnlyList<ArenaWorldPoint> sources)
        {
            if (_carriers.Count >= wanted || _pending)
            {
                return;
            }

            // One at a time, so a raised headcount trickles in as reinforcements rather than materialising as a
            // crowd, and so a failed spawn does not repeat itself a dozen times in one frame.
            var index = _carriers.Count;
            var source = sources[index % sources.Count];
            _pending = true;
            SpawnOne(new Vector3(source.X, source.Y, source.Z), index);
        }

        private void TakeOffTheRoute(int wanted)
        {
            for (var i = _carriers.Count - 1; i >= wanted && i >= 0; i--)
            {
                // Anything it was holding goes back on the floor rather than evaporating.
                Remove(_carriers[i], spillLoad: true);
                _carriers.RemoveAt(i);
            }
        }

        private async void SpawnOne(Vector3 at, int sourceIndex)
        {
            Unit unit = null;
            try
            {
                var definition = CarrierUnit.GetAsset();
                if (definition == null)
                {
                    _logger?.LogWarning($"[carrier] the game has no definition for {CarrierUnit.value}; the boss "
                        + "will have to do without carriers.");
                    return;
                }

                unit = await definition.SpawnUnitAsync(_host, at);
            }
            catch (Exception exception)
            {
                _logger?.LogWarning($"[carrier] a carrier failed to spawn: {exception.Message}");
            }
            finally
            {
                _pending = false;
            }

            if (unit == null)
            {
                return;
            }

            var carrier = new Carrier(unit, sourceIndex);
            ReportTheDeactivationTrapOnce(unit);
            _carriers.Add(carrier);
        }

        /// <summary>Drop the entries whose goblins have gone, spilling whatever they were holding.</summary>
        private void Forget()
        {
            for (var i = _carriers.Count - 1; i >= 0; i--)
            {
                var carrier = _carriers[i];
                if (carrier.Unit != null && carrier.Unit.IsAlive)
                {
                    carrier.LastPosition = carrier.Unit.transform.position;
                    continue;
                }

                Spill(carrier);
                carrier.ClearDrawnLoad();
                _carriers.RemoveAt(i);
            }
        }

        private bool Remove(Carrier carrier, bool spillLoad)
        {
            if (spillLoad)
            {
                Spill(carrier);
            }

            carrier.ClearDrawnLoad();
            if (carrier.Unit == null)
            {
                return false;
            }

            try
            {
                UnityEngine.Object.Destroy(carrier.Unit.gameObject);
                return true;
            }
            catch (Exception exception)
            {
                _logger?.LogWarning($"[carrier] a carrier could not be removed ({exception.Message}); leaving it.");
                return false;
            }
        }

        // ------------------------------------------------------------------ driving one goblin

        /// <summary>Start a leg: point the goblin at the place and remember what it is doing.</summary>
        private void Begin(Carrier carrier, Leg leg, ArenaWorldPoint destination)
        {
            carrier.Leg = leg;
            carrier.OnLeg = 0f;
            carrier.Handling = 0f;
            carrier.Waited = 0f;
            SendTo(carrier, destination);
        }

        /// <summary>
        /// The game's own way of walking a unit somewhere on an errand: set the forced destination, then activate
        /// the behaviour — which, seeing a forced destination, switches the behaviour tree off and hands the point
        /// to the agent. Nothing else is set; the aggro flags the vanilla summon sites use are a trap that nails
        /// every unit to the local player.
        /// </summary>
        private void SendTo(Carrier carrier, ArenaWorldPoint destination)
        {
            var npc = carrier.Npc;
            if (npc == null)
            {
                return;
            }

            try
            {
                var point = new Vector3(destination.X, destination.Y, destination.Z);
                carrier.Destination = point;
                npc.SetForcedDestination(point);
                npc.ActivateBehaviour();
            }
            catch (Exception exception)
            {
                _logger?.LogWarning($"[carrier] a carrier could not be sent on its way ({exception.Message}).");
            }
        }

        /// <summary>Re-aim a carrier already walking, when the place it is walking to has moved far enough to
        /// matter — the boss relocating takes its pile with it.</summary>
        private void Retarget(Carrier carrier, ArenaWorldPoint destination)
        {
            var point = new Vector3(destination.X, destination.Y, destination.Z);
            if ((carrier.Destination - point).sqrMagnitude < 1f)
            {
                return;
            }

            SendTo(carrier, destination);
        }

        /// <summary>A carrier that has been on one leg too long is assumed stuck and given the destination again;
        /// the forced destination is cleared by the game once reached, so re-issuing is the recovery.</summary>
        private void RepeatIfStuck(Carrier carrier, ArenaWorldPoint destination)
        {
            if (carrier.OnLeg < LegTimeoutSeconds)
            {
                return;
            }

            carrier.OnLeg = 0f;
            _logger?.LogWarning("[carrier] a carrier has been on one leg too long; sending it again.");
            SendTo(carrier, destination);
        }

        private static bool HasArrived(Carrier carrier, ArenaWorldPoint destination)
        {
            var unit = carrier.Unit;
            if (unit == null)
            {
                return false;
            }

            var npc = carrier.Npc;
            var agent = npc != null ? npc.AiAgent : null;
            if (agent != null && agent.onDestination)
            {
                return true;
            }

            // Fallback: a goblin that stopped just short of the point still counts, so one awkward spot cannot
            // stall the whole supply line.
            var here = unit.transform.position;
            var there = new Vector3(destination.X, here.y, destination.Z);
            return (here - there).sqrMagnitude <= ArrivalRadius * ArrivalRadius;
        }

        /// <summary>
        /// Report, once, whether this unit can actually have its behaviour tree switched off. The game's own
        /// guard refuses to deactivate a tree on a unit whose <c>canBeDeactivated</c> is false, which would leave
        /// the carrier's own AI fighting the destination — worth knowing as a fact in the log rather than as a
        /// puzzle about why goblins wander.
        /// </summary>
        private void ReportTheDeactivationTrapOnce(Unit unit)
        {
            if (_reportedDeactivationTrap)
            {
                return;
            }

            _reportedDeactivationTrap = true;
            try
            {
                var so = unit.unitSO;
                if (so == null)
                {
                    return;
                }

                var speed = unit is Npc npc && npc.AiAgent != null && npc.AiAgent.navMeshAgent != null
                    ? npc.AiAgent.navMeshAgent.maxSpeed
                    : 0f;
                _observedWalkSpeed = speed;

                _logger?.Log($"[carrier] {CarrierUnit.value}: canBeDeactivated={so.canBeDeactivated}, "
                    + $"canPanic={so.canPanic}, isCivilian={so.isCivilian}, walk speed={speed:0.00} m/s.");

                if (!so.canBeDeactivated)
                {
                    _logger?.LogWarning("[carrier] this unit's behaviour tree cannot be switched off, so its own "
                        + "AI will fight the errand; the walk needs re-issuing every tick instead.");
                }
            }
            catch (Exception exception)
            {
                _logger?.LogWarning($"[carrier] the carrier unit could not be described: {exception.Message}");
            }
        }

        // ------------------------------------------------------------------ the load's look

        private enum Leg
        {
            ToSource = 0,
            ToPile = 1,
        }

        /// <summary>One goblin on the route, what it is doing, and what it is holding.</summary>
        private sealed class Carrier
        {
            private readonly List<GameObject> _drawn = new List<GameObject>();
            private Mesh _look;
            private Material _lookMaterial;

            public Carrier(Unit unit, int sourceIndex)
            {
                Unit = unit;
                Npc = unit as Npc;
                SourceIndex = sourceIndex;
                Leg = Leg.ToSource;
                LastPosition = unit != null ? unit.transform.position : Vector3.zero;
            }

            public Unit Unit { get; }

            public Npc Npc { get; }

            public Leg Leg { get; set; }

            /// <summary>Which production point this carrier is working; advanced after each delivery so a roster
            /// spreads itself over the room's points instead of queueing at one.</summary>
            public int SourceIndex { get; set; }

            public int Load { get; set; }

            /// <summary>Where this carrier is going for its next load, and which heap it will take it from — a
            /// production point, or spilled cargo it passed closer to.</summary>
            public ArenaWorldPoint Fetch { get; set; }

            public CratePileId FetchPile { get; set; }

            /// <summary>Whether this carrier has been told where to fetch from at all. False for the frame after
            /// it arrives in the world, when it has a leg but no destination yet.</summary>
            public bool Aimed { get; set; }

            public float OnLeg { get; set; }

            public float Handling { get; set; }

            public float Waited { get; set; }

            public Vector3 Destination { get; set; }

            /// <summary>Where it was last seen alive — where a spilled load lands.</summary>
            public Vector3 LastPosition { get; set; }

            /// <summary>Draw the load as a stack riding on the goblin, wearing the same mesh and material the real
            /// destructibles do. Presentation only: these have no physics, no health and no hit detection, which
            /// is the whole reason a carrier can haul a dozen without the level filling with bodies.</summary>
            public void DrawLoad(Mesh look, Material lookMaterial)
            {
                if (Unit == null)
                {
                    return;
                }

                _look = look;
                _lookMaterial = lookMaterial;

                var drawn = Math.Min(Load, MaxDrawnStack);
                while (_drawn.Count > drawn)
                {
                    var last = _drawn[_drawn.Count - 1];
                    _drawn.RemoveAt(_drawn.Count - 1);
                    if (last != null)
                    {
                        UnityEngine.Object.Destroy(last);
                    }
                }

                while (_drawn.Count < drawn)
                {
                    _drawn.Add(MakeOne(Unit.transform, _drawn.Count, _look, _lookMaterial));
                }
            }

            /// <summary>One crate of the stack: a bare mesh renderer parented to the goblin, no collider and no
            /// body, so it rides along without touching physics. Falls back to a plain cube when the crate
            /// content is not ready, so a carrier is never invisibly empty-handed.</summary>
            private static GameObject MakeOne(Transform on, int place, Mesh look, Material lookMaterial)
            {
                GameObject box;
                if (look != null && lookMaterial != null)
                {
                    box = new GameObject("FalseGods_CarriedCrate");
                    box.AddComponent<MeshFilter>().sharedMesh = look;
                    box.AddComponent<MeshRenderer>().sharedMaterial = lookMaterial;
                }
                else
                {
                    box = GameObject.CreatePrimitive(PrimitiveType.Cube);
                    box.name = "FalseGods_CarriedCrate";
                    var collider = box.GetComponent<Collider>();
                    if (collider != null)
                    {
                        // Scenery: it must never shove the goblin carrying it, or the player walking past.
                        UnityEngine.Object.Destroy(collider);
                    }
                }

                box.transform.SetParent(on, false);
                box.transform.localScale = Vector3.one * 0.5f;
                box.transform.localPosition = new Vector3(0f, StackBase + place * StackSpacing, 0f);
                return box;
            }

            public void ClearDrawnLoad()
            {
                for (var i = 0; i < _drawn.Count; i++)
                {
                    if (_drawn[i] != null)
                    {
                        UnityEngine.Object.Destroy(_drawn[i]);
                    }
                }

                _drawn.Clear();
            }
        }
    }
}
