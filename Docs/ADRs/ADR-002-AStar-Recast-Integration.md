# ADR-002 — A* Recast integration for arena navigation

**Status:** Accepted; superseded in part by the level-generation hijack (see Verification status)

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
**Both halves verified, and then the shipped design stopped needing one of them.**

The prebaked path this ADR chose was proven end to end from a mod (PoC P5–P5d, RiskList R4/R5): bake with
`NavmeshPrefab.Scan`, ship the serialized tile bytes, apply with `Deserialize` + `SnapToGraph` + `ReplaceTiles`.
It took a floated arena floor from zero walkable nodes to walkable, **without** needing a `NavMeshCleaner`
anchor — `ReplaceTiles` side-steps the flood-fill a full scan triggers — and `ClearTiles` plus a walkability
restore returned the graph to baseline (P7 / R8). Ordinary enemies path it and route around a nav hole (P6 / R9).
Two facts from that work that outlived it: the recast graph **rasterizes meshes, not colliders**, and a bake is
`cellSize`-specific, so applied bounds must be tile-aligned or the floor lands shifted.

**What actually ships is the other route.** The arena is delivered by driving the game's own level generation, so
the arena *is* the level and the game's own navigation step scans it — the floor is a mesh on the `Geometry`
layer, which is exactly what that scan rasterizes. Nothing is applied and nothing has to be taken back. The
navigation port for that composition, `NativeLevelNavigationPort`, is deliberately a reporting no-op; the
additive `AstarNavigationPort` is still built and still used by the client path that loads an arena on top of a
level. So this ADR's *analysis* is load-bearing and its *mechanism* is now the fallback rather than the default.

One thing the shipped arena does author, because nothing else could: **jump links** (`NodeLink2`) between the
cave's terraces, and the walkable-surface tagging that decides which sculpted faces the scan may use.
Navigation is also the only thing constraining where enemies can go — colliders are not.
