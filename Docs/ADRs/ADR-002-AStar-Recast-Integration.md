# ADR-002 — A* Recast integration for arena navigation

**Status:** Proposed

## Context
SULFUR's AI navigation is the **A* Pathfinding Project**, **not** Unity NavMesh (verified in the decompile; see
[CollisionAndNavigationProposal.md](../CollisionAndNavigationProposal.md)). `AstarPath.active` is a single
shared global **for the currently active level**, and a normal level change **rebuilds** it: `GameManager`'s
level-switch routine calls `AstarPath.active.data.ClearGraphs()` and destroys the `AstarPath` GameObject
(`Decompiled/.../GameManager.cs:1097`), then instantiates `astarPathPrefab` for the next level
(`GameManager.cs:1137`). A custom arena must make its walkable geometry available to the *current* graph, and
enemies/bosses path via `CustomRichAI` + `GetNearest(...Walkable)`.

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
- `INavigationPort` (declared in `FalseGods.Application`, implemented in `Integration.Sulfur`) must express:
  register/apply arena nav, query nearest walkable, add/remove off-mesh links, rescan, and teardown.
- Because an additive arena shares the **active level's** graph, the arena owns removing its own nodes,
  off-mesh links, and graph modifiers on exit. A future level change would rebuild the graph anyway, but
  relying on that would mean the arena leaks into the rest of the current level.
- Big/2D bosses may bypass recast for locomotion and use the port only for target queries (ADR-003).

## Verification status
Unverified — `NavmeshPrefab.Apply()` usability + cleaner behaviour are PoC items (RiskList R4/R5).
