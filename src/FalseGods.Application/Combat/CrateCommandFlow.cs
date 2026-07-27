using System;
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
        public Action<CratePileId, ArenaWorldPoint, ArenaWorldPoint, CrateVolleyShape>? OnVolleyFired { get; set; }

        /// <summary>A carrier picked a load up on the host; take the same crates off the same heap here.</summary>
        public Action<ArenaWorldPoint, CratePileId, int, float>? OnTaken { get; set; }

        /// <summary>A load was set down or spilled on the host; lay the same ring out here.</summary>
        public Action<ArenaWorldPoint, ArenaWorldPoint, CratePileId, int, int>? OnSetDown { get; set; }

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
            CratePileId pile, ArenaWorldPoint currentCenter, ArenaWorldPoint leadCenter, CrateVolleyShape shape)
        {
            if (!IsHosting)
            {
                return;
            }

            Broadcast(EncounterCodec.Encode(new CrateVolleyFired(
                ToWire(currentCenter),
                ToWire(leadCenter),
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
            ArenaWorldPoint from, ArenaWorldPoint at, CratePileId pile, int count, int seed)
        {
            if (IsHosting)
            {
                Broadcast(EncounterCodec.Encode(
                    new CratesSetDown(ToWire(from), ToWire(at), (int)pile.Kind, pile.Index, count, seed)));
            }
        }

        private bool IsHosting => _session.IsActive && _session.Role == SessionRole.Host;

        private void Broadcast(EncodedPayload payload) =>
            _channel.Send(payload, MessageDelivery.ReliableOrdered, MessageTarget.AllClients);

        private void OnReceived(SessionPeerId sender, EncodedPayload payload)
        {
            // Only the host decides what the world does; and a host never applies its own broadcast, having
            // already done the thing it is describing.
            if (sender != _session.HostPeer || IsHosting)
            {
                return;
            }

            DecodedMessage message;
            try
            {
                message = EncounterCodec.Decode(payload);
            }
            catch (Exception)
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

                case CratesSetDown down when IsFinite(down.From)
                    && IsFinite(down.At)
                    && down.Count > 0
                    && down.Count <= MaxLoad
                    && CratePileId.TryFrom(down.PileKind, down.PileIndex, out var downPile):
                    OnSetDown?.Invoke(FromWire(down.From), FromWire(down.At), downPile, down.Count, down.Seed);
                    break;

                case CrateVolleyFired volley when IsSaneVolley(volley)
                    && CratePileId.TryFrom(volley.PileKind, volley.PileIndex, out var volleyPile):
                    OnVolleyFired?.Invoke(
                        volleyPile,
                        FromWire(volley.CurrentCenter),
                        FromWire(volley.LeadCenter),
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

        private static bool IsSaneVolley(CrateVolleyFired v) =>
            IsFinite(v.CurrentCenter)
            && IsFinite(v.LeadCenter)
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

        private static bool IsPositive(float value) => IsFinite(value) && value > 0f;

        private static bool IsFinite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);

        private static bool IsFinite(WorldPosition p) => IsFinite(p.X) && IsFinite(p.Y) && IsFinite(p.Z);

        private static WorldPosition ToWire(ArenaWorldPoint p) => new WorldPosition(p.X, p.Y, p.Z);

        private static ArenaWorldPoint FromWire(WorldPosition p) => new ArenaWorldPoint(p.X, p.Y, p.Z);
    }
}
