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
- **Single-purpose modules**, and **a port lives in the innermost module that actually consumes it** — not
  in Core by default.
- **Optional multiplayer** — False Gods runs in vanilla single-player with the SULFUR Together adapter assembly
  absent, and the base plugin never takes a CLR dependency on it.
- **Testable pure gameplay logic** — the boss/encounter domain runs without launching Unity.
- **No transport-specific boss or arena code**; **presentation never sees a wire DTO**; **no implicit
  cross-module access through static globals**.

### Why (the SULFUR Together lesson)

SULFUR Together accumulated system debt because boundaries came late: transport, session, gameplay
networking, boss sync, scene transitions, and UI became coupled, and adding Steam P2P exposed how far
LiteNetLib/Steam/manager types had leaked. Features were built by reaching directly into managers, statics,
Harmony patches, and concrete message/transport types, so later changes rippled into unrelated systems and a
second transport was expensive. False Gods front-loads the boundaries to avoid that.

## 2. Modules & dependency direction

Dependencies point **inward** (arrows = "depends on"):

```
FalseGods.Plugin  (Composition Root, BepInEx entry)
  │
  ├──►  FalseGods.UnityRuntime  ──┐
  ├──►  FalseGods.Application  ───┤
  ├──►  FalseGods.Integration.Sulfur  (always present — the base game always is)
  │                               │
  │                               ▼
  │                     FalseGods.RuntimeContracts  ──►  FalseGods.Core
  │                                    ▲                       ▲
  │                                    │                       │
  │                     FalseGods.Protocol  ───────────────────┘
  │                          (referenced by Application, not by UnityRuntime)
  │
  ╎  ✗ NO compile-time reference (this is a rule, not an omission)
  ╎
  ╌╌►  FalseGods.Integration.SulfurTogether
        OPTIONAL, loaded at runtime. References RuntimeContracts + Core + ST/LiteNetLib/Steam.
        Registers itself into the Composition Root through IIntegrationRegistry.
```

- **Core** knows nothing outer, and holds **only** the domain plus the few abstractions the domain itself calls.
- **Protocol** references only Core. It is the wire contract, and it stops at `FalseGods.Application`.
- **RuntimeContracts** is a small, stable, dependency-light assembly (Core only) holding the presentation
  contracts, the outer capability ports, and the optional-integration registration seam. It is the *only*
  False Gods assembly the optional ST adapter needs.
- **Application** orchestrates the encounter and arena flows, owns the game/Unity-facing ports, and is the one
  place that maps `Protocol` DTOs ⇄ `PresentationState`/`PresentationEvent`.
- **UnityRuntime** references Core + RuntimeContracts + UnityEngine, and **never** `FalseGods.Protocol`.
- **`FalseGods.Plugin` never references `FalseGods.Integration.SulfurTogether`.** The arrow points the other
  way: the optional adapter depends on the stable contract. See §4.

## 3. Responsibility table

