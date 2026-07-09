# ADR-004 — Optional SULFUR Together adapter

**Status:** Proposed

## Context
False Gods must run in vanilla single-player when SULFUR Together (ST) is not installed, and provide
host-authoritative multiplayer when it is. ST — and, underneath it, LiteNetLib and Steam P2P — must not leak
into boss/arena/protocol/presentation code (the SULFUR Together coupling lesson).

## Decision
All ST access lives in a single **optional** assembly, `FalseGods.Integration.SulfurTogether`, which
implements Core ports (`IMultiplayerSession`, `IEncounterChannel`, `IEncounterReplication`, `IPlayerRoster`,
`IArenaLockdownPort`). It is the **only** module aware of ST / LiteNetLib / Steamworks. The Composition Root
detects ST at runtime and wires the adapter in; if the assembly is absent, replication is a no-op and the game
runs single-player. Discovery/reflection is isolated inside the adapter so a missing ST causes **no**
type-load failure.

## Alternatives considered
- **Direct ST/transport references from feature code** — the exact debt pattern to avoid. Rejected.
- **Hard dependency on ST** — breaks vanilla single-player. Rejected.

## Consequences
- Core/UnityRuntime never reference ST; a second transport or ST refactor changes only this adapter.
- Requires soft-dependency discipline (separate assembly, guarded loading) — same approach ST itself uses for
  optional libs.

## Verification status
Unverified — "runs with ST absent, no type-load failure" is a PoC/DoD gate (RiskList R20/R29).
