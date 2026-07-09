# Definition of Done

*Completion gates and the AI-assisted development process for False Gods.* A feature is not "done" when it
merely works once; it is done when it meets the gates below and respects the boundaries in
[Architecture.md](Architecture.md) / [DependencyRules.md](DependencyRules.md).

All gates about runtime behaviour remain **proposed** until there is code to run them against.

## 1. Per-feature-type gates

### Core / domain feature (`FalseGods.Core`)
- No `UnityEngine`/game/network references (dependency scan passes).
- State owner, lifecycle owner, and cleanup owner are named.
- Unit-testable **without Unity**; tests exist for the authoritative decisions.
- Domain events/commands are project-owned; no direct outward calls.

### Arena loading
- Arena realized from the authored Unity prefab; only `VanillaAssetProxy` objects resolved at runtime.
- Runtime hierarchy/transforms match the exported arena manifest (parity check).
- Collision / navigation / gameplay roots registered; A* handled via `INavigationPort` (adapter).
- Full teardown releases Addressables handles and leaves nav clean.

### Boss presentation (`FalseGods.UnityRuntime`)
- Presentation makes **no** authoritative decision (damage/phase/death/target/attack).
- Runs driven only by Protocol snapshots/events; can run with simulation disabled and cannot advance state.
- All spawned visuals/handlers are unsubscribed/released on teardown.

### Single-player
- Runs with SULFUR Together **absent**; no type-load failure.
- Uses the **same** `BossSimulation` rules as a host.

### Multiplayer host / client
- Host runs `BossSimulation`; client runs presentation only.
- All ST access is via `Integration.SulfurTogether` ports; no ST/transport types elsewhere.
- Host-authoritative damage/phase/death; client applies results only.

### Duplicate & out-of-order events
- Reliable events idempotent by (`BossInstanceId`, `Sequence`) and `AttackInstanceId`.
- A duplicate/reordered event cannot duplicate projectiles, adds, rewards, mechanisms, or damage.

### Join-in-progress
- A late client reconstructs current phase/state/attack/weak-points/adds/arena from **one baseline snapshot**
  before normal event processing.

### Teardown
- On exit: simulation stopped, every replication/event handler unsubscribed, resources + Addressables
  released, nav restored, boss/arena state cleared — nothing survives into the next level.

### Optional SULFUR Together absence
- The base plugin loads and plays single-player with the ST integration assembly missing.

### Architecture dependency scan
- The dependency matrix ([DependencyRules.md §1–§3](DependencyRules.md)) holds; no forbidden namespace leaked.

## 2. AI-assisted development process (§12)

### Before implementing a feature — identify:
- the owning module;
- required interfaces / ports;
- allowed dependencies;
- state owner; lifecycle owner; cleanup owner;
- single-player behaviour; host behaviour; client behaviour;
- testing strategy.

### During implementation
- Do **not** introduce a cross-layer direct call merely because it is easier.
- If the correct boundary does not exist, **stop and add the smallest appropriate abstraction** (a port).
- Do not silently add: global static state; service locators; direct transport calls; concrete SULFUR
  Together references outside its adapter; Harmony patches outside `Integration.Sulfur`; scene-name/timing
  heuristics; duplicate sources of truth.

### Before considering a feature complete — review:
- dependency direction;
- event unsubscription;
- resource ownership;
- teardown;
- optional-integration behaviour;
- duplicate network-event handling;
- absence of direct transport coupling;
- test coverage of pure logic.

## 3. Anti-overengineering rule (§13)

Boundary enforcement is **not** a mandate to build a speculative universal boss framework. Use a
vertical slice:

1. minimum module skeleton;
2. arena PoC;
3. one temporary `BossSimulation`;
4. one presentation;
5. single-player test;
6. transport-neutral snapshots/events;
7. connect through the SULFUR Together adapter;
8. host/client validation;
9. extract shared abstractions **only** from demonstrated repetition.

> Generalize after a second real use case reveals a stable common structure — not before.