| Module | Owns | Must NOT contain | Depends on |
|---|---|---|---|
| **FalseGods.Core** | Boss & encounter domain: `BossSimulation`, `ArenaSimulation`, `EncounterCoordinator`, domain commands/events, stable id value types, phase/health/weak-point rules, authoritative decisions, encounter-completion rules. Abstractions **the domain itself calls**: `ISimulationClock`, `IAuthoritativeRandom`, `IEncounterParticipantQuery` | `MonoBehaviour`/`GameObject`/`Transform`, Unity vectors (except behind project-owned value types), `Time.time`, `UnityEngine.Random`, Harmony, SULFUR classes, networking APIs, Addressables, A* types — **and** asset / Addressables / navigation / scene / loading / channel / session / roster / replication ports | nothing outer |
| **FalseGods.Protocol** | Transport-neutral wire DTOs: `BossSnapshot`, `ArenaSnapshot`, `BossEvent`, `ArenaEvent`, `EncounterBaseline`, `ArenaManifest`, stable ids, protocol versions, sequence numbers, sim ticks, serialization contracts, duplicate-suppression rules | LiteNetLib/Steamworks types, concrete sockets, `CoopConnection`, ST internal messages, Unity presentation objects, presentation contracts | Core |
| **FalseGods.RuntimeContracts** | The stable seam shared with optional adapters. Presentation contracts (`PresentationState`, `PresentationEvent`, `IEncounterPresentation`); outer capability ports (`IPlayerRoster`, `IMultiplayerSession`, `IEncounterChannel`, `IArenaLockdownPort`, `IRemoteNpcActivationPort`, `IEncounterReadyGate`); transport-neutral carriers (`SessionPeerId`, `EncodedPayload`, `MessageDelivery`); `IIntegrationRegistry` / `IFalseGodsIntegration`; `ILogger` | `FalseGods.Protocol` DTOs, UnityEngine, ST/LiteNetLib/Steamworks, any concrete implementation | Core |
| **FalseGods.Application** | Encounter & arena orchestration: `EncounterHost`, `ArenaLoadFlow`, ready-gate policy, teardown ordering, replication codecs over `IEncounterChannel`, and the **mappers** `Protocol DTO → PresentationState/PresentationEvent` and `domain state → PresentationState/PresentationEvent`. Ports whose consumer lives here: `IEncounterReplication`, `IDamagePort`, `ISpawnPort`, `INavigationPort`, `IArenaAssetProvider`, `IVanillaAssetProvider`, `IArenaRealization`, `ISceneLifecycleEvents` | UnityEngine, Harmony, ST/LiteNetLib/Steamworks, presentation implementations | Core, Protocol, RuntimeContracts |
| **FalseGods.UnityRuntime** | Presentation & Unity realization: prefab instantiation, renderers/sprites, animation, VFX, audio, lighting, interpolation, arena-prefab realization, AssetBundle lifecycle, original-content loading, Unity authoring components. Implements `IEncounterPresentation` / `IArenaRealization` | authoritative damage / attack selection / phase / death / authority / RNG outcomes; **any `FalseGods.Protocol` type** | Core, RuntimeContracts, UnityEngine |
| **FalseGods.Integration.Sulfur** | Anti-corruption layer to the base game: `Unit.ReceiveDamage`, player lookup, vanilla loot/status, level transitions, A* Recast, `AiAgent`, Addressables for vanilla assets, Harmony, reflection, private-field access, base-game lifecycle hooks. Single-player implementations of `IPlayerRoster` / `IArenaLockdownPort` / `IRemoteNpcActivationPort` | boss mechanics, wire protocol | Core, Protocol, RuntimeContracts, Application, game DLLs |
| **FalseGods.Integration.SulfurTogether** *(optional, runtime-loaded)* | Multiplayer adapter: host/client detection, session lifecycle, peer-id mapping, message registration, reliable/unreliable channel mapping mapped onto `EncodedPayload`/`MessageDelivery`, arena-ready ACK transport, join/leave, ST managers, ST reflection. Registers its capabilities through `IIntegrationRegistry` | boss/arena gameplay decisions; `FalseGods.Protocol` DTOs; `FalseGods.Application` internals | RuntimeContracts, Core, ST + transport DLLs |
| **FalseGods.Plugin** (Composition Root) | BepInEx entry; construction and wiring of Core/Application/UnityRuntime/`Integration.Sulfur`; the `IIntegrationRegistry` instance; startup & shutdown ordering; graceful degradation when no multiplayer integration registers | substantive boss mechanics; **any reference to `Integration.SulfurTogether`** | Core, Protocol, RuntimeContracts, Application, UnityRuntime, Integration.Sulfur, BepInEx |

## 4. Composition Root, optional integration, and the three compositions

`FalseGods.Plugin` is the only place that constructs concrete implementations and injects them through ports.

### 4.1 The optional-integration seam (no hard ST dependency)

`FalseGods.Plugin` **must not** hold a CLR dependency on `FalseGods.Integration.SulfurTogether`: not an assembly
reference, not a type in a field/parameter/return signature, not a `typeof(...)`, and not a type touched on a
static-initialization path. If the adapter assembly is missing, the base plugin must load and play single-player
with no `TypeLoadException` / `FileNotFoundException` (RiskList R20/R29).

The dependency therefore points **from the optional adapter to the stable contract**:

```
FalseGods.Plugin  ──creates──►  IIntegrationRegistry   (FalseGods.RuntimeContracts)
                                        ▲
                                        │ Register(IFalseGodsIntegration)
                                        │
              FalseGods.Integration.SulfurTogether  (optional assembly, loaded only if present)
```

Two acceptable mechanisms, in order of preference:

1. **Self-registering optional plugin (preferred).** The adapter ships as its own BepInEx plugin with a *soft*
   dependency on the False Gods plugin and on SULFUR Together. On load it calls
   `IIntegrationRegistry.Register(...)`, handing over implementations of the RuntimeContracts capability ports.
   The base plugin never mentions the adapter; if the DLL is absent nothing registers and the encounter runs in
   the single-player composition.
