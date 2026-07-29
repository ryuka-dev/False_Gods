using System;
using System.IO;
using FalseGods.Protocol.Wire;
using Xunit;

namespace FalseGods.ProtocolTests
{
    /// <summary>
    /// Round-trip and untrusted-input tests for the destructible commands. The crates themselves never cross the
    /// wire — these inputs are what every peer rebuilds them from, so a field lost or mangled here is a volley the
    /// two players are not dodging together.
    /// </summary>
    public sealed class CrateMessageTests
    {
        [Fact]
        public void A_drop_round_trips()
        {
            var original = new CrateDropped(
                new WorldPosition(1.5f, 2.5f, -3.5f), PileKind: 2, PileIndex: 1, Explosive: true);

            Assert.Equal(original, WireCodec.DeserializeCrateDropped(WireCodec.Serialize(original)));
        }

        [Fact]
        public void A_pick_up_round_trips()
        {
            var original = new CratesTaken(
                new WorldPosition(2f, 0.1f, -3f), PileKind: 1, PileIndex: 0, Count: 7, Radius: 9f);

            Assert.Equal(original, WireCodec.DeserializeCratesTaken(WireCodec.Serialize(original)));
        }

        [Fact]
        public void A_set_down_round_trips_every_input_the_ring_is_rebuilt_from()
        {
            var original = new CratesSetDown(
                new WorldPosition(1f, 2.9f, 3f),
                new WorldPosition(1f, 0.1f, 3f),
                PileKind: 2,
                PileIndex: 1,
                Count: 7,
                Explosives: 2,
                Seed: 4242);

            Assert.Equal(original, WireCodec.DeserializeCratesSetDown(WireCodec.Serialize(original)));
        }

        [Fact]
        public void A_destruction_round_trips()
        {
            var original = new CrateDestroyed(CrateId: 4242, Death: 1);

            Assert.Equal(original, WireCodec.DeserializeCrateDestroyed(WireCodec.Serialize(original)));
        }

        [Fact]
        public void A_destruction_request_round_trips()
        {
            var original = new CrateDestroyRequested(CrateId: 7, Death: 0);

            Assert.Equal(original, WireCodec.DeserializeCrateDestroyRequested(WireCodec.Serialize(original)));
        }

        [Fact]
        public void A_throw_round_trips()
        {
            var original = new CrateThrown(
                new WorldPosition(1f, 2f, 3f), new WorldPosition(-4f, 0f, 5f), 1.6f, 3f);

            Assert.Equal(original, WireCodec.DeserializeCrateThrown(WireCodec.Serialize(original)));
        }

        [Fact]
        public void A_volley_round_trips_every_input_it_is_rebuilt_from()
        {
            // Two players, each with their own pair of threatened spots — the shape that stops a volley landing
            // entirely on whoever happens to be hosting.
            var original = new CrateVolleyFired(
                new[]
                {
                    new CrateVolleyTarget(new WorldPosition(1f, 2f, 3f), new WorldPosition(4f, 5f, 6f)),
                    new CrateVolleyTarget(new WorldPosition(-7f, 2f, 8f), new WorldPosition(-9f, 2f, 10f)),
                },
                PileKind: 2,
                PileIndex: 3,
                Seed: 12345,
                Count: 6,
                SpreadMinRadius: 1.4f,
                SpreadMaxRadius: 4.2f,
                LiftHeight: 5f,
                LiftSeconds: 0.5f,
                HoldSeconds: 0.83f,
                FlightSeconds: 1.2f,
                ApexHeight: 4f,
                LeadShare: 0.5f,
                FireIntervalSeconds: 0.333f);

            var rebuilt = WireCodec.DeserializeCrateVolleyFired(WireCodec.Serialize(original));

            // The record holds a list, so its own equality is by reference; the targets are compared element-wise
            // and the rest by rebuilding the record around them.
            Assert.Equal(original.Targets, rebuilt.Targets);
            Assert.Equal(original with { Targets = rebuilt.Targets }, rebuilt);
        }

