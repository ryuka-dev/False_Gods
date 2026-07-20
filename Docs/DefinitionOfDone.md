# Definition of Done

*Completion gates and the development process for False Gods.* A feature is not "done" when it
merely works once; it is done when it meets the gates below and respects the boundaries in
[Architecture.md](Architecture.md) / [DependencyRules.md](DependencyRules.md). How those boundaries are checked
mechanically — and which checks exist yet — lives in
[ArchitectureEnforcement.md](ArchitectureEnforcement.md).

All gates about runtime behaviour remain **proposed** until there is code to run them against, and every
architecture check named below is currently `Planned`.

## 1. Per-feature-type gates

### Core / domain feature (`FalseGods.Core`)
- No `UnityEngine`/game/network references (dependency scan passes).
- No `FalseGods.Protocol` or `FalseGods.RuntimeContracts` reference.
- **No asset / Addressables / navigation / scene / loading / channel / session / roster / replication port** is
  declared here; each port sits in the innermost module that actually consumes it
  ([Architecture.md §6](Architecture.md)).
- Every port added has a **present consumer** — no speculative interfaces.
- State owner, lifecycle owner, and cleanup owner are named.
- Unit-testable **without Unity**; tests exist for the authoritative decisions.
- Domain events/commands are project-owned; no direct outward calls.

### Arena loading
- Arena realized from the authored Unity prefab; only `VanillaAssetProxy` objects resolved at runtime.
- Runtime hierarchy/transforms match the exported arena manifest (parity check).
- Collision / navigation / gameplay roots registered; A* handled via `INavigationPort` (adapter).
- The flow follows the canonical order: **load locally → resolve assets and navigation → report `ArenaReady`
  with identity + content hash → host validates all required peers → seal and teleport → publish
  `EncounterBaseline` → start simulation** ([MultiplayerLoadingContract.md §5.3](MultiplayerLoadingContract.md)).
  Players are **never** placed before the ready gate passes.
- `ContentHash` is computed from the canonical authored inputs, in the canonical order, with the canonical
  quantization ([MultiplayerLoadingContract.md §5.2.1](MultiplayerLoadingContract.md)), and is stamped with a
  `ContentHashSchemaVersion`. It contains **no** `InstanceID`, enumeration order, memory address, local path, or
  A\* scan output. Two peers on different machines produce byte-identical hashes for the same content.
- Full teardown releases Addressables handles and removes every node, off-mesh link, and graph modifier the
  arena contributed to the **active level's** A\* graph — it does not rely on a future level change to hide a
  leak.

### Boss presentation (`FalseGods.UnityRuntime`)
- Presentation makes **no** authoritative decision (damage/phase/death/target/attack).
- Presentation **never sees a wire DTO**: no `BossSnapshot`, `ArenaSnapshot`, `BossEvent`, `ArenaEvent`, or
  `EncounterBaseline` appears in its signatures, and `FalseGods.UnityRuntime` does not reference
  `FalseGods.Protocol`.
- Presentation is driven only by `PresentationState` / `PresentationEvent`, produced by the `Application`
  mapper; the **same** presentation entry point serves single-player and multiplayer.
- Can run with simulation disabled and cannot advance state.
- All spawned visuals/handlers are unsubscribed/released on teardown.

### Single-player
- Runs with SULFUR Together **and the ST adapter assembly** absent; no type-load failure.
- Uses the **same** `BossSimulation` rules as a host, and the same presentation entry point.

### Multiplayer host / client
- Host runs `BossSimulation`; client runs presentation only.
- The main loading and combat flows name **only** project-owned ports — `IPlayerRoster`, `IArenaLockdownPort`,
  `IMultiplayerSession`, `IEncounterChannel`, `IEncounterReadyGate`, `IRemoteNpcActivationPort`. No
  `GameManager.Players`, `ArenaLockdownManager`, `NetService`, `CoopConnection`, or other ST/game internal
  appears outside `Integration.*`.
- Host-authoritative damage/phase/death; client applies results only.

### Ready gate (fail closed)
- The boss does not start unless **every required peer** has reported `ArenaReady` with a matching `ArenaId`,
  `ArenaVersion`, `ContentHash`, and protocol/bundle version, under a matching `ContentHashSchemaVersion`.
- A `ContentHashSchemaVersion` mismatch refuses **without comparing hashes**.
- Load failure, content mismatch, timeout, and disconnect each have a defined, **fail-closed** outcome. There is
  no "start anyway after a timeout" path.

