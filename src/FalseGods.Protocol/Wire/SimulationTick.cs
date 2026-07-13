using System;

namespace FalseGods.Protocol.Wire
{
    /// <summary>
    /// A host simulation tick stamped on snapshots and events, so clients align presentation (telegraphs, attack
    /// commits, animation) to host simulation time rather than local wall-clock.
    /// </summary>
    /// <remarks>
    /// Docs/OriginalBossNetworkingArchitecture.md §9.5. Monotonically increasing on the host; clients only read
    /// it. It mirrors <c>ISimulationClock.Tick</c>, but as a wire value type — Core's clock and the wire tick are
    /// different concerns and may change independently (Docs/DependencyRules.md §4).
    /// </remarks>
    public readonly struct SimulationTick : IEquatable<SimulationTick>, IComparable<SimulationTick>
    {
        private readonly long _value;

        public SimulationTick(long value)
        {
            _value = value;
        }

        public long Value => _value;

        public int CompareTo(SimulationTick other) => _value.CompareTo(other._value);

        public bool Equals(SimulationTick other) => _value == other._value;

        public override bool Equals(object? obj) => obj is SimulationTick other && Equals(other);

        public override int GetHashCode() => _value.GetHashCode();

        public override string ToString() => $"tick:{_value}";

        public static bool operator ==(SimulationTick left, SimulationTick right) => left.Equals(right);

        public static bool operator !=(SimulationTick left, SimulationTick right) => !left.Equals(right);
    }
}
