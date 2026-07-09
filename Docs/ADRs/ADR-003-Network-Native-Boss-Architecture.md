# ADR-003 — Network-native boss architecture (Simulation / Presentation / Replication)

**Status:** Proposed

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
Unverified — validated by PoC Phase B ([MinimalProofOfConceptPlan.md](../MinimalProofOfConceptPlan.md)).
