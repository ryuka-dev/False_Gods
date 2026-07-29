using System;
using System.Collections.Generic;
using FalseGods.Application.Replication;
using FalseGods.Core.Bosses.Combat;
using FalseGods.Protocol.Wire;
using FalseGods.RuntimeContracts.Arena;
using FalseGods.RuntimeContracts.Multiplayer;
using FalseGods.RuntimeContracts.Transport;

namespace FalseGods.Application.Combat
{
    /// <summary>
    /// Carries the boss's destructible commands to every peer: the host says what it did, and each peer builds its
    /// own crates from the same inputs.
    /// </summary>
    /// <remarks>
    /// <para><b>Why the command and not the crates.</b> These destructibles are assembled at runtime out of the
    /// player's own installed content, so there is no shipped prefab for the session layer to spawn on a client
    /// and nothing for it to match against — the route the summoned minions take is closed here. What every peer
    /// does have is the same assembly recipe and the same mechanic, and that mechanic is a pure function of its
    /// inputs: a volley's scatter, its hold and which crates lead the target all come from a seed. So the inputs
    /// travel, each peer computes the same volley, and no crate position is ever sent.</para>
    /// <para><b>Host-authoritative.</b> Only the host broadcasts; a client applies. A client that also produced
    /// its own would double every crate, and the two peers would be dodging different ones.</para>
    /// <para><b>Untrusted input</b> (Docs/DependencyRules.md §12): a command is accepted only from the session
    /// host, and a volley whose count or timings are not sane is dropped rather than turned into a thousand
    /// crates. Traffic that does not decode, and every other message kind, is ignored — the encounter flows own
    /// those. Callbacks fire on the channel's delivery thread (the game's main thread).</para>
    /// </remarks>
    public sealed class CrateCommandFlow : IDisposable
    {
        /// <summary>A ceiling on a single volley, so a malformed or forged command cannot fill the level with
        /// destructibles. Far above any volley the boss actually fires.</summary>
        public const int MaxVolleyCount = 64;

        /// <summary>A ceiling on the gap between two crates of one barrage, so a forged command cannot leave the
        /// pile hanging in the air indefinitely. Far above any rate the boss actually fires at.</summary>
        public const float MaxFireIntervalSeconds = 5f;

        /// <summary>A ceiling on one carrier's load, so a forged carry cannot conjure a thousand crates in one
        /// message. Far above any load the escalation ladder actually asks for.</summary>
        public const int MaxLoad = 64;

        /// <summary>A ceiling on how far a reported pick-up may reach, so a forged one cannot sweep the level
        /// clean from a single point.</summary>
        public const float MaxPickUpReach = 50f;

        /// <summary>A ceiling on how many players one volley may name. Far above any party the game supports.</summary>
        public const int MaxVolleyTargets = 32;

        private readonly IEncounterChannel _channel;
        private readonly IMultiplayerSession _session;

        public CrateCommandFlow(IEncounterChannel channel, IMultiplayerSession session)
        {
            _channel = channel ?? throw new ArgumentNullException(nameof(channel));
            _session = session ?? throw new ArgumentNullException(nameof(session));
            _channel.Received += OnReceived;
        }

        /// <summary>The host dropped a crate onto a pile; do the same here.</summary>
        public Action<ArenaWorldPoint, CratePileId>? OnDropped { get; set; }

        /// <summary>The host threw a crate; throw the same one here.</summary>
        public Action<ArenaWorldPoint, ArenaWorldPoint, float, float>? OnThrown { get; set; }

        /// <summary>The host fired a volley off a pile; compute the same one here from its shape.</summary>
        public Action<CratePileId, IReadOnlyList<CrateVolleyAim>, CrateVolleyShape>? OnVolleyFired { get; set; }

        /// <summary>A carrier picked a load up on the host; take the same crates off the same heap here.</summary>
        public Action<ArenaWorldPoint, CratePileId, int, float>? OnTaken { get; set; }

        /// <summary>A load was set down or spilled on the host; lay the same ring out here.</summary>
        public Action<ArenaWorldPoint, ArenaWorldPoint, CratePileId, int, int, int>? OnSetDown { get; set; }

        /// <summary>The host settled that a destructible is gone; destroy the same one here, the same way.</summary>
        public Action<int, CrateDeath>? OnDestroyed { get; set; }

        /// <summary>Host only: a client's player destroyed one of its own destructibles and is asking for it to
        /// count. The host destroys its own copy and broadcasts the result, which is what makes the loot roll on
        /// the machine the session layer expects it to.</summary>
        public Action<int, CrateDeath>? OnDestroyRequested { get; set; }

        public void Dispose() => _channel.Received -= OnReceived;

        /// <summary>Host: tell every client what was just dropped, and onto which pile. A client calling this sends
        /// nothing.</summary>
        public void BroadcastDropped(ArenaWorldPoint at, CratePileId pile)
        {
            if (IsHosting)
            {
                Broadcast(EncounterCodec.Encode(new CrateDropped(ToWire(at), (int)pile.Kind, pile.Index)));
            }
        }

