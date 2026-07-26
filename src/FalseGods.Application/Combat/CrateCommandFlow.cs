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
                shape.LeadShare)));
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
                            volley.LeadShare));
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
            && v.LeadShare <= 1f;

        private static bool IsPositive(float value) => IsFinite(value) && value > 0f;

        private static bool IsFinite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);

        private static bool IsFinite(WorldPosition p) => IsFinite(p.X) && IsFinite(p.Y) && IsFinite(p.Z);

        private static WorldPosition ToWire(ArenaWorldPoint p) => new WorldPosition(p.X, p.Y, p.Z);

        private static ArenaWorldPoint FromWire(WorldPosition p) => new ArenaWorldPoint(p.X, p.Y, p.Z);
    }
}
