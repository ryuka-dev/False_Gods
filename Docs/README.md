# False Gods — Investigation Docs

An original boss **arena map** for SULFUR that works in vanilla single-player and in host-authoritative SULFUR
Together multiplayer.

**Where the project stands.** The first encounter is playable end to end, in single-player **and** in
host-authoritative multiplayer. A hand-authored cave arena is delivered by driving the game's own level
generation, so the game scans its navigation, spawns the player and applies the fog natively; the boss is
announced on the game's own boss bar, takes real weapon fire, fights back, and the room fights with it. It is
reached in ordinary play — a portal in the vanilla cave boss's own room — and left through a doorway the boss
blows open. A client's hits arrive as intents and the host answers with results; a peer joining mid-fight rebuilds
what it missed.

Reports 1–9 below are the feasibility investigation that preceded all of this and are kept as **the reasoning
record, not the current word**. Where implementation has moved past them, the runbook is authoritative; where a
decision has been overtaken, the ADR's *Verification status* says so. This has already happened once in a way
worth knowing about: report 4 chose a prebaked navmesh, and the arena that shipped does not need one
([ADR-002](ADRs/ADR-002-AStar-Recast-Integration.md)).

All claims are grounded in the decompiled game assemblies (`../Decompiled/`, gitignored) and in SULFUR
Together's own docs/source. Concrete type/method names are cited; runtime behaviour is marked *proposed /
unverified* until validated in game.

## Start here

- **[BossEncounterRunbook.md](BossEncounterRunbook.md)** — how a boss encounter and its arena get built, in the
  order that works: the production sequence and what closes each step, the measurements pinned to a game version
  with how to re-take them, and the traps that cost real time. Read this before building the next arena.

## Planned work

- **[FarmExpansionRoadmap.md](FarmExpansionRoadmap.md)** — a farm plot in the church hub, worked by hand first
  and by an enslaved goblin later, with crops advancing per level cleared. **Design only — nothing is built.**
  Records the settled decisions, the phase order, and the open questions, chief among them whether a custom item
  type can exist at all.

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
  rule registry, CI levels, exception process, and current status. **It is the authority on enforcement status
  and no summary elsewhere is** — including this one. In short: five checks run in CI on every push and block the
  local pre-push hook (the project-graph layer of `FG-ARCH-002`, plus `-003`, `-005`, `-006`, `-010`);
  `FG-ARCH-002`'s metadata layer and `FG-ARCH-011` need a built adapter DLL and so run in the local full verify
  only; ten of the layers the rules name have no check at all. Branch protection was removed, so CI no longer
  blocks anything server-side — the pre-push hook is the gate.
- **[DefinitionOfDone.md](DefinitionOfDone.md)** — completion gates and the development process rules.
- **[ADRs/](ADRs/README.md)** — architecture decision records (ADR-001 … ADR-006), all six accepted and
  implemented; each one's *Verification status* is the part kept current.

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
  reflects, or ST grows a public bridge. **ST grew one** — a mod-neutral `SULFURTogether.Api` surface (channel,
  session, host-owned spawns, shared destructibles, player life) that this adapter rides with no reflection; only
  seal/teleport and remote NPC activation are still unbridged ([ADR-004](ADRs/ADR-004-Optional-Sulfur-Together-Adapter.md)).
- **Boundaries before implementation.** `FalseGods.Core` is independent of Unity/SULFUR/BepInEx/Harmony/A\*/
  Addressables/networking, and holds only the abstractions the domain itself calls — asset, navigation, session,
  channel, and replication ports live further out. Transport and Steam are invisible to boss/arena code.
  Presentation is driven by `PresentationState`/`PresentationEvent`, never by wire DTOs. The ST adapter is
  optional and is **not a CLR dependency of the base plugin**. See Architecture.md / DependencyRules.md.
- **Unity prefab authoring is the intended production workflow.** Fixed arenas are built and previewed
  visually in a matching-version Unity project, then loaded as mod-owned prefab/AssetBundle content.
  Vanilla proxies are optional elements inside that prefab, not the primary layout format.
- **Original bosses use a network-native replication architecture** (built, and verified on two machines).
  Existing SULFUR Together boss adapters remain useful references and infrastructure, but original bosses are not
  constrained to the imperfect compatibility model required for vanilla boss synchronization. Boss and arena
  replication state are **separate** (`BossSnapshot`/`ArenaSnapshot`, `BossEvent`/`ArenaEvent`), composed by
  `EncounterBaseline` for late join. In practice every change a player can notice goes on the wire **twice** — as
  the reliable event it is *played* with, and as the snapshot field a peer that missed it *corrects from*.
- **"Host-authoritative" does not mean "cross-machine deterministic".** Unity physics, A\* scans, and client
  code are never required to be bit-identical; clients never re-run the authoritative simulation. Determinism is
  required of identifiers, per-stream event order, idempotent application, and once-only authoritative decisions.
- **The three highest-risk unknowns are all closed**, and how they closed is worth knowing before trusting an
  old proposal here. *Addressables key stability* — verified; vanilla prefabs, materials and whole rooms resolve
  and instantiate from the player's install. *Shader variants* — our own stock-URP materials **did** render pink,
  exactly as report 3 predicted, and the fix that shipped is to borrow a vanilla material rather than ship
  variants. *Getting a mesh into the recast scan* — solved twice over, and the way that shipped is not the way
  report 4 chose: the arena is delivered as the level, so the game's own scan rasterizes it and the
  `NavMeshCleaner` question never arises. Teardown restores what it overwrote. See RiskList for the current per-risk
  state.

> Reference environment: game **SULFUR v0.18.5**, **Unity 6000.3.6f1** (confirmed at runtime by the PoC probe),
> **URP** (Universal Render Pipeline), with URP 2D renderer, ShaderGraph, VFX Graph, 2D Animation, and Timeline
> available; **A\* Pathfinding Project 5.3.8**. The whole PoC (P0 … P9) has been run in-game; the per-step results
> are in [MinimalProofOfConceptPlan.md §7.2](MinimalProofOfConceptPlan.md) and the per-risk outcomes in
> [RiskList.md](RiskList.md). Measurements taken since then are pinned to a game version in
> [BossEncounterRunbook.md](BossEncounterRunbook.md), with how to re-take each one.
