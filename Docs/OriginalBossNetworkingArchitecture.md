# 9. Original Boss Networking Architecture

*A general, host-authoritative, network-native replication model for **all** False Gods bosses.* This is the
architecture that [MultiplayerLoadingContract.md §5.4–5.9](MultiplayerLoadingContract.md) references; the
loading/arena contract lives there, the boss internals live here.

All runtime behaviour is **proposed/unverified** until exercised by the Original Boss Networking Vertical
Slice ([MinimalProofOfConceptPlan.md, Phase B](MinimalProofOfConceptPlan.md)). Reuse claims are grounded in
SULFUR Together's round-1-audited systems (see `../Decompiled/` and the ST docs cited inline).

## 9.1 Why not just reuse the vanilla boss adapter?

SULFUR Together's `IBossEncounterAdapter` / `NetBossEncounterManager` / `BossReflect` (msgs 28–43) exist to
retrofit networking onto **vanilla** bosses whose attack/phase/presentation logic is scattered across game
classes, animators, coroutines, events, and private state, reachable only via Harmony + reflection + state
mirroring (`BossAuthority.md`, `HostDrivenProxyPlan.md`). Each vanilla boss needs bespoke adaptation and can
never reach fully clean sync.

False Gods **owns** its bosses end-to-end, so it can do what the vanilla layer cannot: keep one authoritative
simulation with an explicit, replicated state and event contract from day one. That yields more complete,
more verifiable synchronization than any adapter over code that was never designed for it.

## 9.2 The three layers

| Layer | Runs on | Owns | Never does |
|---|---|---|---|
| **BossSimulation** | single-player **and** host only | boss domain only: phase, state machine, target/attack selection, movement intent, damage, health, weak points, stagger, summons, death/completion, boss-specific domain events | render, guess on clients, own arena mechanisms |
| **BossPresentation** | every machine | renderers/sprites, animation, particles, audio, telegraphs, camera FX, non-authoritative visual projectiles, interpolation | decide damage, phase, death, target, or attack outcome |
| **BossReplication** | active only when SULFUR Together is present | host snapshots, discrete events, sequence numbers, sim tick/time, interpolation targets, duplicate suppression, late-join restore, desync detection, recovery snapshots | run gameplay logic |

The layers are **assembly/type-isolated** so that `BossReplication` can be absent (single-player) without the
simulation or presentation referencing any networking type (RiskList R20).

**Boss ≠ arena.** `BossSimulation` owns only the boss. Arena mechanisms (gates, hazards, phase objects, exit)
are owned by a separate `ArenaSimulation`, and an `EncounterCoordinator` bridges them via project-owned
commands/events (e.g. boss `PhaseChanged(2)` → coordinator → arena `ActivateMechanismGroup("phase_2")`). This
keeps a boss reusable across arenas and testable arena-less. Full split:
[Architecture.md §5](Architecture.md). All three consume outer systems only through Core **ports**
([Architecture.md §6](Architecture.md)); replication/state authority still follows the rules below.

## 9.3 Run modes

- **Single-player:** `BossSimulation` + `BossPresentation` on the one machine; `BossReplication` inert. Same
  simulation rules as host mode (invariant 10).
- **Host (multiplayer):** `BossSimulation` + `BossPresentation` locally; `BossReplication` broadcasts.
- **Client proxy (multiplayer):** `BossPresentation` only, driven by `BossReplication`; **no** simulation.

## 9.4 Identity & versioning

- `BossInstanceId` — stable per encounter instance; all peers agree.
- `DefinitionId` — which boss definition (design/data).
- `ProtocolVersion` — the replication wire contract version; a mismatch is an explicit refusal, never silent
  divergence.
- `AttackInstanceId` — stable per attack occurrence (distinct from `AttackDefinitionId`); the anchor for
  duplicate suppression and host/client timeline alignment.

## 9.5 Time model

- The host advances a **simulation tick** (fixed-step recommended). Snapshots and events carry
  `SimulationTick` / `StateStartTick`.
- Clients align presentation (telegraphs, attack commits, animation) to **host simulation time**, not local
  wall-clock, with a bounded, corrected display delay (invariant 9, RiskList R17).

## 9.6 State & events (channels)

