# ADR-006 — Ports-and-adapters module boundaries

**Status:** Proposed

## Context
SULFUR Together accumulated debt because features reached directly into managers, statics, Harmony patches,
and concrete transport/message types, so boundaries blurred and a second transport was expensive. False Gods
wants invalid dependencies to be hard to compile from day one.

The opposite failure is just as real: declaring every port in the domain module. A `FalseGods.Core` that owns
`IAssetProvider`, `INavigationPort`, `IEncounterChannel`, and `IMultiplayerSession` is not a domain — it is a
service registry wearing a domain's name, and it makes the "Core is pure and testable" claim hollow.

## Decision

**Structure.** Ports-and-adapters (hexagonal), with a strict inward dependency direction:

```
Core  ◄── Protocol            Core ◄── RuntimeContracts
  ▲          ▲                          ▲
  └──── Application ─────────────────────┘
                                UnityRuntime ──► Core, RuntimeContracts (never Protocol)
Adapters ──► the module that declares the port they implement
Plugin (Composition Root) ──► everything except Integration.SulfurTogether (ADR-004)
```

**Port placement.** A port is declared by **the innermost module that actually consumes it**, and **no port is
created without a present consumer**.

| Declared in | Ports | Why there |
|---|---|---|
| `FalseGods.Core` | `ISimulationClock`, `IAuthoritativeRandom`, `IEncounterParticipantQuery` | Called from inside domain logic: the simulation ticks, rolls authoritative decisions, and selects a target among participants. |
| `FalseGods.RuntimeContracts` | `IPlayerRoster`, `IMultiplayerSession`, `IEncounterChannel`, `IArenaLockdownPort`, `IEncounterReadyGate`, `IRemoteNpcActivationPort`, `IEncounterPresentation`, `ILogger`, plus the `IFalseGodsIntegration` / `IIntegrationRegistration` seam and the `FalseGodsIntegrations` broker | Implemented by *either* `Integration.Sulfur` (single-player) *or* the optional ST adapter, so they must sit in the small assembly both can reference (ADR-004). |
| `FalseGods.Application` | `IEncounterReplication`, `IDamagePort`, `ISpawnPort`, `INavigationPort`, `IArenaAssetProvider`, `IVanillaAssetProvider`, `IArenaRealization`, `ISceneLifecycleEvents` | Consumed by the encounter/arena orchestration flows. Nothing in Core calls them. |

Explicitly **not in Core**: assets, Addressables, navigation, scenes, loading, network channels, sessions,
rosters, replication.

**Domain split.** Boss / Arena / Encounter are separate Core concerns coordinated by `EncounterCoordinator`,
and the split holds on the wire (`BossSnapshot` vs `ArenaSnapshot`; `BossEvent` vs `ArenaEvent`; composed by
`EncounterBaseline` — see ADR-005).

**Presentation.** `FalseGods.Protocol` stops at `FalseGods.Application`. Presentation receives only
`PresentationState` / `PresentationEvent`, so `FalseGods.UnityRuntime` never references `FalseGods.Protocol`
([Architecture.md §7](../Architecture.md)).

Full rules in [Architecture.md](../Architecture.md) and [DependencyRules.md](../DependencyRules.md).

## Alternatives considered
- **Layered-but-permissive** (shared references, discipline only) — what caused the ST debt. Rejected.
- **All ports in Core** — the "hexagonal" shape without its benefit: Core would transitively describe
  Addressables, A\*, sessions, and channels, and the port set would grow ahead of its consumers. Rejected.
- **No abstraction until needed** — risks the same reach-in coupling; instead we add the *smallest* port when a
  boundary is first crossed, and generalize only on a second use case (anti-overengineering rule).
- **Presentation consumes wire DTOs directly** — fewer types, but a protocol version bump then edits animation
  code, and single-player needs a second presentation path. Rejected in favour of one mapper.

**Enforcement.** Ports-and-adapters only survives if invalid dependencies fail a build rather than a review.
The checks, their stable rule ids, and their current status live in
[ArchitectureEnforcement.md](../ArchitectureEnforcement.md) — not here, and not in DependencyRules.

## Consequences
- More projects/assemblies (Core, Protocol, RuntimeContracts, Application, UnityRuntime, two adapters, Plugin)
  and some indirection up front.
- `FalseGods.RuntimeContracts` carries the one piece of static state in the design (the `FalseGodsIntegrations`
  broker). It is bounded to a single slot with a single reader (ADR-004); it is not, and must not become, a
  service locator that other modules read from.
- One mapping layer (`Application`) that must be kept in sync with both vocabularies — accepted, because it is
  Unity-less and socket-less, therefore unit-testable.
- Testable Unity-less Core; replaceable game/multiplayer/transport integrations; enforceable via separate
  csproj/asmdef + CI namespace scans (planned, not built).
- Prefabs are content only, bound to ports by the Composition Root — no service locators.

## Verification status
Unverified (structural) — enforced later by the checks registered in
[ArchitectureEnforcement.md §5](../ArchitectureEnforcement.md), all currently `Planned`; risks R21–R31 and R35
track leakage.
