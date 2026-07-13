using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using FalseGods.Protocol.Wire;
using Xunit;

namespace FalseGods.ProtocolTests
{
    /// <summary>
    /// The FG-ARCH-008 property, exercised as a Protocol unit test: boss and arena wire state/events stay separate
    /// (Docs/ADRs/ADR-005, Docs/ArchitectureEnforcement.md#fg-arch-008). A boss reusable across arenas must never
    /// carry one arena's mechanism vocabulary, so no boss wire type may expose an arena type and no arena wire type
    /// may expose a boss type. <see cref="EncounterBaseline"/> is the only permitted composition of both.
    /// </summary>
    public sealed class BossArenaSeparationTests
    {
        private static readonly Assembly Protocol = typeof(BossSnapshot).Assembly;

        private static bool IsArenaSide(Type type) =>
            type.Namespace == "FalseGods.Core.Arena"
            || type == typeof(ArenaSnapshot)
            || typeof(IArenaWireEvent).IsAssignableFrom(type);

        private static bool IsBossSide(Type type) =>
            type.Namespace == "FalseGods.Core.Bosses"
            || type == typeof(BossSnapshot)
            || typeof(IBossWireEvent).IsAssignableFrom(type);

        private static IEnumerable<Type> ConcreteImplementing(Type marker) =>
            Protocol.GetTypes().Where(t => t.IsPublic && !t.IsInterface && !t.IsAbstract && marker.IsAssignableFrom(t));

        // Every public property type a wire type exposes, unwrapping generic arguments and array elements.
        private static IEnumerable<Type> ReferencedTypes(Type wireType)
        {
            foreach (var property in wireType.GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                foreach (var referenced in Unwrap(property.PropertyType))
                {
                    yield return referenced;
                }
            }
        }

        private static IEnumerable<Type> Unwrap(Type type)
        {
            yield return type;
            if (type.IsArray)
            {
                foreach (var t in Unwrap(type.GetElementType()!))
                {
                    yield return t;
                }
            }

            if (type.IsGenericType)
            {
                foreach (var arg in type.GetGenericArguments())
                {
                    foreach (var t in Unwrap(arg))
                    {
                        yield return t;
                    }
                }
            }
        }

        [Fact]
        public void No_boss_wire_type_exposes_an_arena_type()
        {
            var bossTypes = ConcreteImplementing(typeof(IBossWireEvent)).Append(typeof(BossSnapshot));

            foreach (var bossType in bossTypes)
            {
                var leaked = ReferencedTypes(bossType).Where(IsArenaSide).ToArray();
                Assert.True(
                    leaked.Length == 0,
                    $"{bossType.Name} exposes arena type(s): {string.Join(", ", leaked.Select(t => t.Name).Distinct())}");
            }
        }

        [Fact]
        public void No_arena_wire_type_exposes_a_boss_type()
        {
            var arenaTypes = ConcreteImplementing(typeof(IArenaWireEvent)).Append(typeof(ArenaSnapshot));

            foreach (var arenaType in arenaTypes)
            {
                var leaked = ReferencedTypes(arenaType).Where(IsBossSide).ToArray();
                Assert.True(
                    leaked.Length == 0,
                    $"{arenaType.Name} exposes boss type(s): {string.Join(", ", leaked.Select(t => t.Name).Distinct())}");
            }
        }

        [Fact]
        public void EncounterBaseline_is_the_one_type_permitted_to_compose_both()
        {
            var referenced = ReferencedTypes(typeof(EncounterBaseline)).ToArray();
            Assert.Contains(referenced, IsBossSide);
            Assert.Contains(referenced, IsArenaSide);
        }
    }
}
