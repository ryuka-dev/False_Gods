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

        /// <summary>
        /// How long a carrier may stand perfectly still, on an errand, before it is assumed to have lost its way
        /// and is put back on the route.
        /// </summary>
        /// <remarks>
        /// <b>A backstop, not a mechanism.</b> A carrier that has stopped moving is not doing anything the fight
        /// wants — it is neither fetching nor delivering nor being killed — and a route quietly losing goblins to
        /// it starves the boss for reasons nobody can see. Long enough that the pauses the errand really has (the
        /// handling beat at each end, a moment squeezing past another goblin) never trip it.
        /// </remarks>
        private const float StillTooLongSeconds = 5f;

        /// <summary>How far a carrier has to move to count as moving at all, per frame of standing still. Loose
        /// enough that the small shuffling an agent does while it settles is not movement.</summary>
        private const float StillEnough = 0.05f;

        /// <summary>The tallest a stack is drawn. A carrier hauling a dozen crates would otherwise wear a mast;
        /// beyond this the load is still carried, just not all of it drawn.</summary>
        private const int MaxDrawnStack = 5;

        private readonly MonoBehaviour _host;
        private readonly IThrownCratePort _crates;
        private readonly ILogger _logger;
        private readonly Action<ArenaWorldPoint, CratePileId, int, float> _announceTaken;
        private readonly Action<ArenaWorldPoint, ArenaWorldPoint, CratePileId, int, int, int> _announceSetDown;
        private readonly List<Carrier> _carriers = new List<Carrier>();

        private bool _reportedDeactivationTrap;
        private bool _pending; // a spawn is in flight; don't ask for a second one in the same breath

        // What each carrier picked up, refilled on every collection and read straight into its drawn stack. One
        // list, reused, because a pick-up happens on one carrier at a time.
        private readonly List<SulfurThrownCratePort.CrateLook> _justPickedUp =
            new List<SulfurThrownCratePort.CrateLook>();

        // Where the others are already headed, rebuilt whenever one is given an errand so nobody is sent after a
        // heap somebody else will have carried off by the time they arrive.
        private readonly List<ArenaWorldPoint> _claimedHeaps = new List<ArenaWorldPoint>();

        // The route's own clock and the gaps it owes: one entry per carrier killed, holding its place empty until
        // that time. Wound by the caller's frame, so it measures the fight's time rather than the wall's.
        private float _routeClock;
        private float _replaceAfterSeconds;
        private readonly List<float> _mourning = new List<float>();
        private int _nextSetDownSeed = 1;
        private float _observedWalkSpeed;

        /// <param name="announceTaken">Told whenever a carrier picks a load up: where it stood, which heap, how
        /// many, and how far it could reach. A peer with no carriers of its own has no other way to know its piles
        /// have shrunk.</param>
        /// <param name="announceSetDown">Told whenever a load is put down or spilled, with the seed the ring was
        /// laid out from — which is the whole delivery, since every peer builds the same ring from it.</param>
        public SulfurCarrierPort(
            MonoBehaviour host,
            IThrownCratePort crates,
            ILogger logger = null,
            Action<ArenaWorldPoint, CratePileId, int, float> announceTaken = null,
            Action<ArenaWorldPoint, ArenaWorldPoint, CratePileId, int, int, int> announceSetDown = null)
        {
            _host = host ?? throw new ArgumentNullException(nameof(host));
            _crates = crates ?? throw new ArgumentNullException(nameof(crates));
            _logger = logger;
            _announceTaken = announceTaken;
            _announceSetDown = announceSetDown;
        }

        public void Warm()
        {
            try
            {
                // Asking the game for the definition is what pulls the creature out of the player's install; the
                // result is not needed here, only the fact that it has happened.
                CarrierUnit.GetAsset();
            }
            catch (Exception exception)
            {
                _logger?.LogWarning($"[carrier] the village could not be fetched ahead of time "
                    + $"({exception.Message}); the first carrier will fetch it instead.");
            }
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
            float replaceAfterSeconds,
            IReadOnlyList<ArenaWorldPoint> sources,
            ArenaWorldPoint deliverTo,
            CratePileId deliverPile)
        {
            // The route's own clock, wound by the caller's frame rather than read off the world, so a paused or
            // slowed game holds the gap open for as long as it would have taken.
            _routeClock += deltaSeconds;
            _replaceAfterSeconds = replaceAfterSeconds;

            Forget();

            if (sources == null || sources.Count == 0)
            {
                return; // a room with nowhere to fetch from has no supply line
            }

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
            _mourning.Clear();
            if (dismissed > 0)
            {
                _logger?.Log($"[carrier] {dismissed} carrier(s) taken off the route with the encounter.");
            }
        }

        public void Disband()
        {
            var released = 0;
            var dropped = 0;
            for (var i = 0; i < _carriers.Count; i++)
            {
                var carrier = _carriers[i];
                dropped += DropWhatItIsHolding(carrier);
                carrier.ClearDrawnLoad();
                if (SendBackToItsOwnLife(carrier))
                {
                    released++;
                }
            }

            _carriers.Clear();
            _mourning.Clear();
            if (released > 0 || dropped > 0)
            {
                _logger?.Log($"[carrier] the errand is over: {released} villager(s) went back to their own lives, "
                    + $"{dropped} crate(s) put down where they stood.");
            }
        }

        /// <summary>
        /// Hand a carrier back its own behaviour.
        /// </summary>
        /// <remarks>
        /// <b>Both steps, in this order.</b> A forced destination is what switches a creature's behaviour tree off,
        /// and the game refuses to switch the tree back on while one is set — so clearing it is not tidying up
        /// afterwards, it is the thing that makes the second call work at all.
        /// </remarks>
        private bool SendBackToItsOwnLife(Carrier carrier)
        {
            var npc = carrier.Npc;
            if (npc == null || carrier.Unit == null)
            {
                return false;
            }

            try
            {
                npc.SetForcedDestination(Vector3.zero);
                npc.ActivateBehaviourTree();
                return true;
            }
            catch (Exception exception)
            {
                _logger?.LogWarning($"[carrier] a villager could not be given its own behaviour back "
                    + $"({exception.Message}); it will stand where it is.");
                return false;
            }
        }

        /// <summary>Put down what a carrier is holding, where it stands: real crates on nobody's pile, so what was
        /// in transit when the fight ended is still there to be shot.</summary>
        private int DropWhatItIsHolding(Carrier carrier)
        {
            if (carrier.Load <= 0 || carrier.LastPosition == Vector3.zero)
            {
                return 0;
            }

            var where = new ArenaWorldPoint(
                carrier.LastPosition.x, carrier.LastPosition.y, carrier.LastPosition.z);
            var dropped = TossLoadAround(carrier, where, CratePileId.Loose);
            carrier.Load = 0;
            return dropped;
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
            carrier.DrawLoad();

            if (HasSeizedUp(carrier, deltaSeconds))
            {
                PutBackOnTheRoute(carrier, sources);
                return;
            }

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
                        var at = new ArenaWorldPoint(here.x, here.y, here.z);
                        _justPickedUp.Clear();
                        var port = _crates as SulfurThrownCratePort;
                        var took = port != null
                            ? port.TakeFrom(carrier.FetchPile, room, at, PickUpReach, _justPickedUp)
                            : _crates.TakeFrom(carrier.FetchPile, room, at, PickUpReach);
                        if (took > 0)
                        {
                            carrier.Load += took;
                            carrier.Shoulder(_justPickedUp, took);

                            // A client has no carriers of its own, so its piles only shrink when told.
                            _announceTaken?.Invoke(at, carrier.FetchPile, took, PickUpReach);
                        }
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

            // Cargo lying about is always collected first, however far away it is and from the carrier's very
            // first errand. It used to be taken only when it happened to be nearer than the production point,
            // which meant a floor strewn with spilled loads stayed strewn while the village walked past it to
            // make more — the room filling up with cargo nobody would ever come back for. A production point
            // will still be there in a minute; what is on the floor is what the fight actually left behind.
            var here = carrier.Unit != null ? carrier.Unit.transform.position : Vector3.zero;
            var from = new ArenaWorldPoint(here.x, here.y, here.z);
            if (_crates.TryFindNearestResting(
                CratePileId.Loose, from, out var spilled, HeapsBeingCollected(carrier), PickUpReach))
            {
                carrier.Fetch = spilled;
                carrier.FetchPile = CratePileId.Loose;
            }
        }

        /// <summary>
        /// The spilled heaps other carriers are already walking to.
        /// </summary>
        /// <remarks>
        /// <b>Because three of them all went to the same one.</b> Each aimed at the nearest heap, which was the
        /// same heap, and the first to arrive collected all of it — leaving the other two to walk the rest of the
        /// way, find bare floor, and only then think again. A carrier already on its way to a heap is as good as
        /// having collected it, so the next one to be sent looks past it. The gap is the collector's own reach,
        /// which is what makes one heap one errand.
        /// </remarks>
        private IReadOnlyList<ArenaWorldPoint> HeapsBeingCollected(Carrier except)
        {
            _claimedHeaps.Clear();
            for (var i = 0; i < _carriers.Count; i++)
            {
                var other = _carriers[i];
                if (!ReferenceEquals(other, except)
                    && other.Aimed
                    && other.Leg == Leg.ToSource
                    && other.FetchPile.Kind == CratePileKind.Loose)
                {
                    _claimedHeaps.Add(other.Fetch);
                }
            }

            return _claimedHeaps;
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

            // What comes out is what it was carrying. Anything else and a carrier a player watched walking with a
            // barrel on its back could burst into cargo with no barrel in it.
            var explosives = carrier.ExplosivesHeld;
            var placed = _crates.TossRing(from, at, pile, load, seed, explosives);

            // Every peer lays the same load out from these few numbers; no crate position is ever sent.
            _announceSetDown?.Invoke(from, at, pile, load, seed, explosives);
            return placed;
        }

        /// <summary>
        /// Whether this carrier has stood perfectly still long enough to count as lost.
        /// </summary>
        /// <remarks>
        /// Measured off the goblin itself rather than off its agent's opinion of what it is doing: the failures
        /// this is here for are exactly the ones where the agent believes it is walking.
        /// </remarks>
        private static bool HasSeizedUp(Carrier carrier, float deltaSeconds)
        {
            if (carrier.Unit == null)
            {
                return false;
            }

            var here = carrier.Unit.transform.position;
            if ((here - carrier.StillSince).sqrMagnitude > StillEnough * StillEnough)
            {
                carrier.StillSince = here;
                carrier.Still = 0f;
                return false;
            }

            carrier.Still += deltaSeconds;
            return carrier.Still >= StillTooLongSeconds;
        }

        /// <summary>
        /// Put a carrier that stopped working back at a production point and start its errand again.
        /// </summary>
        /// <remarks>
        /// <para><b>Moved with the game's own teleport</b> — the one the vanilla cave boss uses to change pools —
        /// rather than by writing a transform, so the agent is told rather than discovering it has moved. The
        /// destination is a production point the room authored: it is on the navigation mesh by construction, it
        /// is where crates already appear, and it is the start of the errand anyway, so there is nothing there to
        /// arrive inside of.</para>
        /// <para>Its load comes with it. A carrier that was holding a delivery when it seized up did nothing
        /// wrong, and dropping the load here would be a second invisible failure on top of the first.</para>
        /// </remarks>
        private void PutBackOnTheRoute(Carrier carrier, IReadOnlyList<ArenaWorldPoint> sources)
        {
            carrier.Still = 0f;
            if (carrier.Unit == null || sources == null || sources.Count == 0)
            {
                return;
            }

            var to = sources[carrier.SourceIndex >= 0 && carrier.SourceIndex < sources.Count
                ? carrier.SourceIndex
                : 0];

            try
            {
                carrier.Unit.TeleportTo(new Vector3(to.X, to.Y, to.Z));
                carrier.StillSince = carrier.Unit.transform.position;
                carrier.LastPosition = carrier.Unit.transform.position;
            }
            catch (Exception exception)
            {
                _logger?.LogWarning($"[carrier] a stuck carrier could not be moved ({exception.Message}); it will "
                    + "be tried again.");
                return;
            }

            carrier.Aimed = false;
            Begin(carrier, Leg.ToSource, to);
            _logger?.Log($"[carrier] one stood still for {StillTooLongSeconds:0.#}s and was put back at its "
                + $"production point, still holding {carrier.Load}.");
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
            // A carrier killed leaves its place empty for a while. Without that the headcount is restored the same
            // frame it drops and the whole route is decoration: the player can stand in it, but standing in it
            // costs the boss nothing.
            for (var i = _mourning.Count - 1; i >= 0; i--)
            {
                if (_mourning[i] <= _routeClock)
                {
                    _mourning.RemoveAt(i);
                }
            }

            var allowed = wanted - _mourning.Count;
            if (_carriers.Count >= allowed || _pending)
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

                // Died on the route rather than being taken off it, so the gap is owed time before it is filled.
                if (_replaceAfterSeconds > 0f)
                {
                    _mourning.Add(_routeClock + _replaceAfterSeconds);
                    _logger?.Log($"[carrier] one is down; the route stays {_replaceAfterSeconds:0.#}s short "
                        + $"({_carriers.Count} still working).");
                }
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
            /// <summary>What is actually on this carrier's back, in pick-up order.</summary>
            private readonly List<SulfurThrownCratePort.CrateLook> _carried =
                new List<SulfurThrownCratePort.CrateLook>();

            /// <summary>How many of what it is holding go off. What the stack has to be honest about, and what
            /// comes back out of it when the load is put down or the carrier is killed holding it.</summary>
            public int ExplosivesHeld
            {
                get
                {
                    var count = 0;
                    for (var i = 0; i < _carried.Count; i++)
                    {
                        if (_carried[i].Explosive)
                        {
                            count++;
                        }
                    }

                    return count;
                }
            }

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

            /// <summary>Where this carrier was when it last actually moved, and how long ago that was. The
            /// watchdog's whole state.</summary>
            public Vector3 StillSince { get; set; }

            public float Still { get; set; }

            /// <summary>Draw the load as a stack riding on the goblin, wearing the same mesh and material the real
            /// destructibles do. Presentation only: these have no physics, no health and no hit detection, which
            /// is the whole reason a carrier can haul a dozen without the level filling with bodies.</summary>
            /// <summary>Take a collection onto the carrier's back: what it is now holding, in the order it picked
            /// them up, so the stack shows the load rather than a stand-in for it.</summary>
            public void Shoulder(List<SulfurThrownCratePort.CrateLook> picked, int took)
            {
                for (var i = 0; i < picked.Count && i < took; i++)
                {
                    _carried.Add(picked[i]);
                }

                // A collection this peer could not itemise (it was told the count, not the contents) still has to
                // show something, so the stack is padded with whatever the last known item was.
                while (_carried.Count < Load)
                {
                    _carried.Add(_carried.Count > 0 ? _carried[_carried.Count - 1] : default(SulfurThrownCratePort.CrateLook));
                }

                Redraw();
            }

            public void DrawLoad()
            {
                if (Unit == null)
                {
                    return;
                }

                Redraw();
            }

            private void Redraw()
            {
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
                    _drawn.Add(MakeOne(Unit.transform, _drawn.Count, LookAt(_drawn.Count, drawn)));
                }
            }

            /// <summary>
            /// The look of the nth thing drawn on this carrier's back.
            /// </summary>
            /// <remarks>
            /// <b>The cap must not hide a barrel.</b> Only the first few of a load are drawn, so that a carrier
            /// hauling a dozen does not wear a mast — but a player deciding whether to shoot this goblin is
            /// reading that stack, and a barrel that was carried but not drawn makes the stack a lie. So when the
            /// load holds one and none of the drawn places would have shown it, the last drawn place becomes a
            /// barrel. What is hidden is then only ever ordinary cargo, which is the thing the cap was for.
            /// </remarks>
            private SulfurThrownCratePort.CrateLook LookAt(int place, int drawn)
            {
                if (place >= _carried.Count)
                {
                    return default(SulfurThrownCratePort.CrateLook);
                }

                if (place == drawn - 1 && _carried.Count > drawn && ExplosivesHeld > 0 && !AnyExplosiveWithin(drawn - 1))
                {
                    return FirstExplosive();
                }

                return _carried[place];
            }

            private bool AnyExplosiveWithin(int places)
            {
                for (var i = 0; i < places && i < _carried.Count; i++)
                {
                    if (_carried[i].Explosive)
                    {
                        return true;
                    }
                }

                return false;
            }

            private SulfurThrownCratePort.CrateLook FirstExplosive()
            {
                for (var i = 0; i < _carried.Count; i++)
                {
                    if (_carried[i].Explosive)
                    {
                        return _carried[i];
                    }
                }

                return default(SulfurThrownCratePort.CrateLook);
            }

            /// <summary>One crate of the stack: a bare mesh renderer parented to the goblin, no collider and no
            /// body, so it rides along without touching physics. Falls back to a plain cube when the crate
            /// content is not ready, so a carrier is never invisibly empty-handed.</summary>
            private static GameObject MakeOne(Transform on, int place, SulfurThrownCratePort.CrateLook look)
            {
                GameObject box;
                if (look.Known)
                {
                    box = new GameObject("FalseGods_CarriedCrate");
                    box.AddComponent<MeshFilter>().sharedMesh = look.Mesh;
                    box.AddComponent<MeshRenderer>().sharedMaterial = look.Material;
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
                // What it was holding goes with what was drawn of it: a carrier that has set its load down starts
                // its next collection empty, or the stack would keep growing out of the last trip's contents.
                _carried.Clear();

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
