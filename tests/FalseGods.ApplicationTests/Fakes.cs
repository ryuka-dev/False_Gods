using System.Collections.Generic;
using FalseGods.Core.Bosses;
using FalseGods.Core.Bosses.Events;
using FalseGods.Core.Simulation;
using FalseGods.RuntimeContracts.Presentation;

namespace FalseGods.ApplicationTests
{
    /// <summary>A clock whose time the test drives by hand.</summary>
    internal sealed class FakeClock : ISimulationClock
    {
        public long Tick { get; private set; }

        public float Time { get; private set; }

        public void Advance(float seconds)
        {
            Time += seconds;
            Tick++;
        }
    }

    /// <summary>Random that returns a fixed value, so attack selection is deterministic.</summary>
    internal sealed class FixedRandom : IAuthoritativeRandom
    {
        private readonly int _value;

        public FixedRandom(int value)
        {
            _value = value;
        }

        public int NextInt(int minInclusive, int maxExclusive) => _value;

        public float NextFloat() => 0f;
    }

    /// <summary>A single participant at a fixed position, so the boss always has a target to aim at.</summary>
    internal sealed class OneParticipant : IEncounterParticipantQuery
    {
        private readonly ParticipantId _id = new ParticipantId(1);
        private readonly SimVector2 _position;

        public OneParticipant(float x, float z)
        {
            _position = new SimVector2(x, z);
        }

        public IReadOnlyList<ParticipantId> Participants => new[] { _id };

        public bool TryGetPosition(ParticipantId participant, out SimVector2 position)
        {
            position = _position;
            return participant == _id;
        }
    }

    /// <summary>
    /// A presentation that records everything it is told, in order, so a test can assert the mapper/driver fed the
    /// single entry point correctly. It decides nothing — which is the whole contract of
    /// <see cref="IEncounterPresentation"/> (RiskList R16/R27).
    /// </summary>
    internal sealed class RecordingPresentation : IEncounterPresentation
    {
        public List<IPresentationEvent> Events { get; } = new List<IPresentationEvent>();

        public List<PresentationState> States { get; } = new List<PresentationState>();

        /// <summary>Every call, in the exact order received, tagged by kind — to assert cues precede the state apply.</summary>
        public List<string> Calls { get; } = new List<string>();

        public void Apply(PresentationState state)
        {
            States.Add(state);
            Calls.Add("state");
        }

        public void Handle(IPresentationEvent presentationEvent)
        {
            Events.Add(presentationEvent);
            Calls.Add("event:" + presentationEvent.GetType().Name);
        }
    }

    /// <summary>Builds and drives a real <see cref="BossSimulation"/> so state-projection tests have real state.</summary>
    internal sealed class BossFixture
    {
        public static readonly BossDefinition Definition = new BossDefinition(
            maxHealth: 100,
            phaseTwoHealthFraction: 0.5f,
            moveSpeed: 0f, // no movement, so aim/position tests are not perturbed by closing distance
            idleSeconds: 1f,
            telegraphSeconds: 1f,
            commitSeconds: 1f,
            recoverSeconds: 1f,
            weakPointDamageMultiplier: 3);

        public FakeClock Clock { get; } = new FakeClock();

        public BossSimulation Boss { get; }

        public BossFixture(int randomValue = 0, float targetX = 10f, float targetZ = 0f)
        {
            Boss = new BossSimulation(
                new BossInstanceId(7),
                Definition,
                Clock,
                new FixedRandom(randomValue),
                new OneParticipant(targetX, targetZ));
        }

        public IReadOnlyList<IBossDomainEvent> Step(float seconds)
        {
            Clock.Advance(seconds);
            Boss.Advance();
            return Boss.DrainEvents();
        }
    }
}