2. **Reflective discovery.** The base plugin probes for a known assembly/type name, and if found invokes a
   parameterless factory that returns `IFalseGodsIntegration`. Everything reflective stays in one small,
   exception-guarded discovery class; failure degrades to single-player, never to a crash.

Either way, the Composition Root only ever sees `IFalseGodsIntegration` and the RuntimeContracts ports.

### 4.2 SULFUR Together's real surface: mostly `internal`

Do **not** assume the adapter can simply reference ST and call its systems. Measured against the current ST
source, ST declares roughly **189 `internal` types to 38 `public`**, and there is **no `[InternalsVisibleTo]`**.
The systems False Gods wants are on the internal side:

| ST type | Visibility |
|---|---|
| `CoopConnection` | `internal static` |
| `ArenaLockdownManager` | `internal static` |
| `NetBossEncounterManager` | `internal static` |
| `NetLoadBarrier` | `internal static` |
| `RemotePlayerRegistryManager` | `internal sealed` |
| `NetService` | `public` |

So the ST adapter must either (a) reach these through **reflection**, accepting the version-fragility and
guarding every call, or (b) depend on SULFUR Together exposing a **public integration bridge** for the
capabilities False Gods needs. Option (b) is the preferred long-term path and is a coordination item with the ST
project; option (a) is what is available today. Either way the fragility is confined to
`FalseGods.Integration.SulfurTogether` and surfaces to the rest of the mod as "capability registered" or
"capability unavailable".

### 4.3 The three compositions

- **Single-player** (no multiplayer integration registered): Core (`BossSimulation`+`ArenaSimulation`+
  `EncounterCoordinator`) + `Application` + `UnityRuntime` presentation + `Integration.Sulfur` port
  implementations. Replication is a **no-op**; the ready gate resolves immediately for the single local peer.
  The same `BossSimulation` rules run here as on a host.
- **Multiplayer host**: single-player composition **plus** a registered `IFalseGodsIntegration` supplying
  `IMultiplayerSession` / `IEncounterChannel` / `IPlayerRoster` / `IArenaLockdownPort` / `IEncounterReadyGate`.
  `Application` runs `IEncounterReplication` on top of the channel and broadcasts snapshots and discrete events.
- **Multiplayer client**: `UnityRuntime` presentation only, driven by `Application`'s inbound mapper; **no**
  `BossSimulation`. Client presentation applies host-authoritative results.

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

**The split holds on the wire too.** Arena mechanism state is *not* carried inside boss state or the boss event
stream. `FalseGods.Protocol` keeps `BossSnapshot` and `ArenaSnapshot` separate, `BossEvent` and `ArenaEvent`
separate, and composes both — with the encounter's own state — into an `EncounterBaseline` for late join and
recovery ([ADR-005](ADRs/ADR-005-Snapshot-And-Discrete-Event-Replication.md),
[MultiplayerLoadingContract.md §5.7](MultiplayerLoadingContract.md)). A boss reusable across arenas cannot have
one arena's mechanism enum welded into its snapshot.

## 6. Ports (project-owned capability interfaces)

A port is declared by **the innermost module that actually consumes it**, and implemented by an outer adapter.
Core is not a dumping ground for every interface in the project: a port belongs in Core only if the *domain
logic itself* calls it. And **no port is created without a present consumer** (RiskList R31) — the list below
grows as the vertical slice demands, it is not a pre-built framework.

| Declared in | Ports | Rationale |
|---|---|---|
| **Core** | `ISimulationClock`, `IAuthoritativeRandom`, `IEncounterParticipantQuery` | `BossSimulation` advances on ticks, makes authoritative random decisions, and picks targets among participants. These are called from inside domain logic. |
| **RuntimeContracts** | `IPlayerRoster`, `IMultiplayerSession`, `IEncounterChannel`, `IArenaLockdownPort`, `IRemoteNpcActivationPort`, `IEncounterReadyGate`, `IEncounterPresentation`, `ILogger` | Implemented by *either* `Integration.Sulfur` (single-player) *or* the optional ST adapter, so they must live in the small assembly both can reference. |
| **Application** | `IEncounterReplication`, `IDamagePort`, `ISpawnPort`, `INavigationPort`, `IArenaAssetProvider`, `IVanillaAssetProvider`, `IArenaRealization`, `ISceneLifecycleEvents` | Consumed by the orchestration flows; implemented by `Integration.Sulfur` / `UnityRuntime`. Nothing in Core calls them. |

