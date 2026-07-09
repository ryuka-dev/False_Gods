# False Gods — Investigation Docs

Feasibility investigation for building an original boss **arena map** for SULFUR that works in vanilla
single-player and in host-authoritative SULFUR Together multiplayer. **Investigation only** — no
implementation yet.

All claims are grounded in the decompiled game assemblies (`../Decompiled/`, gitignored) and in SULFUR
Together's own docs/source. Concrete type/method names are cited; runtime behaviour is marked *proposed /
unverified* until validated by the proof-of-concept.

## Reports

1. **[ArenaResourceArchitecture.md](ArenaResourceArchitecture.md)** — How SULFUR cave levels are organized
   and which pieces are reusable.
2. **[ArenaLoadingProposal.md](ArenaLoadingProposal.md)** — How to load a custom fixed arena and resolve
   vanilla assets at runtime (proxy → real-asset).
3. **[MaterialCompatibilityReport.md](MaterialCompatibilityReport.md)** — Cave material/shader dependencies,
   risks, and the safe reuse path.
4. **[CollisionAndNavigationProposal.md](CollisionAndNavigationProposal.md)** — Collision layers, the A\*
   recast graph, boss pathing, and runtime teardown.
5. **[MultiplayerLoadingContract.md](MultiplayerLoadingContract.md)** — Host/Client responsibilities mapped
   onto SULFUR Together's existing systems.
6. **[RiskList.md](RiskList.md)** — Ranked unknowns and their cheapest first-validation.
7. **[MinimalProofOfConceptPlan.md](MinimalProofOfConceptPlan.md)** — The ~20×20 m test room and its
   pass/fail criteria.
8. **[OriginalContentPipeline.md](OriginalContentPipeline.md)** — Unity project, original assets, prefabs,
   shaders, materials, sprites, bundles, and editor-to-runtime workflow.
9. **[OriginalBossNetworkingArchitecture.md](OriginalBossNetworkingArchitecture.md)** — The purpose-built
   host-authoritative replication model for False Gods bosses.

### Architecture & process (boundaries before implementation)

- **[Architecture.md](Architecture.md)** — module boundaries, inward dependency direction, ports, and the
  Boss/Arena/Encounter split.
- **[DependencyRules.md](DependencyRules.md)** — allowed/forbidden dependencies and the mechanical-enforcement
  plan.
- **[DefinitionOfDone.md](DefinitionOfDone.md)** — completion gates and the AI-development process rules.
- **[ADRs/](ADRs/README.md)** — architecture decision records (ADR-001 … ADR-006).

## TL;DR of the key findings

- **Levels are modular `Room` prefabs** (`Structure` + `Decoration`), grouped by `LevelBlock`
  (`List<AssetReference> roomPrefabsAddressable`), sequenced by a MakerGraph node pipeline. Rooms load via
  **Addressables** (`AssetReference.LoadAssetAsync<GameObject>()`), so vanilla props/walls can be resolved
  and instantiated at runtime from the player's install — no redistribution needed.
- **Navigation is the A\* Pathfinding Project**, *not* Unity NavMesh. A persistent `AstarPath.active` recast
  graph is **re-scanned at runtime** each level; enemies move via `AiAgent` + `CustomRichAI`. A custom arena
  just needs its walkable geometry present when the recast graph scans (or a prebaked `NavmeshPrefab`).
- **Vanilla bosses are `Npc` units driven by a `BossFightHelper` + `BossPhase`** — these are
  **reverse-engineering references, not base classes**. Original bosses are built from `FalseGods.Core`
  types (Simulation / Presentation / Replication), not by subclassing vanilla helpers.
- **SULFUR Together provides the multiplayer spine** — host owns level+seed, `NetLevelManifest` diffing,
  host-driven enemy proxy, and a full `ArenaLockdownManager` (seal/barrier/teleport). False Gods **consumes
  these through project-owned ports** in an optional `FalseGods.Integration.SulfurTogether` adapter — never by
  direct dependency — and treats the vanilla boss adapters (`IBossEncounterAdapter`/`NetBossEncounterManager`)
  as reference only.
- **Boundaries before implementation.** `FalseGods.Core` is independent of Unity/SULFUR/BepInEx/Harmony/A\*/
  Addressables/networking; transport and Steam are invisible to boss/arena code; SULFUR Together is optional.
  See Architecture.md / DependencyRules.md.
- **Unity prefab authoring is the intended production workflow.** Fixed arenas are built and previewed
  visually in a matching-version Unity project, then loaded as mod-owned prefab/AssetBundle content.
  Vanilla proxies are optional elements inside that prefab, not the primary layout format.
- **Original bosses will use a network-native replication architecture.** Existing SULFUR Together boss
  adapters remain useful references and infrastructure, but original bosses are not constrained to the
  imperfect compatibility model required for vanilla boss synchronization.
- **Highest-risk unknowns**: Addressables key stability & shader-variant coverage for reused assets; getting
  a custom mesh cleanly into the recast scan without the `NavMeshCleaner` flood-fill discarding it; and clean
  teardown so arena nav/objects don't leak into the next level. Validate these first (see RiskList + PoC).

> Reference environment (verified from game files): **Unity 6000.3.6f1**, **URP** (Universal Render
> Pipeline), with URP 2D renderer, ShaderGraph, VFX Graph, 2D Animation, and Timeline available.
