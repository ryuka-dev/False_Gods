using System.Collections.Generic;
using System.Linq;
using FalseGods.Core.Bosses;
using FalseGods.Core.Bosses.Events;
using FalseGods.Core.Simulation;

namespace FalseGods.CoreTests
{
    /// <summary>
    /// A small builder that wires a <see cref="BossSimulation"/> to the three fakes with a known definition, so
    /// each test drives time and the roster and reads events without repeating the plumbing.
    /// </summary>
    internal sealed class BossTestHarness
    {
        // A definition with round timings so the cycle boundaries fall on exact times the tests step to.
        public static readonly BossDefinition StandardDefinition = new BossDefinition(
            maxHealth: 100,
            phaseTwoHealthFraction: 0.5f,
            moveSpeed: 2f,
            idleSeconds: 1f,
            telegraphSeconds: 1f,
            commitSeconds: 0.5f,
            recoverSeconds: 1f,
            weakPointDamageMultiplier: 3,
            attackDamage: 10,
            aimedHitRadius: 2f,
            areaHitRadius: 5f);

        public FakeClock Clock { get; } = new FakeClock();

        public FakeParticipants Participants { get; } = new FakeParticipants();

        public ScriptedRandom Random { get; private set; } = new ScriptedRandom();

        public BossSimulation Boss { get; private set; } = null!;

        public BossTestHarness WithRandom(params int[] values)
        {
            Random = new ScriptedRandom(values);
            return this;
        }

        public BossTestHarness WithParticipantAt(int id, float x, float z)
        {
            Participants.Set(new ParticipantId(id), new SimVector2(x, z));
            return this;
        }

        public BossSimulation Build(BossDefinition? definition = null, int bossId = 1)
        {
            Boss = new BossSimulation(
                new BossInstanceId(bossId),
                definition ?? StandardDefinition,
                Clock,
                Random,
                Participants);
            return Boss;
        }

        /// <summary>Advance host time by <paramref name="seconds"/> in one step, then tick the boss once.</summary>
        public IReadOnlyList<IBossDomainEvent> Step(float seconds)
        {
            Clock.Advance(seconds);
            Boss.Advance();
            return Boss.DrainEvents();
        }

        public static T Single<T>(IReadOnlyList<IBossDomainEvent> events) where T : IBossDomainEvent =>
            events.OfType<T>().Single();

        public static bool Has<T>(IReadOnlyList<IBossDomainEvent> events) where T : IBossDomainEvent =>
            events.OfType<T>().Any();
    }
}
