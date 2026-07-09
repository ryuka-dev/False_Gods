# ADR-005 — Snapshot + discrete-event replication

**Status:** Proposed

## Context
Clients render a host-authoritative boss inside a host-authoritative arena. Continuous state (position, health,
mechanism pose) tolerates loss and benefits from frequent correction; discrete transitions (attack committed,
phase change, death, gate opened) must happen exactly once and survive packet loss/reorder. The client does
**not** run the authoritative simulation.

An early draft folded arena mechanism state into `BossReplicatedState` and arena transitions into the boss
event stream. That contradicts the Boss/Arena/Encounter split ([Architecture.md §5](../Architecture.md)): it
welds one arena's mechanism vocabulary into every boss's snapshot, forces a boss version bump when an arena
gains a hazard, and makes an arena-less boss test impossible to serialize.

## Decision

**Five wire types in `FalseGods.Protocol`, split along the same seam as the domain:**

| Type | Carrier | Contents |
|---|---|---|
| `BossSnapshot` | unreliable | boss continuous state: position/rotation, health, phase id, state id, `StateStartTick`, active `AttackInstanceId`, target, weak-point states, `LastProcessedBossEventSequence` |
| `ArenaSnapshot` | unreliable | arena continuous state: mechanism states, hazard states, gate/barrier state, safe-region state, `LastProcessedArenaEventSequence` |
| `BossEvent` | reliable, sequenced | `AttackSelected`, `TelegraphStarted`, `AttackCommitted`, `ProjectileSpawned`, `PhaseChanged`, `WeakPointChanged`, `AddSpawned`, `BossStaggered`, `BossDefeated` |
| `ArenaEvent` | reliable, sequenced | `MechanismGroupActivated`, `MechanismStateChanged`, `HazardTriggered`, `GateStateChanged`, `ArenaExitUnlocked` |
| `EncounterBaseline` | reliable, once | composes `BossSnapshot` + `ArenaSnapshot` + encounter state (`EncounterId`, `ArenaId`/`ArenaVersion`/`ContentHash`, `ProtocolVersion`, `SimulationTick`, encounter phase, both `LastProcessed*Sequence` values) |

- Boss and arena events occupy **separate sequence spaces**. Idempotence is by (`EncounterId`, stream,
  `Sequence`), and boss attack effects are additionally idempotent by `AttackInstanceId`.
- `EncounterBaseline` is the single carrier for **join-in-progress and full recovery**: a late or recovering
  client applies exactly one baseline, then resumes normal per-stream event processing from the sequences it
  carries.
- Because the client never re-simulates, snapshots carry **decision results, not RNG state**. The host's
  authoritative RNG stays host-internal and is replicated **only** if a concrete need arises (replay,
  save/restore, host recovery).
- Presentation never receives these types. `FalseGods.Application` maps them into `PresentationState` /
  `PresentationEvent` ([Architecture.md §7](../Architecture.md)).

## Alternatives considered
- **One `BossReplicatedState` carrying arena mechanism state** — the original draft. Couples boss protocol to
  arena content and breaks boss reuse across arenas. Rejected.
- **One event stream for boss and arena** — a dropped arena event stalls boss events behind it, and duplicate
  suppression needs a combined key that no single owner controls. Rejected.
- **Full deterministic lockstep** — needs cross-machine deterministic Unity/A\*/physics; unnecessary for a
  host-authoritative model and fragile. Rejected.
- **Replicating full RNG state by default** — only useful for client resimulation, which we don't do; dropped
  from the default snapshot to avoid a field with no consumer.

## Consequences
- Requires stable `EncounterId` / `BossInstanceId` / `AttackInstanceId` and per-stream duplicate suppression.
- Two snapshot types and two event streams to version instead of one; `EncounterBaseline` must be updated
  whenever either half gains state, or late join silently loses it. That coupling is deliberate and lives in one
  place.
- Bounded, host-time-aligned client display delay (RiskList R17); duplicate/out-of-order idempotence
  (RiskList R19); baseline-driven late join (RiskList R18).

## Verification status
Unverified — validated by PoC Phase B (B2–B8), with B8 asserting that one `EncounterBaseline` restores boss,
arena, and encounter state together.