        /// <summary>Host: tell every client what was just thrown.</summary>
        public void BroadcastThrown(ArenaWorldPoint from, ArenaWorldPoint to, float flightSeconds, float apexHeight)
        {
            if (IsHosting)
            {
                Broadcast(EncounterCodec.Encode(
                    new CrateThrown(ToWire(from), ToWire(to), flightSeconds, apexHeight)));
            }
        }

        /// <summary>Host: tell every client the volley's inputs, which is the whole volley.</summary>
        public void BroadcastVolley(
            CratePileId pile, IReadOnlyList<CrateVolleyAim> aims, CrateVolleyShape shape)
        {
            if (!IsHosting)
            {
                return;
            }

            var targets = new List<CrateVolleyTarget>(aims.Count);
            for (var i = 0; i < aims.Count; i++)
            {
                targets.Add(new CrateVolleyTarget(ToWire(aims[i].Current), ToWire(aims[i].Lead)));
            }

            Broadcast(EncounterCodec.Encode(new CrateVolleyFired(
                targets,
                (int)pile.Kind,
                pile.Index,
                shape.Seed,
                shape.Count,
                shape.SpreadMinRadius,
                shape.SpreadMaxRadius,
                shape.LiftHeight,
                shape.LiftSeconds,
                shape.HoldSeconds,
                shape.FlightSeconds,
                shape.ApexHeight,
                shape.LeadShare,
                shape.FireIntervalSeconds)));
        }

        /// <summary>Host: tell every client that a carrier collected a load, so their piles shrink too.</summary>
        public void BroadcastTaken(ArenaWorldPoint at, CratePileId pile, int count, float radius)
        {
            if (IsHosting)
            {
                Broadcast(EncounterCodec.Encode(
                    new CratesTaken(ToWire(at), (int)pile.Kind, pile.Index, count, radius)));
            }
        }

        /// <summary>Host: tell every client that a load was put down, and how its ring was laid out.</summary>
        public void BroadcastSetDown(
            ArenaWorldPoint from, ArenaWorldPoint at, CratePileId pile, int count, int seed, int explosives)
        {
            if (IsHosting)
            {
                Broadcast(EncounterCodec.Encode(new CratesSetDown(
                    ToWire(from), ToWire(at), (int)pile.Kind, pile.Index, count, seed, explosives)));
            }
        }

        /// <summary>
        /// A destructible died on this peer in a way the others cannot work out: settle it for everyone if this
        /// peer settles the world, otherwise ask the host to. With no session there is nobody to tell.
        /// </summary>
        public void ReportDestroyed(int crateId, CrateDeath death)
        {
            if (IsHosting)
            {
                BroadcastDestroyed(crateId, death);
            }
            else
            {
                RequestDestroy(crateId, death);
            }
        }

        /// <summary>Host: tell every client that a destructible is gone, and how it died.</summary>
        public void BroadcastDestroyed(int crateId, CrateDeath death)
        {
            if (IsHosting)
            {
                Broadcast(EncounterCodec.Encode(new CrateDestroyed(crateId, (int)death)));
            }
        }

        /// <summary>Client: ask the host to settle that this peer's player destroyed a destructible. A host or a
        /// peer with no session sends nothing — it settles its own world.</summary>
        public void RequestDestroy(int crateId, CrateDeath death)
        {
            if (!_session.IsActive || _session.Role == SessionRole.Host)
            {
                return;
            }

            _channel.Send(
                EncounterCodec.Encode(new CrateDestroyRequested(crateId, (int)death)),
                MessageDelivery.ReliableOrdered,
                MessageTarget.Host);
        }

        private bool IsHosting => _session.IsActive && _session.Role == SessionRole.Host;

        private void Broadcast(EncodedPayload payload) =>
            _channel.Send(payload, MessageDelivery.ReliableOrdered, MessageTarget.AllClients);

