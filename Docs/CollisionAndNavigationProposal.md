# 4. Collision & Navigation Proposal

*Recommended collision layering, how to get the arena into SULFUR's navigation, boss pathing, and runtime
teardown.* This is the most game-specific report — **SULFUR does not use Unity NavMesh; it uses the A\*
Pathfinding Project.**

## 4.1 Decoupled layers (as the request intends)

```
ArenaRoot
  ├─ VisualRoot       vanilla cave prefabs/props (report 2/3), no gameplay colliders relied upon
  ├─ CollisionRoot    our own SIMPLE colliders: 1 floor, 4 boundary walls, few pillar colliders
  ├─ NavigationRoot   what the A* recast graph should treat as walkable + off-mesh links + anchors
  ├─ GameplayRoot     PlayerSpawn, BossSpawn, seal triggers, exit, mechanisms, phase objects
  └─ LightingRoot     realtime lights, fog/ambient, reflection probes (report 3.3)
```

Rationale (from the decompile): even the vanilla game keeps *generation-time* collision simple
(`WorkingCollision`, `quickCollisonCheckRadius`, a `bakedCollision` TextAsset) rather than testing full rock
meshes. We follow the same instinct — never make the boss or players collide with decorative rock
`MeshCollider`s.

## 4.2 Collision layer

- Give `CollisionRoot` colliders the **layer(s) the game treats as world geometry**. The game references a
  `GameManager.geometryLayer` (used by `BatchedNPCRaycasts`, AI LOS, ground checks); the arena floor/walls
  must be on the layer(s) that both physics and the recast graph consider solid.
- Keep decorative vanilla rock colliders **off** the geometry/nav layer (or strip their colliders on
  instantiate) so players/boss never snag on them.
- Walls: a simple invisible boundary box around the play area. Floor: one flat collider. Pillars/large
  obstacles: a handful of convex colliders. Nothing else.

> The exact geometry layer index and the recast graph's rasterization mask are **serialized on the scene's
> `AstarPath` component**, not in code. Read them at runtime for the PoC:
> `AstarPath.active.data.recastGraph.mask` / `.rasterizeColliders` / `.rasterizeMeshes` and
> `GameManager.Instance.geometryLayer`. (RiskList R3.)

## 4.3 Navigation — the A\* recast graph (the real system)

Verified facts (`BuildNavMeshNode`, `NavMeshManager`, `NavMeshCleaner`, `RecastTagVolume`, `AiAgent`,
`CustomRichAI`):

- There is **one persistent `AstarPath.active`** in the game. Its recast graph is **graph index 0**
  (`NNConstraint.graphMask = 1` throughout the code). It is **re-scanned at runtime for every level**, not
  baked per scene.
- The runtime scan the game performs (`BuildNavMeshNode.Execute`):
  1. `AstarPath.active.data.recastGraph.cellSize = currentEnvironment.navMeshVoxelSize` (**0.1** by default).
  2. `recastGraph.SnapBoundsToScene()` (fit graph bounds to current geometry).
  3. add each room's `bakedNavMeshLinks` to `AstarPath.active.offMeshLinks`; call `NodeLink2.TryAddLink()`.
  4. `AstarPath.active.ScanAsync()` (async in play mode) — or `.Scan()` synchronously.
  5. after scan, `RecastTagVolume.UpdateGraphTags()` runs (via `AstarPath.OnPostScan`).
- `NavMeshManager` exposes the two calls as a tidy reusable pair:
  `SetupAstarPathSize()` = `SnapForceBoundsToScene()`, `BakeNavMesh()` = `Scan()`.
- **`NavMeshCleaner : GraphModifier.OnGraphsPostUpdate`** flood-fills walkability: it looks up the graph
  `Area` of each point in `validNavMeshPoints` (built from every connector position + every room's
  `navMeshAnchors`) and marks **only nodes in those areas as walkable**; everything else becomes unwalkable.
  ⚠️ **This is the single most important gotcha for a custom arena**: if our arena's walkable island is not
  represented in `validNavMeshPoints`, the cleaner will mark it unwalkable and the boss/enemies won't move.

### Recommended approach for the arena's nav

**Option 1 (recommended for a fixed arena): prebaked `NavmeshPrefab`.**
`BuildNavMeshNode` shows the game already supports **`NavmeshPrefab`** objects: if any exist, it calls
`prefab.Apply()` + `AstarPath.active.UpdateGraphs(prefab.bounds)` **instead of** a full rescan. We can bake
the arena's navmesh in our editor project and ship it as a `NavmeshPrefab`, then apply it at arena load. This
is deterministic (identical on host and every client), cheap at runtime, and side-steps both the recast scan
cost and the `NavMeshCleaner` flood-fill. *Needs verification that `NavmeshPrefab.Apply()` is callable from a
mod and that a bundle-baked navmesh is valid (RiskList R4).*

