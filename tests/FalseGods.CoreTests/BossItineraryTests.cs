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

        /// <summary>
        /// Drive a whole relocation and hand back everything it emitted. A relocation only begins once the boss
        /// is idle and then takes real time — leaving, hidden, arriving — so a test that only applies damage sees
        /// nothing, which is the point.
        /// </summary>
        private static IReadOnlyList<IBossDomainEvent> RunRelocation(BossTestHarness harness)
        {
            var collected = new List<IBossDomainEvent>();
            for (var i = 0; i < 6; i++)
            {
                collected.AddRange(harness.Step(0.7f));
            }

            return collected;
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
            Assert.Empty(boss.DrainEvents().OfType<BossRelocated>()); // not yet: it leaves between actions
            var events = RunRelocation(harness);

            var moved = events.OfType<BossRelocated>().Single();
            Assert.Equal(1, moved.StationIndex);
            Assert.Equal(1, moved.AnchorIndex);
            Assert.Equal(Anchors[1].Ground, boss.Position);
            Assert.Equal(0.1f, boss.PositionHeight);
        }

        [Fact]
        public void Damage_that_does_not_reach_the_next_threshold_leaves_it_where_it_stands()
        {
            var harness = new BossTestHarness();
            var boss = Spawned(harness);

            boss.ApplyDamage(19); // 100 -> 81, still above 80%
            boss.DrainEvents();
            var events = RunRelocation(harness);

            Assert.Empty(events.OfType<BossRelocated>());
            Assert.Equal(0, boss.StationIndex);
            Assert.Equal(Anchors[0].Ground, boss.Position);
        }

        [Fact]
        public void One_hit_crossing_several_stations_walks_them_one_at_a_time()
        {
            var harness = new BossTestHarness();
            var boss = Spawned(harness);

            boss.ApplyDamage(85); // 100 -> 15: past the 80, 60, 40 and 20 per-cent stations at once
            boss.DrainEvents();

            var visited = new List<int>();
            for (var i = 0; i < 30; i++)
            {
                visited.AddRange(harness.Step(0.3f).OfType<BossRelocated>().Select(e => e.AnchorIndex));
            }

            // Not a jump to the last one: a weapon strong enough to cross the whole itinerary at once does not
            // get to delete the fight's shape.
            Assert.Equal(new[] { 1, 0, 1, 0 }, visited);
            Assert.Equal(4, boss.StationIndex);
        }

        [Fact]
        public void The_whole_itinerary_is_walked_in_order_as_health_falls()
        {
            var harness = new BossTestHarness();
            var boss = Spawned(harness);
            var visited = new List<int>();

            for (var i = 0; i < 10; i++)
            {
                boss.ApplyDamage(10);
                boss.DrainEvents();
                visited.AddRange(RunRelocation(harness).OfType<BossRelocated>().Select(e => e.AnchorIndex));
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
            var harness = new BossTestHarness();
            var boss = Spawned(harness);

            boss.ApplyDamage(1000);
            boss.DrainEvents();
            var events = RunRelocation(harness);

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
            var events = RunRelocation(harness);

            Assert.Equal(new SimVector2(3f, 4f), boss.Position);
            Assert.Equal(5f, boss.PositionHeight);
            Assert.Empty(events.OfType<BossRelocated>());
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

        // ---------------------------------------------------------------- the relocation itself

        [Fact]
        public void It_leaves_is_gone_and_arrives_in_that_order()
        {
            var harness = new BossTestHarness();
            var boss = Spawned(harness);
            boss.ApplyDamage(21);

            harness.Step(0.01f);
            Assert.Equal(BossActivity.Vanishing, boss.Activity);
            Assert.Equal(Anchors[0].Ground, boss.Position); // still where it was: it has not gone yet

            harness.Step(0.5f);
            Assert.Equal(BossActivity.Hidden, boss.Activity);
            Assert.Equal(Anchors[1].Ground, boss.Position); // moved out of sight

            harness.Step(0.7f);
            Assert.Equal(BossActivity.Appearing, boss.Activity);

            harness.Step(0.7f);
            Assert.Equal(BossActivity.Idle, boss.Activity);
        }

        [Fact]
        public void It_takes_no_damage_for_the_whole_relocation()
        {
            var harness = new BossTestHarness();
            var boss = Spawned(harness);
            boss.ApplyDamage(21); // 100 -> 79
            harness.Step(0.01f);
            var healthOnLeaving = boss.Health;

            // Fire at it through leaving, gone, and arriving.
            for (var i = 0; i < 3; i++)
            {
                boss.ApplyDamage(50);
                Assert.True(boss.IsRelocating);
                Assert.Equal(healthOnLeaving, boss.Health);
                harness.Step(0.7f);
            }

            // And it is hittable again the moment it is back.
            Assert.Equal(BossActivity.Idle, boss.Activity);
            boss.ApplyDamage(5);
            Assert.Equal(healthOnLeaving - 5, boss.Health);
        }

        [Fact]
        public void A_boss_mid_attack_leaves_at_once_and_that_attack_never_lands()
        {
            var harness = new BossTestHarness().WithParticipantAt(1, 5f, 5f).WithRandom(0);
            var boss = Spawned(harness);

            harness.Step(1.1f); // idle elapses: it telegraphs
            Assert.Equal(BossActivity.Telegraphing, boss.Activity);
            boss.DrainEvents();

            boss.ApplyDamage(21); // reaches the next station mid-telegraph

            // Waiting for a clean moment sounds polite and does not survive contact: measured in game, a fast
            // weapon crossed every threshold inside one attack cycle and the boss never moved at all.
            Assert.Equal(BossActivity.Vanishing, boss.Activity);

            var events = new List<IBossDomainEvent>();
            for (var i = 0; i < 6; i++)
            {
                events.AddRange(harness.Step(0.3f));
            }

            Assert.Single(events.OfType<BossRelocated>());
            Assert.Empty(events.OfType<AttackCommitted>()); // the interrupted attack pays for the retreat
        }

        [Fact]
        public void Interrupting_the_weak_point_window_closes_it()
        {
            var harness = new BossTestHarness().WithParticipantAt(1, 5f, 5f).WithRandom(0);
            var boss = Spawned(harness);
            harness.Step(1.1f); // telegraph
            harness.Step(1.1f); // commit
            harness.Step(0.6f); // recovery: the weak point is open
            Assert.True(boss.IsWeakPointExposed);
            boss.DrainEvents();

            boss.ApplyDamage(21);

            // The flag is derived from the activity, so without this event a client would keep the weak point lit
            // on a boss that no longer has one.
            var closed = boss.DrainEvents().OfType<WeakPointExposed>().Single();
            Assert.False(closed.Exposed);
            Assert.False(boss.IsWeakPointExposed);
        }

        [Fact]
        public void A_station_reached_while_it_is_away_is_taken_on_the_next_leaving()
        {
            var harness = new BossTestHarness();
            var boss = Spawned(harness);
            boss.ApplyDamage(21); // -> station 1
            harness.Step(0.01f);
            Assert.Equal(BossActivity.Vanishing, boss.Activity);

            // A hit landing here is refused, so the itinerary cannot be skipped past by shooting during a move.
            boss.ApplyDamage(60);
            var events = RunRelocation(harness);

            Assert.Equal(1, boss.StationIndex);
            Assert.Single(events.OfType<BossRelocated>());
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
