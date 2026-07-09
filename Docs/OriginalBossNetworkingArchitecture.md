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
| **BossPresentation** | every machine | renderers/sprites, animation, particles, audio, telegraphs, camera FX, non-authoritative visual projectiles, interpolation | decide damage, phase, death, target, or attack outcome; **touch a wire DTO** |
| **BossReplication** | active only when a multiplayer integration is registered | host snapshots, discrete events, sequence numbers, sim tick/time, interpolation targets, duplicate suppression, late-join restore, desync detection, recovery baselines | run gameplay logic |

The layers are **assembly/type-isolated** so that `BossReplication` can be absent (single-player) without the
simulation or presentation referencing any networking type (RiskList R20), and so that the optional SULFUR
Together adapter is never a CLR dependency of the base plugin
([ADR-004](ADRs/ADR-004-Optional-Sulfur-Together-Adapter.md)).

**Presentation is fed contracts, not packets.** Wire DTOs stop at `FalseGods.Application`:

```text
Network DTO -> replication/application mapper -> PresentationState / PresentationEvent -> BossPresentation
```

The same mapper is fed by the local simulation in single-player, so both modes drive one presentation entry
point ([Architecture.md §7](Architecture.md)).

**Boss ≠ arena.** `BossSimulation` owns only the boss. Arena mechanisms (gates, hazards, phase objects, exit)
are owned by a separate `ArenaSimulation`, and an `EncounterCoordinator` bridges them via project-owned
commands/events (e.g. boss `PhaseChanged(2)` → coordinator → arena `ActivateMechanismGroup("phase_2")`). This
keeps a boss reusable across arenas and testable arena-less. The same seam holds on the wire: `BossSnapshot` vs
`ArenaSnapshot`, `BossEvent` vs `ArenaEvent`, composed by `EncounterBaseline` (§9.6). Full split:
[Architecture.md §5](Architecture.md). All three consume outer systems only through project-owned **ports**,
each declared in the innermost module that consumes it ([Architecture.md §6](Architecture.md)); replication and
state authority still follow the rules below.

## 9.3 Run modes

- **Single-player:** `BossSimulation` + `BossPresentation` on the one machine; `BossReplication` inert. Same
  simulation rules as host mode (invariant 10).
- **Host (multiplayer):** `BossSimulation` + `BossPresentation` locally; `BossReplication` broadcasts.
- **Client proxy (multiplayer):** `BossPresentation` only, driven by `BossReplication`; **no** simulation.

## 9.4 Identity & versioning

- `EncounterId` — stable per encounter run; scopes both event streams and the baseline.
- `BossInstanceId` — stable per boss instance within the encounter; all peers agree.
- `DefinitionId` — which boss definition (design/data).
- `ProtocolVersion` — the replication wire contract version; a mismatch is an explicit refusal, never silent
  divergence.
- `AttackInstanceId` — stable per attack occurrence (distinct from `AttackDefinitionId`); the anchor for
  duplicate suppression and host/client timeline alignment.

**What "deterministic" covers.** These identifiers, the per-stream event order, idempotent event application,
and the rule that each authoritative decision happens exactly once on the host. It does **not** cover Unity
physics, A\* recast scans, or client-side code being bit-identical across machines — that is never required,
because clients never re-run the authoritative simulation.

## 9.5 Time model

- The host advances a **simulation tick** (fixed-step recommended). Snapshots and events carry
  `SimulationTick` / `StateStartTick`.
- Clients align presentation (telegraphs, attack commits, animation) to **host simulation time**, not local
  wall-clock, with a bounded, corrected display delay (invariant 9, RiskList R17).

## 9.6 State & events (channels)