Two carriers (mirrors ST's channel semantics in `NetworkingArchitecture.md`):

- **Unreliable snapshots** — continuous correction: `BossReplicatedState` (position/rotation/health/phase/
  state/attack/weak-points/mechanism + `LastProcessedEventSequence`). Loss is tolerated; snapshots
  never *drive* discrete transitions.
- **Reliable, sequenced discrete events** — `BossDiscreteEvent` (`AttackSelected`, `TelegraphStarted`,
  `AttackCommitted`, `ProjectileSpawned`, `PhaseChanged`, `WeakPointChanged`, `AddSpawned`,
  `ArenaMechanismChanged`, `BossStaggered`, `BossDefeated`). Each carries a `Sequence` for ordering + idempotent
  application.

(Exact schemas: [MultiplayerLoadingContract.md §5.7](MultiplayerLoadingContract.md).)

## 9.7 Authority rules

- **Attack selection:** host picks once; clients never roll. `AttackInstanceId` ties the whole telegraph→
  commit→projectile chain together.
- **Deterministic random:** the host performs random decisions once and replicates their **result** (e.g.
  the selected attack / target), not the RNG state. Clients apply results and never roll. The host's RNG
  state stays **host-internal**; it is replicated only if a concrete need arises (replay, save/restore, host
  recovery) — since clients do not re-simulate, they need the outcome, not the generator.
- **Movement:** host computes movement intent per the boss's chosen movement model
  ([CollisionAndNavigationProposal.md §4.8](CollisionAndNavigationProposal.md)); clients interpolate from
  snapshots.
- **Projectiles:** host authoritative for gameplay projectiles (damage-dealing); clients may render
  non-authoritative visual-only copies aligned to the `ProjectileSpawned` event.
- **Damage / health / death:** host only. Clients report intent (hit requests) and apply results.
- **Phase:** host advances the real state machine and emits `PhaseChanged` + snapshot; clients never phase
  from local HP.
- **Weak points / adds / arena mechanisms:** host authoritative; replicated as state + discrete events.

## 9.8 Join-in-progress & recovery

- A client joining mid-fight first receives a **full baseline snapshot** (current phase, state, active
  attack, weak points, adds, arena mechanism state) **before** normal event processing begins
  (invariant 6, RiskList R18).
- Periodic **recovery snapshots** let a client that missed/!desynced rebuild without a full rejoin.

## 9.9 Robustness (duplicate / loss / desync)

- **Duplicate suppression:** reliable events are idempotent by (`BossInstanceId`, `Sequence`) and by
  `AttackInstanceId`; a retransmitted event never spawns a second projectile/add/reward or re-applies damage
  (invariant 5, RiskList R19).
- **Out-of-order handling:** events applied in `Sequence` order; `LastProcessedEventSequence` in snapshots
  lets a client detect gaps.
- **Snapshot loss:** losing continuous snapshots never cancels or repeats a discrete attack (invariant 7).
- **Desync detection:** compare replicated phase/state/attack (and optionally a lightweight state hash)
  against host snapshots; on mismatch, request/apply a recovery snapshot.

## 9.10 Invariants
The ten synchronization invariants are the acceptance contract; they are listed once in
[MultiplayerLoadingContract.md §5.9](MultiplayerLoadingContract.md) and are not duplicated here.

## 9.11 Teardown

On `BossDefeated` / arena exit: stop the simulation, unsubscribe every replication + event handler, release
presentation resources, and clear boss/arena state so nothing survives into the next level (RiskList R8,
report 4.6, [MultiplayerLoadingContract.md §5.11](MultiplayerLoadingContract.md)).

## 9.12 SULFUR Together: reuse vs reference vs new

**Boundary rule:** False Gods consumes SULFUR Together (ST) capabilities **only through project-owned ports
implemented inside `FalseGods.Integration.SulfurTogether`** ([Architecture.md](Architecture.md),
[DependencyRules.md](DependencyRules.md)). No boss, arena, protocol, or presentation code references ST,
LiteNetLib, Steam, `CoopConnection`, `NetService`, `NetArenaCommand`, or any concrete ST message/manager type.
"Consume via port" below means exactly that — never a direct dependency.

| ST capability | Disposition | Port (implemented in the ST adapter) |
|---|---|---|
| LiteNetLib transport (+ Steam P2P loopback) | **Consume via port** — transport is invisible to False Gods | `IEncounterChannel` (reliable/unreliable send/receive) |
| Session / connection lifecycle (`CoopConnection`) | **Consume via port** | `IMultiplayerSession` (host/client role, join/leave) |
| Player registry (`GameManager.Players` incl. headless remotes) | **Consume via port** | `IPlayerRoster` / `IPlayerQuery` |
| Arena readiness / lockdown (`ArenaLockdownManager`, `ArenaBarrierManager`, `ArenaDoorwaySensor`, `NetArenaCommand`, `NetClientArenaEnter`) | **Consume via port** | `IArenaLockdownPort` |
| Level/seed authority + `NetLevelManifest` | **Model on; consume via port** | `IMultiplayerSession` (arena-entry authority); `ArenaManifest` mirrors the header shape |
| Runtime spawn + world/entity roster (`NetRuntimeSpawn`, `NetWorldEntityRoster`) | **Model on; consume via port** | `ISpawnPort` / `IEncounterReplication` (stable ids) |
| Message envelope + `Net*`/`Net*Codec`/`Register` pattern | **Reuse pattern inside the adapter only** | Adapter serializes Protocol DTOs; message ids/registration never leave the adapter (§5.10) |
| Boss adapter stack (`IBossEncounterAdapter`, `NetBossEncounterManager`, `BossReflect`, msgs 28–43) | **Reference only** | Vanilla-compat design; original bosses use the layered model, not this state model — no dependency |
| Host-driven proxy discipline (`HostDrivenProxyPlan.md`) | **Reference / principle** | Confirms "client = non-autonomous puppet"; our presentation layer is the clean-room version |
| BossSimulation / BossPresentation / BossReplication split, sim-tick time model, `BossReplicatedState` / `BossDiscreteEvent`, attack-instance identity, duplicate suppression, join-in-progress baseline | **New to False Gods** | Defined in Core/Protocol; the purpose-built contract this document specifies |

## 9.13 Running without SULFUR Together

- `BossSimulation` + `BossPresentation` must build and run with **no reference to any networking type**.
- `BossReplication` is isolated behind an optional integration seam (separate assembly / reflection /
  soft dependency), discovered only when SULFUR Together is loaded — the same soft-dependency discipline
  SULFUR Together itself uses for optional libraries. Single-player must never fail because networking is
  absent (RiskList R20, invariant 10).