### Duplicate & out-of-order events
- Boss and arena event streams are **separate** and each is idempotent by (`EncounterId`, stream, `Sequence`);
  boss attack effects are additionally idempotent by `AttackInstanceId`.
- A duplicate/reordered event cannot duplicate projectiles, adds, rewards, mechanisms, or damage.

### Join-in-progress
- A late client reconstructs the current encounter from **one `EncounterBaseline`** — which composes
  `BossSnapshot`, `ArenaSnapshot`, and the encounter's own state — before normal event processing.

### Teardown
- On exit: simulation stopped, every replication/event handler unsubscribed, resources + Addressables
  released, the arena's A\* contributions removed from the active graph, boss/arena state cleared — nothing
  survives into the next level.

### Optional SULFUR Together absence
- The base plugin loads and plays single-player with the ST integration assembly missing.
- `FalseGods.Plugin.dll` carries **no metadata reference** to `FalseGods.Integration.SulfurTogether`
  (`FG-ARCH-002`).
- The adapter is a **companion BepInEx plugin** with a hard `[BepInDependency]` on the base plugin's GUID, and
  it registers through `FalseGodsIntegrations` ([ADR-004](ADRs/ADR-004-Optional-Sulfur-Together-Adapter.md)).
  It references `FalseGods.RuntimeContracts`, never `FalseGods.Plugin`.
- Registering twice leaves the **first** integration authoritative; disposing the registration token clears the
  slot and tears down the multiplayer composition.

### Architecture dependency scan
- The dependency matrix ([DependencyRules.md §1–§3](DependencyRules.md)) holds; no forbidden namespace leaked.
- Reflection stays on the right side of the split: `Integration.Sulfur` reflects into game internals,
  `Integration.SulfurTogether` reflects into ST internals, nobody else reflects into either, and Harmony patches
  exist only in `Integration.Sulfur` (`FG-ARCH-006`).
- Every architecture check cited a rule id from
  [ArchitectureEnforcement.md §5](ArchitectureEnforcement.md) (`FG-ARCH-010`).

## 2. Development process

### Before implementing a feature — identify:
- the owning module;
- required interfaces / ports, **and the innermost module that actually consumes each one**;
- allowed dependencies;
- state owner; lifecycle owner; cleanup owner;
- single-player behaviour; host behaviour; client behaviour;
- testing strategy.

### During implementation
- Do **not** introduce a cross-layer direct call merely because it is easier.
- If the correct boundary does not exist, **stop and add the smallest appropriate abstraction** (a port).
- Do not silently add: global static state; service locators; direct transport calls; concrete SULFUR
  Together references outside its adapter; a reference from `FalseGods.Plugin` to the ST adapter; a wire DTO in
  a presentation signature; a Core port with no consumer; Harmony patches outside `Integration.Sulfur`;
  scene-name/timing heuristics; duplicate sources of truth.

### Before considering a feature complete — review:
- dependency direction;
- port placement (is it in the innermost consuming module? does it have a consumer?);
- event unsubscription;
- resource ownership;
- teardown;
- optional-integration behaviour (does the plugin still load with the adapter deleted?);
- duplicate network-event handling;
- absence of direct transport coupling;
- absence of wire DTOs in presentation;
- test coverage of pure logic.

## 3. Anti-overengineering rule

Boundary enforcement is **not** a mandate to build a speculative universal boss framework. Use a
vertical slice:

1. minimum module skeleton;
2. arena PoC;
3. one temporary `BossSimulation`;
4. one presentation, driven through `PresentationState`/`PresentationEvent`;
5. single-player test (mapper: domain → presentation contracts);
6. transport-neutral snapshots/events (`BossSnapshot`, `ArenaSnapshot`, `BossEvent`, `ArenaEvent`,
   `EncounterBaseline`) and the mapper: wire DTO → presentation contracts;
7. connect through the optional SULFUR Together adapter (registered, never referenced);
8. host/client validation;
9. extract shared abstractions **only** from demonstrated repetition.

> Generalize after a second real use case reveals a stable common structure — not before.

When that second case arrives, the shared structure belongs in the **boss/encounter definition as data** — a
registry entry pairing an arena, a boss tuning, and its phase-to-arena-command rules — **not** as another `case`
in `EncounterCoordinator.Process` or another branch in `BossSimulation.SelectAttack`. Those two are the
load-bearing seams for a second boss; grow them into data-driven definitions, do not special-case them.