        [Fact]
        public void A_volley_naming_absurdly_many_players_is_refused_rather_than_allocated()
        {
            var payload = WireCodec.Serialize(new CrateVolleyFired(
                new[] { new CrateVolleyTarget(new WorldPosition(0f, 0f, 0f), new WorldPosition(0f, 0f, 0f)) },
                2, 0, 1, 1, 1f, 2f, 1f, 1f, 1f, 1f, 1f, 0.5f, 0f));

            // Overwrite the leading target count with something no party could justify.
            BitConverter.GetBytes(int.MaxValue).CopyTo(payload, 0);

            Assert.Throws<InvalidDataException>(() => WireCodec.DeserializeCrateVolleyFired(payload));
        }

        [Fact]
        public void Trailing_bytes_throw_not_ignored()
        {
            var payload = WireCodec.Serialize(new CrateDropped(new WorldPosition(0f, 0f, 0f), 1, 0, false));
            var padded = new byte[payload.Length + 1];
            Array.Copy(payload, padded, payload.Length);

            Assert.Throws<InvalidDataException>(() => WireCodec.DeserializeCrateDropped(padded));
        }

        [Fact]
        public void Truncated_payload_throws_not_misreads()
        {
            var payload = WireCodec.Serialize(new CrateThrown(
                new WorldPosition(1f, 2f, 3f), new WorldPosition(4f, 5f, 6f), 1f, 1f));
            var truncated = new byte[payload.Length - 3];
            Array.Copy(payload, truncated, truncated.Length);

            Assert.ThrowsAny<Exception>(() => WireCodec.DeserializeCrateThrown(truncated));
        }
    }

    /// <summary>
    /// Round-trip and untrusted-input tests for the session's boss-arena declaration: the host says which level
    /// is a boss arena, and a peer asks the host to take everyone there.
    /// </summary>
    public sealed class ArenaLevelMessageTests
    {
        [Fact]
        public void Declaration_round_trips()
        {
            var original = new ArenaLevelDeclared(new ArenaLevelId(3, 0), true);

            var decoded = WireCodec.DeserializeArenaLevelDeclared(WireCodec.Serialize(original));

            Assert.Equal(original, decoded);
        }

        [Fact]
        public void Withdrawal_round_trips_distinctly_from_a_declaration()
        {
            var level = new ArenaLevelId(3, 0);

            var declared = WireCodec.DeserializeArenaLevelDeclared(
                WireCodec.Serialize(new ArenaLevelDeclared(level, true)));
            var withdrawn = WireCodec.DeserializeArenaLevelDeclared(
                WireCodec.Serialize(new ArenaLevelDeclared(level, false)));

            Assert.True(declared.IsBossArena);
            Assert.False(withdrawn.IsBossArena);
        }

        [Fact]
        public void Request_round_trips()
        {
            var original = new ArenaLevelRequested(new ArenaLevelId(3, 0));

            var decoded = WireCodec.DeserializeArenaLevelRequested(WireCodec.Serialize(original));

            Assert.Equal(original, decoded);
        }

        [Fact]
        public void A_negative_environment_survives_the_wire_for_the_receiver_to_refuse()
        {
            // The codec does not know which environments exist; validating that is the integration layer's job,
            // and it can only do it if the value arrives intact rather than being clamped or thrown here.
            var original = new ArenaLevelDeclared(new ArenaLevelId(-7, 4), true);

            var decoded = WireCodec.DeserializeArenaLevelDeclared(WireCodec.Serialize(original));

            Assert.Equal(-7, decoded.Level.Environment);
            Assert.Equal(4, decoded.Level.LevelIndex);
        }

        [Fact]
        public void Trailing_bytes_throw_not_ignored()
        {
            var payload = WireCodec.Serialize(new ArenaLevelDeclared(new ArenaLevelId(3, 0), true));
            var padded = new byte[payload.Length + 1];
            Array.Copy(payload, padded, payload.Length);

            Assert.Throws<InvalidDataException>(() => WireCodec.DeserializeArenaLevelDeclared(padded));
        }

        [Fact]
        public void Truncated_payload_throws_not_misreads()
        {
            var payload = WireCodec.Serialize(new ArenaLevelRequested(new ArenaLevelId(3, 0)));
            var truncated = new byte[payload.Length - 3];
            Array.Copy(payload, truncated, truncated.Length);

            Assert.ThrowsAny<Exception>(() => WireCodec.DeserializeArenaLevelRequested(truncated));
        }
    }
}
