# ADR-005 — Snapshot + discrete-event replication

**Status:** Proposed

## Context
Clients render a host-authoritative boss. Continuous state (position, health) tolerates loss and benefits from
frequent correction; discrete transitions (attack committed, phase change, death, spawn) must happen exactly
once and survive packet loss/reorder. The client does **not** run the authoritative simulation.

## Decision
Two carriers over transport-neutral `FalseGods.Protocol` types:
- **Unreliable `BossReplicatedState` snapshots** — continuous correction (position/rotation/health/phase/
  state/attack/weak-points/arena-mechanism), plus `LastProcessedEventSequence`.
- **Reliable, sequenced `BossDiscreteEvent`s** — transitions (`AttackSelected`, `TelegraphStarted`,
  `AttackCommitted`, `ProjectileSpawned`, `PhaseChanged`, `WeakPointChanged`, `AddSpawned`,
  `ArenaMechanismChanged`, `BossStaggered`, `BossDefeated`), idempotent by (`BossInstanceId`, `Sequence`) and
  `AttackInstanceId`.

Because the client never re-simulates, snapshots carry **decision results, not RNG state**. The host's
authoritative RNG stays host-internal and is replicated **only** if a concrete need arises (replay,
save/restore, host recovery). Join-in-progress is served by a full baseline snapshot before normal event
processing.

## Alternatives considered
- **Full deterministic lockstep** — needs cross-machine deterministic Unity/A*/physics; unnecessary for a
  host-authoritative model and fragile. Rejected.
- **Replicating full RNG state by default** — only useful for client resimulation, which we don't do; dropped
  from the default snapshot to avoid a field with no consumer.

## Consequences
- Requires stable `BossInstanceId`/`AttackInstanceId` and duplicate suppression.
- Bounded, host-time-aligned client display delay (RiskList R17); duplicate/out-of-order idempotence
  (RiskList R19); baseline-driven late join (RiskList R18).

## Verification status
Unverified — validated by PoC Phase B (B2–B8).
