# ADR-001 — Unity prefab as arena source of truth

**Status:** Accepted, implemented (`FalseGods.Unity` -> AssetBundle + `ArenaContentArtifact`)

## Context
Arenas need to be iterated visually (geometry, lighting, occlusion, boss-fight space) and must load identically
for every player. Two options: author arenas as data/code (transform lists assembled at runtime) or as
Unity-authored prefabs. SULFUR itself assembles modular `Room` prefabs; our arenas are fixed, hand-designed
spaces.

## Decision
The **Unity-authored `ArenaRoot` prefab is the source of truth** for a fixed arena's layout, collision,
lights, gameplay markers, and phase objects. Runtime code loads/realizes that authored content and resolves
only `VanillaAssetProxy` objects; it does not reconstruct the arena from hard-coded transforms. See
[ArenaLoadingProposal.md](../ArenaLoadingProposal.md) and [OriginalContentPipeline.md](../OriginalContentPipeline.md).

## Alternatives considered
- **Code/data-defined layout** — reproducible but not visually iterable; rejected as the primary format.
- **Feed a custom `Room` into SULFUR's generation pipeline** — heavy `MakerSet`/graph coupling for a single
  fixed arena; deferred to a possible procedural future.

## Consequences
- Requires a matching-version Unity project (Unity 6000.3.6f1 / URP) and an AssetBundle build/load path.
- Enables visual iteration and an authored-manifest parity check at runtime.
- Prefabs are content, not service containers (ADR-006).

## Verification status
**Verified in game.** The arena the first encounter is fought in *is* an authored Unity prefab: built into
`falsegods-poc-room.bundle` by `FalseGods.Unity`, shipped beside a `ArenaContentArtifact` that carries the
authored hierarchy, and loaded at runtime. AssetBundle load closed at PoC P2 (RiskList R2); authored-hierarchy
parity closed at P8 (R14, `verdict = MATCH`, all 14 parity nodes at their authored local transforms).

What the decision looks like in practice, a year of arena work later: **markers, not transform data.** Boss
stations, minion spawn points, crate production and delivery points, the fight's start trigger, the reward drop
and the vanilla-prop placements are all authored objects in the prefab that runtime code *reads*; none of them is
a constant in C#. The one thing the prefab does **not** decide is which of them carry an artifact row — the
parity map covers identity, colliders and spawns, so a marker added outside that set moves the bundle without
moving the content hash. See [BossEncounterRunbook.md](../BossEncounterRunbook.md).

Still not exercised: **vanilla-proxy** divergence (R14's open half). The arena borrows vanilla materials, props
and whole donor rooms, but by cloning live assets at load rather than through authored proxy references, so the
editor/runtime divergence this ADR worried about has no authored proxies to diverge from yet.
