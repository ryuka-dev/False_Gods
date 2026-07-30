# ADR-003 — Network-native boss architecture (Simulation / Presentation / Replication)

**Status:** Accepted, implemented (`FalseGods.Core` / `.UnityRuntime` / `.Protocol` + `.Application`)

## Context
SULFUR Together retrofits networking onto vanilla bosses via Harmony + reflection + adapters, achieving only
imperfect sync because vanilla boss logic (attacks, phases, presentation) was never designed for networking.
False Gods owns its bosses end-to-end.

## Decision
Every False Gods boss is split into three layers (see
[OriginalBossNetworkingArchitecture.md](../OriginalBossNetworkingArchitecture.md)):
- **BossSimulation** — authoritative domain logic; runs in single-player and on the host only.
- **BossPresentation** — visuals only, on every machine; never decides damage/phase/death/target/attack, and
  never sees a wire DTO — it is driven by `PresentationState` / `PresentationEvent`
  ([Architecture.md §7](../Architecture.md)).
- **BossReplication** — snapshots + discrete events, active only when a multiplayer integration is registered.

Single-player and host use the **same** `BossSimulation` and the **same** presentation entry point; the host
merely adds replication.

The simulation is **host-authoritative with deterministic identifiers and explicit authoritative decisions** —
not a cross-machine-deterministic simulation. Unity physics, A\* scans, and client-side code are not required to
be bit-identical anywhere, and clients never re-run the authoritative simulation. What must be deterministic is
identity (`EncounterId`, `BossInstanceId`, `AttackInstanceId`), event order within a stream, idempotent
application of a replayed event, and the fact that each authoritative decision is made exactly once, on the
host, and replicated as a result.

## Alternatives considered
- **Reuse `IBossEncounterAdapter`** — a vanilla-compat model; would import its limitations. Kept as reference
  only.
- **Client-side simulation with reconciliation** — divergence-prone (SULFUR Together's earlier "patch-based
  mirror" problem). Rejected.
- **Deterministic lockstep** — would demand cross-machine determinism from Unity physics and A\*, which the
  engine and the pathfinder do not promise. Rejected (ADR-005).

## Consequences
- Requires project-owned domain types in `FalseGods.Core`, DTOs in `FalseGods.Protocol`, and presentation
  contracts in `FalseGods.RuntimeContracts`.
- Presentation must be inert without simulation (testable in isolation; RiskList R16).
- Enables single-player without any networking dependency (ADR-004).

## Verification status
**Implemented and verified in game on two machines.** The three layers exist as separate assemblies and the
separation is mechanical, not aspirational: `FalseGods.Core` holds the simulation and cannot reference
`UnityEngine`; `FalseGods.UnityRuntime` holds the presentation and cannot reference `FalseGods.Protocol`, so
handing presentation a wire DTO does not compile; `FalseGods.Application` owns both mappers (domain → presentation
and wire → presentation) and `PresentationParityTests` asserts the two produce the same presentation for the same
state, which is what stops single-player and multiplayer drifting apart.

**What the two-peer runs proved beyond compilation:** the host owns the boss's phase, health, stations, rage,
summons and death; each peer plays its own roar, fog, music, arms, hit flash and boss bar off the replicated
facts; a client's weapon hits arrive as *intents* and the host answers with results; and a peer that joins mid
fight rebuilds the presentation it missed rather than replaying the ceremony. Every one of those was watched on
both machines, and each round's log was read on both sides rather than judged by feel — which is how the one real
bug of the opening arc was caught (a room gated on "triggered" where it meant "running").

**Open:** telegraph/commit timing offsets between host and client have never been measured (RiskList R17), and no
second boss has yet tested whether the definition really is data rather than a special case.
