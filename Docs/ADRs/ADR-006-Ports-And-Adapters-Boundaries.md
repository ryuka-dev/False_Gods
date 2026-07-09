# ADR-006 — Ports-and-adapters module boundaries

**Status:** Proposed

## Context
SULFUR Together accumulated debt because features reached directly into managers, statics, Harmony patches,
and concrete transport/message types, so boundaries blurred and a second transport was expensive. False Gods
wants invalid dependencies to be hard to compile from day one.

## Decision
Adopt a **ports-and-adapters (hexagonal)** structure with a strict inward dependency direction
(`Plugin → UnityRuntime → Protocol → Core`; adapters referenced only by the Composition Root). `FalseGods.Core`
declares **ports** (`IGameClock`, `IAuthoritativeRandom`, `IPlayerRoster`, `IDamagePort`, `ISpawnPort`,
`INavigationPort`, `IVanillaAssetProvider`, `IArenaLockdownPort`, `IEncounterReplication`, `IEncounterChannel`,
`IMultiplayerSession`, `ILogger`, …); outer adapters (`Integration.Sulfur`, `Integration.SulfurTogether`,
`UnityRuntime`) implement them. Boss / Arena / Encounter are separate Core concerns coordinated by
`EncounterCoordinator`. Full rules in [Architecture.md](../Architecture.md) and
[DependencyRules.md](../DependencyRules.md).

## Alternatives considered
- **Layered-but-permissive** (shared references, discipline only) — what caused the ST debt. Rejected.
- **No abstraction until needed** — risks the same reach-in coupling; instead we add the *smallest* port when a
  boundary is first crossed, and generalize only on a second use case (anti-overengineering rule).

## Consequences
- More projects/assemblies and some indirection up front.
- Testable Unity-less Core; replaceable game/multiplayer/transport integrations; enforceable via separate
  csproj/asmdef + CI namespace scans (planned, not built).
- Prefabs are content only, bound to ports by the Composition Root — no service locators.

## Verification status
Unverified (structural) — enforced later by the mechanical checks in
[DependencyRules.md §7](../DependencyRules.md); risks R21–R31 track leakage.
