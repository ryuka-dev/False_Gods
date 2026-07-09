# ADR-001 — Unity prefab as arena source of truth

**Status:** Proposed

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
Unverified — depends on AssetBundle load (PoC P2) and runtime parity (RiskList R14).
