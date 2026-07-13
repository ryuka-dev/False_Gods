using System.Collections.Generic;
using FalseGods.Core.Simulation;

namespace FalseGods.CoreTests
{
    /// <summary>A clock whose time the test drives by hand, so the boss's cycle is fully deterministic.</summary>
    internal sealed class FakeClock : ISimulationClock
    {
        public long Tick { get; private set; }

        public float Time { get; private set; }

        /// <summary>Move host simulation time forward by <paramref name="seconds"/> and advance the tick count.</summary>
        public void Advance(float seconds)
        {
            Time += seconds;
            Tick++;
        }
    }

    /// <summary>
    /// A random source that replays a scripted sequence of ints, so attack selection is deterministic. Falls back
    /// to the first configured value (or 0) once the sequence is exhausted.
    /// </summary>
    internal sealed class ScriptedRandom : IAuthoritativeRandom
    {
        private readonly Queue<int> _ints;

        public ScriptedRandom(params int[] values)
        {
            _ints = new Queue<int>(values);
        }

        public int NextInt(int minInclusive, int maxExclusive)
        {
            if (_ints.Count == 0)
            {
                return minInclusive;
            }

            return _ints.Dequeue();
        }

        public float NextFloat() => 0f;
    }

    /// <summary>A mutable participant roster the test controls: add, remove, and move participants at will.</summary>
    internal sealed class FakeParticipants : IEncounterParticipantQuery
    {
        private readonly List<ParticipantId> _order = new List<ParticipantId>();
        private readonly Dictionary<ParticipantId, SimVector2> _positions = new Dictionary<ParticipantId, SimVector2>();

        public IReadOnlyList<ParticipantId> Participants => _order;

        public bool TryGetPosition(ParticipantId participant, out SimVector2 position) =>
            _positions.TryGetValue(participant, out position);

        public void Set(ParticipantId participant, SimVector2 position)
        {
            if (!_positions.ContainsKey(participant))
            {
                _order.Add(participant);
            }

            _positions[participant] = position;
        }

        public void Remove(ParticipantId participant)
        {
            _order.Remove(participant);
            _positions.Remove(participant);
        }

        public void Clear()
        {
            _order.Clear();
            _positions.Clear();
        }
    }
}
