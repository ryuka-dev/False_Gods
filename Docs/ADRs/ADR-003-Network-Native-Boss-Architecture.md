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
- **BossPresentation** — visuals only, on every machine; never decides damage/phase/death/target/attack.
- **BossReplication** — snapshots + discrete events, active only when SULFUR Together is present.

Single-player and host use the **same** `BossSimulation`; the host merely adds replication.

## Alternatives considered
- **Reuse `IBossEncounterAdapter`** — a vanilla-compat model; would import its limitations. Kept as reference
  only.
- **Client-side simulation with reconciliation** — divergence-prone (SULFUR Together's earlier "patch-based
  mirror" problem). Rejected.

## Consequences
- Requires project-owned domain types in `FalseGods.Core` and DTOs in `FalseGods.Protocol`.
- Presentation must be inert without simulation (testable in isolation; RiskList R16).
- Enables single-player without any networking dependency (ADR-004).

## Verification status
Unverified — validated by PoC Phase B ([MinimalProofOfConceptPlan.md](../MinimalProofOfConceptPlan.md)).