Two carriers (mirrors ST's channel semantics in `NetworkingArchitecture.md`), and **boss state is separate from
arena state on both**:

- **Unreliable snapshots** — continuous correction:
  - `BossSnapshot` (position/rotation/health/phase/state/attack/weak-points + `LastProcessedBossEventSequence`);
  - `ArenaSnapshot` (mechanism/hazard/gate states + `LastProcessedArenaEventSequence`).

  Loss is tolerated; snapshots never *drive* discrete transitions.
- **Reliable, sequenced discrete events** — two independent streams, each with its own sequence space:
  - `BossEvent` (`AttackSelected`, `TelegraphStarted`, `AttackCommitted`, `ProjectileSpawned`, `PhaseChanged`,
    `WeakPointChanged`, `AddSpawned`, `BossStaggered`, `BossDefeated`);
  - `ArenaEvent` (`MechanismGroupActivated`, `MechanismStateChanged`, `HazardTriggered`, `GateStateChanged`,
    `ArenaExitUnlocked`).
- **`EncounterBaseline`** — the reliable, once-per-join composition of `BossSnapshot` + `ArenaSnapshot` +
  encounter state, used for join-in-progress and full recovery.

Arena mechanism state is deliberately **not** a `BossSnapshot` field and arena transitions are deliberately not
`BossEvent`s: a boss reusable across arenas cannot carry one arena's mechanism vocabulary in its protocol
([ADR-005](ADRs/ADR-005-Snapshot-And-Discrete-Event-Replication.md)).

(Exact schemas: [MultiplayerLoadingContract.md §5.7](MultiplayerLoadingContract.md).)

## 9.7 Authority rules

- **Attack selection:** host picks once; clients never roll. `AttackInstanceId` ties the whole telegraph→
  commit→projectile chain together.
- **Authoritative random:** the host performs random decisions once and replicates their **result** (e.g.
  the selected attack / target), not the RNG state. Clients apply results and never roll. The host's RNG
  state stays **host-internal**; it is replicated only if a concrete need arises (replay, save/restore, host
  recovery) — since clients do not re-simulate, they need the outcome, not the generator. No cross-machine
  determinism of the generator is required, because no client ever runs it.
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

- A client joining mid-fight first receives one **`EncounterBaseline`** — boss snapshot, arena snapshot, and
  encounter state (current phase, state, active attack, weak points, adds, arena mechanism state) — **before**
  normal event processing begins (invariant 6, RiskList R18).
- The baseline carries `LastProcessedBossEventSequence` and `LastProcessedArenaEventSequence`, so the client
  knows exactly where each stream resumes.
- A client that **missed events or has drifted out of sync** can be repaired by re-applying a fresh
  `EncounterBaseline`, without a full rejoin.

## 9.9 Robustness (duplicate / loss / desync)

- **Duplicate suppression:** reliable events are idempotent by (`EncounterId`, stream, `Sequence`), and boss
  attack effects additionally by `AttackInstanceId`; a retransmitted event never spawns a second
  projectile/add/reward or re-applies damage (invariant 5, RiskList R19).
- **Out-of-order handling:** events are applied in `Sequence` order **within their own stream**; the
  `LastProcessed*EventSequence` fields in the snapshots let a client detect a gap in either stream
  independently.
- **Snapshot loss:** losing continuous snapshots never cancels or repeats a discrete attack (invariant 7).
- **Desync detection:** compare replicated phase/state/attack (and optionally a lightweight state hash)
  against host snapshots; on mismatch, request and apply a fresh `EncounterBaseline`.

## 9.10 Invariants
The synchronization invariants are the acceptance contract; they are listed once in
[MultiplayerLoadingContract.md §5.9](MultiplayerLoadingContract.md) and are not duplicated here.

## 9.11 Teardown

On `BossDefeated` / arena exit: stop the simulation, unsubscribe every boss **and arena** replication/event
handler, release presentation resources, remove the arena's own contributions from the active level's A\* graph,
and clear boss/arena state so nothing survives into the next level (RiskList R8, report 4.6,
[MultiplayerLoadingContract.md §5.11](MultiplayerLoadingContract.md)).

## 9.12 SULFUR Together: reuse vs reference vs new

**Boundary rule:** False Gods consumes SULFUR Together (ST) capabilities **only through project-owned ports
implemented inside the optional `FalseGods.Integration.SulfurTogether` adapter**
([Architecture.md](Architecture.md), [DependencyRules.md](DependencyRules.md)). No boss, arena, protocol, or
presentation code — and no main flow — references ST, LiteNetLib, Steam, `CoopConnection`, `NetService`,
`NetArenaCommand`, or any concrete ST message/manager type. "Consume via port" below means exactly that.

**This table is an existing-systems mapping.** ST type names appear here as implementation notes for the
adapter, and nowhere else in the design.

| ST capability | Disposition | Port (implemented in the ST adapter) |
|---|---|---|
| LiteNetLib transport (+ Steam P2P loopback) | **Consume via port** — transport is invisible to False Gods | `IEncounterChannel` (reliable/unreliable send/receive of `EncodedPayload`) |
| Session / connection lifecycle (`CoopConnection`, `internal`) | **Consume via port** | `IMultiplayerSession` (host/client role, join/leave) |
| Player registry (`GameManager.Players` incl. headless remotes) | **Consume via port** | `IPlayerRoster` (membership, identity, positions) |
| Remote NPC activation (`NpcUpdateManager.LateUpdate` postfix) | **Consume via a separate port** — not the same responsibility as the roster (§9.14) | `IRemoteNpcActivationPort` |
| Arena lockdown (`ArenaLockdownManager` (`internal`), `ArenaBarrierManager`, `ArenaDoorwaySensor`, `NetArenaCommand`, `NetClientArenaEnter`) | **Consume via port** | `IArenaLockdownPort` |
| Load readiness (`NetLoadBarrier`, `internal`, log/status-only by default) | **Pattern reuse only** — it gates nothing today | `IEncounterReadyGate` is a **new** fail-closed gate ([MultiplayerLoadingContract.md §5.1.2](MultiplayerLoadingContract.md)) |
| Level/seed authority + `NetLevelManifest` | **Model on; consume via port** | `IMultiplayerSession` (arena-entry authority); `ArenaManifest` mirrors the header shape |
| Runtime spawn + world/entity roster (`NetRuntimeSpawn`, `NetWorldEntityRoster`) | **Model on; consume via port** | `ISpawnPort` / `IEncounterReplication` (stable ids) |
| Message envelope + `Net*`/`Net*Codec`/`Register` pattern | **Reuse pattern inside the adapter only** | Application serializes Protocol DTOs to `EncodedPayload`; message ids/registration never leave the adapter (§5.10) |
| Boss adapter stack (`IBossEncounterAdapter`, `NetBossEncounterManager` (`internal`), `BossReflect`, msgs 28–43) | **Reference only** | Vanilla-compat design; original bosses use the layered model, not this state model — no dependency |
| Host-driven proxy discipline (`HostDrivenProxyPlan.md`) | **Reference / principle** | Confirms "client = non-autonomous puppet"; our presentation layer is the clean-room version |
| BossSimulation / BossPresentation / BossReplication split, sim-tick time model, `BossSnapshot` / `ArenaSnapshot` / `BossEvent` / `ArenaEvent` / `EncounterBaseline`, presentation contracts, attack-instance identity, duplicate suppression | **New to False Gods** | Defined in Core/Protocol/RuntimeContracts; the purpose-built contract this document specifies |

Most of the ST types above are `internal` with no `[InternalsVisibleTo]`, so the adapter reaches them by guarded
reflection or via a future public ST integration bridge — see
[MultiplayerLoadingContract.md §5.1.1](MultiplayerLoadingContract.md) and
[ADR-004](ADRs/ADR-004-Optional-Sulfur-Together-Adapter.md).

## 9.13 Running without SULFUR Together

- `BossSimulation` + `BossPresentation` must build and run with **no reference to any networking type**.
- `BossReplication` lives behind an optional integration seam. `FalseGods.Plugin` holds **no CLR dependency** on
  `FalseGods.Integration.SulfurTogether`: the adapter is a separate assembly that references the stable
  `FalseGods.RuntimeContracts` and registers its capabilities at runtime through `IIntegrationRegistry`. If it
  is absent, nothing registers, replication is a no-op, and single-player plays
  ([ADR-004](ADRs/ADR-004-Optional-Sulfur-Together-Adapter.md), RiskList R20/R29, invariant 10).

## 9.14 Player roster and NPC activation are different capabilities

An earlier draft claimed that registering remote players in `GameManager.Players` was enough to make NPCs wake
near a client. The decompile says otherwise:

- `GameManager.Players` registration feeds the game's **detection, line-of-sight, and target selection** — an
  already-awake NPC can see and chase a remote player.
- Vanilla `NpcUpdateManager.LateUpdate` computes its activation distance from
  `GameManager.Instance.PlayerObject.transform.position`, i.e. the **local main player only**. Registering a
  remote player does not touch that loop. ST supplements it with an additional `NpcUpdateManager.LateUpdate`
  **postfix** (`RemotePlayerRegistryManager.ActivateNpcsNearRemotePlayers`) that wakes inactive NPCs near any
  remote player.

So `IPlayerRoster` and `IRemoteNpcActivationPort` are **two ports for two responsibilities**. Both are outer
capability ports in `FalseGods.RuntimeContracts`; neither belongs in `FalseGods.Core` (RiskList R10).
