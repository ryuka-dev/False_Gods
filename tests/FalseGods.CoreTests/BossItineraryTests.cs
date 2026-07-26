using System;
using System.Collections.Generic;
using System.Linq;
using FalseGods.Core.Bosses;
using FalseGods.Core.Bosses.Events;
using FalseGods.Core.Simulation;
using Xunit;

namespace FalseGods.CoreTests
{
    /// <summary>
    /// A boss whose standing places are authored: it starts at its first station, relocates when its health falls
    /// to a station's threshold, and never walks.
    /// </summary>
    public sealed class BossItineraryTests
    {
        // The first boss's shape: home, away, home, away, home — read against two authored anchors.
        private static readonly IReadOnlyList<BossStation> Itinerary = new[]
        {
            new BossStation(0, 1.00f),
            new BossStation(1, 0.80f),
            new BossStation(0, 0.60f),
            new BossStation(1, 0.40f),
            new BossStation(0, 0.20f),
        };

        private static readonly IReadOnlyList<BossAnchor> Anchors = new[]
        {
            new BossAnchor(new SimVector2(-8.18f, 25.97f), 8.1f), // the perch
            new BossAnchor(new SimVector2(0.24f, -7.69f), 0.1f),  // the floor
        };

        private static BossDefinition Anchored(int maxHealth = 100) => new BossDefinition(
            maxHealth: maxHealth,
            phaseTwoHealthFraction: 0.5f,
            moveSpeed: 0f,
            idleSeconds: 1f,
            telegraphSeconds: 1f,
            commitSeconds: 0.5f,
            recoverSeconds: 1f,
            weakPointDamageMultiplier: 1,
            attackDamage: 10,
            aimedHitRadius: 2f,
            areaHitRadius: 5f,
            stations: Itinerary);

        private static BossSimulation Spawned(BossTestHarness harness, BossDefinition? definition = null)
        {
            var boss = harness.Build(definition ?? Anchored(), anchors: Anchors);
            boss.Spawn(new SimVector2(999f, 999f), 999f);
            boss.DrainEvents();
            return boss;
        }

        [Fact]
        public void Spawns_at_its_first_station_not_at_the_fallback()
        {
            var boss = Spawned(new BossTestHarness());

            Assert.Equal(0, boss.StationIndex);
            Assert.Equal(Anchors[0].Ground, boss.Position);
            Assert.Equal(8.1f, boss.PositionHeight);
        }

        [Fact]
        public void Crossing_a_stations_threshold_relocates_it_to_that_anchor()
        {
            var harness = new BossTestHarness();
            var boss = Spawned(harness);

            boss.ApplyDamage(21); // 100 -> 79, at or below the 80% station
            var events = boss.DrainEvents();

            var moved = events.OfType<BossRelocated>().Single();
            Assert.Equal(1, moved.StationIndex);
            Assert.Equal(1, moved.AnchorIndex);
            Assert.Equal(Anchors[1].Ground, boss.Position);
            Assert.Equal(0.1f, boss.PositionHeight);
        }

        [Fact]
        public void Damage_that_does_not_reach_the_next_threshold_leaves_it_where_it_stands()
        {
            var boss = Spawned(new BossTestHarness());

            boss.ApplyDamage(19); // 100 -> 81, still above 80%
            var events = boss.DrainEvents();

            Assert.Empty(events.OfType<BossRelocated>());
            Assert.Equal(0, boss.StationIndex);
            Assert.Equal(Anchors[0].Ground, boss.Position);
        }

        [Fact]
        public void One_hit_crossing_several_stations_lands_at_the_last_and_reports_once()
        {
            var boss = Spawned(new BossTestHarness());

            boss.ApplyDamage(85); // 100 -> 15: past the 80, 60, 40 and 20 per-cent stations
            var events = boss.DrainEvents();

            var moved = events.OfType<BossRelocated>().Single();
            Assert.Equal(4, moved.StationIndex);
            Assert.Equal(0, moved.AnchorIndex);
            Assert.Equal(4, boss.StationIndex);
        }