Explicitly **not in Core**: assets, Addressables, navigation, scenes, loading, network channels, sessions,
rosters, and replication.

Flow examples:

```
BossSimulation emits DamageRequested   → Application → Integration.Sulfur executes Unit.ReceiveDamage
BossSimulation emits SpawnRequested    → Application → Integration.Sulfur chooses the SULFUR spawn operation
Application encodes BossEvent (Protocol) → EncodedPayload
                                       → Integration.SulfurTogether ships it on ST's transport
```

## 7. Presentation is driven by presentation contracts, never by wire DTOs

`BossPresentation` must not accept `BossSnapshot`, `BossEvent`, `ArenaSnapshot`, `ArenaEvent`,
`EncounterBaseline`, or any other `FalseGods.Protocol` type. Wire DTOs exist to survive a network; presentation
contracts exist to drive renderers. Coupling them means a protocol version bump edits animation code.

```
                        ┌──────────────────────────── multiplayer client ─┐
   Network DTO          │ BossSnapshot / ArenaSnapshot / BossEvent /      │
   (FalseGods.Protocol) │ ArenaEvent / EncounterBaseline                  │
                        └───────────────────────┬─────────────────────────┘
                                                │
   ┌──────────── single-player & host ──────────┤
   │ Core domain state + domain events          │
   └───────────────────────┬────────────────────┘
                           ▼                    ▼
              FalseGods.Application — replication / presentation mapper
                           │
                           ▼
              PresentationState / PresentationEvent   (FalseGods.RuntimeContracts)
                           │
                           ▼
              IEncounterPresentation  →  BossPresentation / ArenaPresentation
                           (FalseGods.UnityRuntime)
```

- `PresentationState` / `PresentationEvent` are **project-owned, transport-agnostic** presentation contracts.
  They carry what a renderer needs (pose, animation state, telegraph timing, phase visual id, weak-point visual
  state, mechanism visual state) and nothing a socket needs (sequence numbers, protocol version, delivery mode).
- Single-player and multiplayer converge on **one** presentation entry point: the local simulation's output and
  the remote wire DTOs are each mapped, then enter `IEncounterPresentation` identically. Presentation cannot
  tell the difference, and that is the point — it is the same code path in both modes.
- The mapper is the only place that knows both vocabularies, it lives in `Application`, and it is unit-testable
  without Unity and without a socket.
- Presentation still decides **nothing**: not damage, not phase, not death, not target, not attack outcome.

## 8. Unity prefab boundary

Arena/boss prefabs are **content and presentation definitions, not service containers**. They may contain
renderers, sprites, rigs, animation, audio, VFX, marker transforms, authoring metadata, collision, navigation
hints, spawn points, and mechanism bindings. They must **not** store or discover `NetService.Instance`,
LiteNetLib connections, Steamworks objects, ST managers, global boss managers, packet callbacks, or
authoritative combat decisions. After instantiation, the Composition Root/runtime **binds** the prefab view to
project-owned interfaces. Avoid service-locator patterns where arbitrary components fetch global managers.

## 9. Ownership, lifecycle & teardown

- Every mutable state has **one** owning module. Presentation, proxies, and caches are never authoritative.
- The **Composition Root owns startup/shutdown ordering**; each module owns cleanup of what it created.
- On encounter/arena exit: stop simulation, unsubscribe every replication/event handler, release presentation
  resources and Addressables handles, clean the arena's own contributions to the active level's A\* graph, and
  clear boss/arena state — no residue into the next level (see
  [CollisionAndNavigationProposal.md §4.6](CollisionAndNavigationProposal.md) and
  [MultiplayerLoadingContract.md §5.11](MultiplayerLoadingContract.md)).
- Cleanup for a subsystem lives with that subsystem's owner, not split across modules (RiskList R30).

## 10. Relationship to vanilla SULFUR types

`BossFightHelper`, `BossPhase`, and `IBossEncounterAdapter` are **reverse-engineering references** for how the
vanilla game behaves — they are **not** base classes or dependencies for original bosses. Original bosses are
built from `FalseGods.Core` types; any interaction with vanilla combat/lifecycle happens through
`Integration.Sulfur` ports.