        private void OnReceived(SessionPeerId sender, EncodedPayload payload)
        {
            DecodedMessage message;
            try
            {
                message = EncounterCodec.Decode(payload);
            }
            catch (Exception)
            {
                return;
            }

            // The one message that travels the other way: a client asking for a destruction it saw. Only a host
            // reads it, and never from itself.
            if (message.Value is CrateDestroyRequested request)
            {
                if (IsHosting && sender != _session.HostPeer && IsKnownDeath(request.Death))
                {
                    OnDestroyRequested?.Invoke(request.CrateId, (CrateDeath)request.Death);
                }

                return;
            }

            // Everything else is the host's word. A host never applies its own broadcast, having already done the
            // thing it is describing.
            if (sender != _session.HostPeer || IsHosting)
            {
                return;
            }

            switch (message.Value)
            {
                case CrateDropped dropped when IsFinite(dropped.At)
                    && CratePileId.TryFrom(dropped.PileKind, dropped.PileIndex, out var droppedPile):
                    OnDropped?.Invoke(FromWire(dropped.At), droppedPile);
                    break;

                case CrateThrown thrown when IsFinite(thrown.From) && IsFinite(thrown.To)
                    && IsPositive(thrown.FlightSeconds) && IsFinite(thrown.ApexHeight):
                    OnThrown?.Invoke(
                        FromWire(thrown.From), FromWire(thrown.To), thrown.FlightSeconds, thrown.ApexHeight);
                    break;

                case CratesTaken taken when IsFinite(taken.At)
                    && taken.Count > 0
                    && taken.Count <= MaxLoad
                    && IsPositive(taken.Radius)
                    && taken.Radius <= MaxPickUpReach
                    && CratePileId.TryFrom(taken.PileKind, taken.PileIndex, out var takenPile):
                    OnTaken?.Invoke(FromWire(taken.At), takenPile, taken.Count, taken.Radius);
                    break;

                case CrateDestroyed destroyed when IsKnownDeath(destroyed.Death):
                    OnDestroyed?.Invoke(destroyed.CrateId, (CrateDeath)destroyed.Death);
                    break;

                case CratesSetDown down when IsFinite(down.From)
                    && IsFinite(down.At)
                    && down.Count > 0
                    && down.Count <= MaxLoad
                    && down.Explosives >= 0
                    && down.Explosives <= down.Count
                    && CratePileId.TryFrom(down.PileKind, down.PileIndex, out var downPile):
                    OnSetDown?.Invoke(
                        FromWire(down.From), FromWire(down.At), downPile, down.Count, down.Seed, down.Explosives);
                    break;

                case CrateVolleyFired volley when IsSaneVolley(volley)
                    && CratePileId.TryFrom(volley.PileKind, volley.PileIndex, out var volleyPile):
                    OnVolleyFired?.Invoke(
                        volleyPile,
                        ToAims(volley.Targets),
                        new CrateVolleyShape(
                            volley.Seed,
                            volley.Count,
                            volley.SpreadMinRadius,
                            volley.SpreadMaxRadius,
                            volley.LiftHeight,
                            volley.LiftSeconds,
                            volley.HoldSeconds,
                            volley.FlightSeconds,
                            volley.ApexHeight,
                            volley.LeadShare,
                            volley.FireIntervalSeconds));
                    break;
            }
        }

        /// <summary>Rebuild the aim points a volley named. The wire's own reader has already bounded the count.</summary>
        private static IReadOnlyList<CrateVolleyAim> ToAims(IReadOnlyList<CrateVolleyTarget> targets)
        {
            var aims = new List<CrateVolleyAim>(targets.Count);
            for (var i = 0; i < targets.Count; i++)
            {
                aims.Add(new CrateVolleyAim(FromWire(targets[i].Current), FromWire(targets[i].Lead)));
            }

            return aims;
        }

        private static bool IsSaneVolley(CrateVolleyFired v) =>
            HasSaneTargets(v.Targets)
            && v.Count > 0
            && v.Count <= MaxVolleyCount
            && IsFinite(v.SpreadMinRadius)
            && IsFinite(v.SpreadMaxRadius)
            && v.SpreadMinRadius >= 0f
            && v.SpreadMaxRadius >= v.SpreadMinRadius
            && IsFinite(v.LiftHeight)
            && IsPositive(v.LiftSeconds)
            && IsFinite(v.HoldSeconds)
            && v.HoldSeconds >= 0f
            && IsPositive(v.FlightSeconds)
            && IsFinite(v.ApexHeight)
            && IsFinite(v.LeadShare)
            && v.LeadShare >= 0f
            && v.LeadShare <= 1f
            && IsFinite(v.FireIntervalSeconds)
            && v.FireIntervalSeconds >= 0f
            // A barrage cannot outlast the fight: the last crate waits Count * interval, so a forged command
            // cannot park the whole pile in the air for an hour.
            && v.FireIntervalSeconds <= MaxFireIntervalSeconds;

        /// <summary>A volley must name at least one player and no absurd number of them, with every spot a real
        /// place — an aim full of NaN would scatter crates to nowhere.</summary>
        private static bool HasSaneTargets(IReadOnlyList<CrateVolleyTarget> targets)
        {
            if (targets == null || targets.Count == 0 || targets.Count > MaxVolleyTargets)
            {
                return false;
            }

            for (var i = 0; i < targets.Count; i++)
            {
                if (!IsFinite(targets[i].Current) || !IsFinite(targets[i].Lead))
                {
                    return false;
                }
            }

            return true;
        }

        /// <summary>Whether a reported cause of death is one this build knows. A number from another machine is a
        /// claim, not a <see cref="CrateDeath"/>: an unknown one is dropped rather than cast into the enum, where
        /// it would silently fall through to whichever branch happens to be the else.</summary>
        private static bool IsKnownDeath(int death) =>
            death == (int)CrateDeath.Shot || death == (int)CrateDeath.Struck;

        private static bool IsPositive(float value) => IsFinite(value) && value > 0f;

        private static bool IsFinite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);

        private static bool IsFinite(WorldPosition p) => IsFinite(p.X) && IsFinite(p.Y) && IsFinite(p.Z);

        private static WorldPosition ToWire(ArenaWorldPoint p) => new WorldPosition(p.X, p.Y, p.Z);

        private static ArenaWorldPoint FromWire(WorldPosition p) => new ArenaWorldPoint(p.X, p.Y, p.Z);
    }
}