        [Fact]
        public void The_whole_itinerary_is_walked_in_order_as_health_falls()
        {
            var boss = Spawned(new BossTestHarness());
            var visited = new List<int>();

            for (var i = 0; i < 10; i++)
            {
                boss.ApplyDamage(10);
                visited.AddRange(boss.DrainEvents().OfType<BossRelocated>().Select(e => e.AnchorIndex));
            }

            Assert.Equal(new[] { 1, 0, 1, 0 }, visited);
        }

        [Fact]
        public void An_anchored_boss_never_walks_toward_its_target()
        {
            var harness = new BossTestHarness().WithParticipantAt(1, 40f, 40f);
            var boss = Spawned(harness);

            harness.Step(0.5f);

            Assert.Equal(Anchors[0].Ground, boss.Position);
        }

        [Fact]
        public void Death_is_not_a_relocation()
        {
            var boss = Spawned(new BossTestHarness());

            boss.ApplyDamage(1000);
            var events = boss.DrainEvents();

            Assert.True(boss.IsDead);
            Assert.Empty(events.OfType<BossRelocated>());
        }

        [Fact]
        public void A_room_with_no_anchors_leaves_the_boss_at_its_spawn()
        {
            var harness = new BossTestHarness();
            var boss = harness.Build(Anchored(), anchors: Array.Empty<BossAnchor>());
            boss.Spawn(new SimVector2(3f, 4f), 5f);
            boss.DrainEvents();

            boss.ApplyDamage(50);

            Assert.Equal(new SimVector2(3f, 4f), boss.Position);
            Assert.Equal(5f, boss.PositionHeight);
            Assert.Empty(boss.DrainEvents().OfType<BossRelocated>());
        }

        [Fact]
        public void A_boss_with_no_itinerary_stands_where_it_is_spawned()
        {
            var harness = new BossTestHarness();
            var boss = harness.Build(anchors: Anchors); // the standard definition has no stations
            boss.Spawn(new SimVector2(3f, 4f), 5f);

            Assert.Equal(-1, boss.StationIndex);
            Assert.Equal(new SimVector2(3f, 4f), boss.Position);
            Assert.Equal(5f, boss.PositionHeight);
        }

        // ---------------------------------------------------------------- definition validation

        [Fact]
        public void An_itinerary_that_does_not_start_at_full_health_is_refused()
        {
            var stations = new[] { new BossStation(0, 0.9f), new BossStation(1, 0.5f) };

            Assert.Throws<ArgumentException>(() => Definition(stations, moveSpeed: 0f));
        }

        [Fact]
        public void An_itinerary_that_does_not_descend_is_refused()
        {
            var stations = new[] { new BossStation(0, 1f), new BossStation(1, 0.5f), new BossStation(0, 0.5f) };

            Assert.Throws<ArgumentException>(() => Definition(stations, moveSpeed: 0f));
        }

        [Fact]
        public void A_boss_that_both_walks_and_has_an_itinerary_is_refused()
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => Definition(Itinerary, moveSpeed: 1.5f));
        }

        [Fact]
        public void A_station_entered_at_zero_health_is_refused()
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => new BossStation(0, 0f));
        }

        private static BossDefinition Definition(IReadOnlyList<BossStation> stations, float moveSpeed) =>
            new BossDefinition(
                maxHealth: 100,
                phaseTwoHealthFraction: 0.5f,
                moveSpeed: moveSpeed,
                idleSeconds: 1f,
                telegraphSeconds: 1f,
                commitSeconds: 0.5f,
                recoverSeconds: 1f,
                weakPointDamageMultiplier: 1,
                attackDamage: 10,
                aimedHitRadius: 2f,
                areaHitRadius: 5f,
                stations: stations);
    }
}
