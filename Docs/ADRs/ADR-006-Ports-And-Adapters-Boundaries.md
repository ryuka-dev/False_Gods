# ADR-006 — Ports-and-adapters module boundaries

**Status:** Accepted, implemented; mechanical enforcement still partial (see Verification status)

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
**The boundaries held while the code grew inside them** — which is the only test this ADR could ever pass. All
eight modules now carry real source, and `FalseGods.Core`, `.Protocol`, `.RuntimeContracts` and `.Application`
still build and unit-test on a machine with no game and no BepInEx installed at all.

**Enforcement is real but partial, and the split matters.** The project reference graph already makes the common
violations *not compile*: Core cannot see `UnityEngine`, `UnityRuntime` cannot see `FalseGods.Protocol`,
`RuntimeContracts` sees nothing but Core, only a base-game anti-corruption layer sees `0Harmony`
(`Integration.Sulfur`, and since [ADR-007](ADR-007-Feature-Owned-Base-Game-Adapters.md) also `Farm`), and `FalseGods.Plugin`
cannot see the ST adapter. On top of that, five checks run in CI and block the pre-push hook (the project-graph
layer of `FG-ARCH-002`, plus `FG-ARCH-003`, `-005`, `-006` and `-010`); `FG-ARCH-002`'s metadata layer and
`FG-ARCH-011` need a built adapter DLL and so run locally only. **Ten of the layers the rules name still have no
check, and no rule is enforced at every layer it names** — see
[ArchitectureEnforcement.md](../ArchitectureEnforcement.md), which is the authority on this and is kept current.

**Where the pressure actually showed up**, a year of feature work later: not in the module graph, which held, but
in deciding *which* module a new port belongs to. The rule that settled it is the one already written here — a
port lives in the innermost module that actually consumes it, and Core may not declare a port with no present
consumer. Ports whose implementation must see `Application` live in `Application`; the ones `UnityRuntime`
implements live in `RuntimeContracts`, because `UnityRuntime` cannot see `Application`. Risks R21–R31 and R35
still track leakage.
