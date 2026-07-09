# Architecture

*The module boundaries, dependency direction, and ownership rules for False Gods.* This document is the
authority on **structure**; the wire/replication contract lives in
[OriginalBossNetworkingArchitecture.md](OriginalBossNetworkingArchitecture.md), the loading flow in
[MultiplayerLoadingContract.md](MultiplayerLoadingContract.md), and the enforcement detail in
[DependencyRules.md](DependencyRules.md).

All module/interface layouts here are **proposed** until implementation; exact signatures are deferred.
No code is created by this document.

## 1. Goals

Established **before** implementation, so invalid dependencies are hard to write:

- **Explicit ownership** of every piece of state and lifecycle.
- **Strict inward dependency direction** — inner modules never reference outer technologies.
- **Replaceable integrations** behind project-owned ports (base game, multiplayer, Unity).
- **Single-purpose modules.**
- **Optional multiplayer** — False Gods runs in vanilla single-player with SULFUR Together absent.
- **Testable pure gameplay logic** — the boss/encounter domain runs without launching Unity.
- **No transport-specific boss or arena code**; **no implicit cross-module access through static globals**.

### Why (the SULFUR Together lesson)

SULFUR Together accumulated system debt because boundaries came late: transport, session, gameplay
networking, boss sync, scene transitions, and UI became coupled, and adding Steam P2P exposed how far
LiteNetLib/Steam/manager types had leaked. Features were built by reaching directly into managers, statics,
Harmony patches, and concrete message/transport types, so later changes rippled into unrelated systems and a
second transport was expensive. False Gods front-loads the boundaries to avoid that.

## 2. Modules & dependency direction

Dependencies point **inward** (arrows = "depends on"):

```
FalseGods.Plugin  ──►  FalseGods.UnityRuntime  ──►  FalseGods.Protocol  ──►  FalseGods.Core
      │                                                                          ▲   ▲
      │  (Composition Root references the adapters and wires them to ports)      │   │
      ├───────────────►  FalseGods.Integration.Sulfur  ─────────────────────────┘   │
      └───────────────►  FalseGods.Integration.SulfurTogether  ────────────────────┘
                          (OPTIONAL — the only module aware of ST / LiteNetLib / Steam)
```

- **Core** knows nothing outer. **Protocol** references only Core. **UnityRuntime** references Protocol+Core
  and UnityEngine. **Integration.*** reference Core/Protocol (+ their external tech) and implement Core ports.
- **Only the Composition Root (`FalseGods.Plugin`)** references the integration adapters and wires them in.
  No inner module references an adapter.

## 3. Responsibility table

| Module | Owns | Must NOT contain | Depends on |
|---|---|---|---|
| **FalseGods.Core** | Boss & encounter domain: `BossSimulation`, `ArenaSimulation`, `EncounterCoordinator`, domain commands/events, deterministic ids, phase/health/weak-point rules, authoritative decisions, encounter-completion rules, **port interfaces**, a simulation-clock abstraction | `MonoBehaviour`/`GameObject`/`Transform`, Unity vectors (except behind project-owned value types), `Time.time`, `UnityEngine.Random`, Harmony, SULFUR classes, networking APIs, Addressables, A* types | nothing outer |
| **FalseGods.Protocol** | Transport-neutral DTOs: `BossSnapshot`, `BossDiscreteEvent`, `ArenaManifest`, stable ids, protocol versions, sequence numbers, sim ticks, serialization contracts, duplicate-suppression rules | LiteNetLib/Steamworks types, concrete sockets, `CoopConnection`, ST internal messages, Unity presentation objects | Core |
| **FalseGods.UnityRuntime** | Presentation & Unity realization: prefab instantiation, renderers/sprites, animation, VFX, audio, lighting, interpolation, arena-prefab realization, AssetBundle lifecycle, original-content loading, presentation bindings, Unity authoring components | authoritative damage / attack selection / phase / death / authority / RNG outcomes | Protocol, Core, UnityEngine |
| **FalseGods.Integration.Sulfur** | Anti-corruption layer to the base game: `Unit.ReceiveDamage`, player lookup, vanilla loot/status, level transitions, A* Recast, `AiAgent`, Addressables for vanilla assets, Harmony, reflection, private-field access, base-game lifecycle hooks | boss mechanics, wire protocol | Core, Protocol, game DLLs |
| **FalseGods.Integration.SulfurTogether** | Optional multiplayer adapter: host/client detection, session lifecycle, player-id mapping, message registration, reliable/unreliable channel mapping, arena-ready integration, join/leave, baseline delivery, ST managers, ST reflection | boss/arena gameplay decisions | Core, Protocol, ST + transport DLLs |
| **FalseGods.Plugin** (Composition Root) | BepInEx entry; integration detection; service creation; choosing single-player vs multiplayer adapters; wiring simulation/presentation/game-integration/replication; startup & shutdown ordering | substantive boss mechanics | all of the above |

