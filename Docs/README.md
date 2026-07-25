# False Gods — Investigation Docs

An original boss **arena map** for SULFUR that works in vanilla single-player and in host-authoritative SULFUR
Together multiplayer.

**Where the project stands.** The first encounter is playable in single-player: a hand-authored cave arena is
delivered by driving the game's own level generation, so the game builds its navigation, spawns the player and
applies the fog natively; the boss fights, takes real weapon fire, damages the player, and throws the game's own
crates; enemies path the cave and jump between its terraces. **Multiplayer is the open work** — the client path
still uses the additive arena load from before the level hijack. Reports 1–9 below are the feasibility
investigation that preceded all of this and are kept as the reasoning record; where implementation has since
moved past them, the runbook is the current word.

All claims are grounded in the decompiled game assemblies (`../Decompiled/`, gitignored) and in SULFUR
Together's own docs/source. Concrete type/method names are cited; runtime behaviour is marked *proposed /
unverified* until validated in game.

## Start here

- **[BossEncounterRunbook.md](BossEncounterRunbook.md)** — how a boss encounter and its arena get built, in the
  order that works: the production sequence and what closes each step, the measurements pinned to a game version
  with how to re-take them, and the traps that cost real time. Read this before building the next arena.

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

- **[Architecture.md](Architecture.md)** — module boundaries, inward dependency direction, ports, the
  optional-integration seam, and the Boss/Arena/Encounter split.
- **[DependencyRules.md](DependencyRules.md)** — what is allowed and forbidden (the rules themselves).
- **[ArchitectureEnforcement.md](ArchitectureEnforcement.md)** — how those rules get checked: the `FG-ARCH-*`
  rule registry, CI levels, exception process, and current status. `FG-ARCH-002` and `FG-ARCH-010` have working
  checks (`.\scripts\verify.ps1`), run in CI on every push, and block the local pre-push hook — the FG-ARCH-002
  project-graph layer and FG-ARCH-010; the other eight rules are `Planned`. Branch protection was removed, so
  CI no longer blocks anything server-side; the pre-push hook is the gate.
- **[DefinitionOfDone.md](DefinitionOfDone.md)** — completion gates and the development process rules.
- **[ADRs/](ADRs/README.md)** — architecture decision records (ADR-001 … ADR-006).

## TL;DR of the key findings

- **Levels are modular `Room` prefabs** (`Structure` + `Decoration`), grouped by `LevelBlock`
  (`List<AssetReference> roomPrefabsAddressable`), sequenced by a MakerGraph node pipeline. Rooms load via
  **Addressables** (`AssetReference.LoadAssetAsync<GameObject>()`), so vanilla props/walls can be resolved
  and instantiated at runtime from the player's install — no redistribution needed.
- **Navigation is the A\* Pathfinding Project**, *not* Unity NavMesh. `AstarPath.active` is shared global state
  for the **currently active level**, built at runtime rather than baked; a normal level change **rebuilds** it
  (`ClearGraphs()` + `Destroy` at `GameManager.cs:1097`, `Instantiate(astarPathPrefab)` at `:1137`). Enemies move
  via `AiAgent` + `CustomRichAI`. A custom arena just needs its walkable geometry present when the recast graph
  scans (or a prebaked `NavmeshPrefab`) — and must clean up its own nodes/links on exit rather than waiting for
  the next level to hide the leak.
- **Vanilla bosses are `Npc` units driven by a `BossFightHelper` + `BossPhase`** — these are
  **reverse-engineering references, not base classes**. Original bosses are built from `FalseGods.Core`
  types (Simulation / Presentation / Replication), not by subclassing vanilla helpers.
- **SULFUR Together provides the multiplayer spine** — host owns level+seed, `NetLevelManifest` diffing,
  host-driven enemy proxy, and a full `ArenaLockdownManager` (seal/barrier/teleport). False Gods **consumes
  these through project-owned ports** in an optional `FalseGods.Integration.SulfurTogether` adapter — never by
  direct dependency — and treats the vanilla boss adapters (`IBossEncounterAdapter`/`NetBossEncounterManager`)
  as reference only. Most of ST's relevant types are `internal` with no `[InternalsVisibleTo]`, so the adapter
  reflects, or ST grows a public bridge.
- **Boundaries before implementation.** `FalseGods.Core` is independent of Unity/SULFUR/BepInEx/Harmony/A\*/
  Addressables/networking, and holds only the abstractions the domain itself calls — asset, navigation, session,
  channel, and replication ports live further out. Transport and Steam are invisible to boss/arena code.
  Presentation is driven by `PresentationState`/`PresentationEvent`, never by wire DTOs. The ST adapter is
  optional and is **not a CLR dependency of the base plugin**. See Architecture.md / DependencyRules.md.
- **Unity prefab authoring is the intended production workflow.** Fixed arenas are built and previewed
  visually in a matching-version Unity project, then loaded as mod-owned prefab/AssetBundle content.
  Vanilla proxies are optional elements inside that prefab, not the primary layout format.
- **Original bosses will use a network-native replication architecture.** Existing SULFUR Together boss
  adapters remain useful references and infrastructure, but original bosses are not constrained to the
  imperfect compatibility model required for vanilla boss synchronization. Boss and arena replication state are
  **separate** (`BossSnapshot`/`ArenaSnapshot`, `BossEvent`/`ArenaEvent`), composed by `EncounterBaseline` for
  late join.
- **"Host-authoritative" does not mean "cross-machine deterministic".** Unity physics, A\* scans, and client
  code are never required to be bit-identical; clients never re-run the authoritative simulation. Determinism is
  required of identifiers, per-stream event order, idempotent application, and once-only authoritative decisions.
- **Highest-risk unknowns**: Addressables key stability & shader-variant coverage for reused assets; getting
  a custom mesh cleanly into the recast scan without the `NavMeshCleaner` flood-fill discarding it; and clean
  teardown so arena nav/objects don't leak into the next level. Validate these first (see RiskList + PoC).

> Reference environment: **Unity 6000.3.6f1** (confirmed at runtime by the PoC probe), **URP** (Universal
> Render Pipeline), with URP 2D renderer, ShaderGraph, VFX Graph, 2D Animation, and Timeline available;
> **A\* Pathfinding Project 5.3.8**. PoC steps P0/P1/P2 have been run in-game — RiskList R1 verified, R2
> verified (our own 6000.3.6f1 bundle loads with meshes/materials/layers intact), R3 verified (with a
> design-changing finding), R5 mechanism confirmed; see report 4.2/4.4 and report 7 §7.2.