**Option 2 (fallback): runtime recast rescan.**
After the arena geometry is present, call the game's own rescan (`NavMeshManager.SetupAstarPathSize()` +
`BakeNavMesh()`, i.e. `SnapForceBoundsToScene()` + `Scan()`). Then either (a) ensure a walkable anchor inside
the arena is included wherever `NavMeshCleaner.validNavMeshPoints` is computed, or (b) provide our own
`GraphModifier` / place a `navMeshAnchor`-tagged transform so our island survives cleaning. Runtime rescan is
more flexible (supports future destructible/phase-changing terrain) but costs a scan and must be sequenced
carefully in multiplayer (report 5).

Off-mesh links (jumps/drops across gaps) use A* `NodeLink2` / `OffMeshLinks.OffMeshLinkSource`; add them only
if the arena needs disconnected walkable areas. A single flat arena floor needs none.

## 4.4 Real agent parameters

- `cellSize` (voxel size) is the one nav parameter set **from game code** per environment:
  `WorldEnvironment.navMeshVoxelSize = 0.1`. Match this for the arena's environment so agent fit is
  consistent.
- The rest of the recast parameters — **character radius, walkable height, max slope, max step/climb**, and
  the rasterization mask — are **serialized on the scene `AstarPath` RecastGraph**, not in code. Obtain the
  real values at runtime for the PoC by reading
  `AstarPath.active.data.recastGraph.{characterRadius, walkableHeight, walkableClimb, maxSlope, mask}` and
  record them in this doc once measured. Design arena slopes/steps within those limits (keep the main floor
  flat, per the arena design requirements).

## 4.5 Boss / enemy pathing

- Ordinary enemies and most bosses are `Npc` units with an `AiAgent`; movement is via **`CustomRichAI`** (a
  subclass of A*'s `RichAI`) over the recast graph. They query
  `AstarPath.active.GetNearest(pos, NNConstraint.Walkable)` constantly (e.g. `AiAgent`, `BossFightHelper`).
- **Keep the boss's walkable area a clean, convex-ish flat island.** Because agents snap to
  `GetNearest(...Walkable)`, concave pockets, thin ledges, and decoration-induced holes cause corner-sticking
  — exactly what the arena-design requirements want to avoid. Put decorative rock **outside** the nav mask.
- **Large / special-movement bosses**: the vanilla `EmperorBossSpiderEndless` does **not** rely purely on the
  recast graph — it uses `Physics.Raycast(..., groundLayerMask)` ground-following for its body. So for a big
  original boss, movement can be **scripted/physics-driven** (raycast to ground + our own steering) and use
  the recast graph only for target-point queries, or not at all. Dashes/leaps/teleports should move the boss
  directly and then **re-snap to the navmesh** on landing via `GetNearest(...Walkable)` (the pattern
  `BossFightHelper` already uses).
- **Knockback / off-navmesh recovery**: after knockback or a leap, snap the boss back with
  `AstarPath.active.GetNearest(pos, NNConstraint.Walkable).position` before resuming pathing (vanilla bosses
  do this). Guard against being pushed outside the boundary walls (the invisible walls + a recovery snap).
- **Authority**: in multiplayer the **host runs boss AI/pathing**; the client shows an interpolated puppet
  (report 5, and SULFUR Together `HostDrivenProxyPlan.md`). Never compute boss paths on the client.

## 4.6 Runtime teardown (must not pollute the next level)

Because `AstarPath.active` is **persistent and shared**, arena teardown must restore it:

- If **Option 1 (NavmeshPrefab)** was used: on exit, remove/replace the applied graph region. The safest is to
  let the **next level's own generation rescan** overwrite it — but do not leave stale walkable nodes that the
  next `NavMeshCleaner` pass might treat as valid. Prefer triggering a clean rescan (or the game's normal
  level transition, which already rescans) after unloading the arena geometry.
- If **Option 2 (rescan)** was used: unloading the arena geometry and letting the normal level-transition
  rescan run is sufficient, since the game rescans every level anyway.
- Destroy all arena roots, `Addressables.Release` every handle taken for vanilla assets, remove any off-mesh
  links we added to `AstarPath.active.offMeshLinks`, and clear any `GraphModifier`/`RecastTagVolume` we
  registered. Verify no arena `GameObject`s or nav nodes survive into the next level (PoC teardown check).

## 4.7 Open verification items (RiskList)
- R3: exact geometry layer + recast rasterization mask (read at runtime).
- R4: `NavmeshPrefab.Apply()` usability from a mod + validity of a bundle-baked navmesh.
- R5: real recast agent params (read at runtime, record here).
- R8: teardown leaves `AstarPath.active` clean for the next level.