## 4. Composition Root & the three compositions

`FalseGods.Plugin` is the only place that constructs concrete implementations and injects them into
Core/UnityRuntime through ports.

- **Single-player** (ST absent): Core (`BossSimulation`+`ArenaSimulation`+`EncounterCoordinator`) +
  `UnityRuntime` presentation + `Integration.Sulfur` port implementations. Replication is a **no-op**. The
  same `BossSimulation` rules run here as on a host.
- **Multiplayer host**: single-player composition **plus** `Integration.SulfurTogether` providing
  `IEncounterReplication`/`IEncounterChannel`/`IMultiplayerSession`; the host simulation broadcasts snapshots
  and discrete events.
- **Multiplayer client**: `UnityRuntime` presentation only, driven by `Integration.SulfurTogether` feeding
  Protocol snapshots/events; **no** `BossSimulation`. Client presentation applies host-authoritative results.

The host **adds replication** to the single-player composition; it does not swap in a different boss
implementation.

## 5. Boss / Arena / Encounter separation

`BossSimulation` owning arena mechanisms would couple every boss to one arena. Split into three:

- **`BossSimulation`** — boss domain only: state machine, attack decisions, movement intent, phase rules,
  health, weak points, stagger, boss-specific domain events.
- **`ArenaSimulation` / `ArenaStateMachine`** — arena gameplay state: gates, hazards, destructible/phase
  objects, safe/unsafe regions, gameplay-relevant lighting-state requests, arena mechanisms, encounter exit
  state.
- **`EncounterCoordinator`** — evaluates encounter rules and translates between the two via project-owned
  commands/events:

```
BossSimulation emits PhaseChanged(2)
    → EncounterCoordinator evaluates encounter rules
        → ArenaSimulation receives ActivateMechanismGroup("phase_2")
```

The boss never locates Unity arena objects or calls mechanism components directly; the arena never inspects
private boss fields. This separation makes possible: one boss in multiple arenas, one arena hosting multiple
bosses, a Boss Rush / test arena, arena-less boss tests, and Unity-less `BossSimulation` tests.

## 6. Ports (project-owned capability interfaces)

Core declares the capability it needs; an outer adapter implements it. Core never reaches out to a concrete
global manager. Proposed ports (signatures deferred):

```
IGameClock            IAuthoritativeRandom   IPlayerRoster        IPlayerQuery
IDamagePort           ISpawnPort             INavigationPort      IAssetProvider
IVanillaAssetProvider IArenaRuntime          IArenaLockdownPort   IEncounterReplication
IMultiplayerSession   IEncounterChannel      ILogger
```

Flow examples:

```
BossSimulation emits DamageRequested   → Integration.Sulfur executes Unit.ReceiveDamage
BossSimulation emits SpawnRequested    → host-side runtime chooses the concrete SULFUR spawn operation
BossReplication publishes BossDiscreteEvent (transport-neutral)
                                       → Integration.SulfurTogether serializes and sends it
```

## 7. Unity prefab boundary

Arena/boss prefabs are **content and presentation definitions, not service containers**. They may contain
renderers, sprites, rigs, animation, audio, VFX, marker transforms, authoring metadata, collision, navigation
hints, spawn points, and mechanism bindings. They must **not** store or discover `NetService.Instance`,
LiteNetLib connections, Steamworks objects, ST managers, global boss managers, packet callbacks, or
authoritative combat decisions. After instantiation, the Composition Root/runtime **binds** the prefab view to
project-owned interfaces. Avoid service-locator patterns where arbitrary components fetch global managers.

## 8. Ownership, lifecycle & teardown

- Every mutable state has **one** owning module (CLAUDE.md §5). Presentation/proxies/caches are never
  authoritative.
- The **Composition Root owns startup/shutdown ordering**; each module owns cleanup of what it created.
- On encounter/arena exit: stop simulation, unsubscribe every replication/event handler, release presentation
  resources and Addressables handles, restore nav, and clear boss/arena state — no residue into the next level
  (see report 4.6 and [MultiplayerLoadingContract.md §5.11](MultiplayerLoadingContract.md)).
- Cleanup for a subsystem lives with that subsystem's owner, not split across modules (RiskList R30).

## 9. Relationship to vanilla SULFUR types

`BossFightHelper`, `BossPhase`, and `IBossEncounterAdapter` are **reverse-engineering references** for how the
vanilla game behaves — they are **not** base classes or dependencies for original bosses. Original bosses are
built from `FalseGods.Core` types; any interaction with vanilla combat/lifecycle happens through
`Integration.Sulfur` ports.
