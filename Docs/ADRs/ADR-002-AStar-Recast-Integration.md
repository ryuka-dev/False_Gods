# ADR-002 — A* Recast integration for arena navigation

**Status:** Proposed

## Context
SULFUR's AI navigation is the **A* Pathfinding Project** (a persistent `AstarPath.active` recast graph
re-scanned per level), **not** Unity NavMesh (verified in the decompile; see
[CollisionAndNavigationProposal.md](../CollisionAndNavigationProposal.md)). A custom arena must make its
walkable geometry available to that graph, and enemies/bosses path via `CustomRichAI` + `GetNearest(...Walkable)`.

## Decision
Arena navigation integrates with the game's A* recast graph, accessed **only through an `INavigationPort`
implemented in `FalseGods.Integration.Sulfur`**. Core/UnityRuntime never reference `Pathfinding.*`. Preferred
mechanism: a prebaked `NavmeshPrefab` applied at load; fallback: a runtime recast rescan
(`SnapForceBoundsToScene` + `Scan`) with attention to the `NavMeshCleaner` walkability flood-fill.

## Alternatives considered
- **Unity NavMesh** — not what the game/agents use; would not drive vanilla AI. Rejected.
- **Direct A* calls from gameplay code** — leaks `Pathfinding.*` into feature code; rejected in favour of the
  port.

## Consequences
- `INavigationPort` must express: register/apply arena nav, query nearest walkable, add/remove off-mesh links,
  rescan, and teardown.
- Big/2D bosses may bypass recast for locomotion and use the port only for target queries (ADR-003).

## Verification status
Unverified — `NavmeshPrefab.Apply()` usability + cleaner behaviour are PoC items (RiskList R4/R5).
