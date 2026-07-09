# False Gods

A SULFUR mod that adds original bosses and dedicated boss-arena maps. It is designed to work in **both**:

- **Vanilla single-player SULFUR**, and
- **[SULFUR Together](../SULFUR%20Together)** multiplayer, where the **host is authoritative** over the
  boss, the arena, and the combat flow.

> **Status: research + an empty module skeleton.** This repository contains the design documents, a local
> reverse-engineering reference, and the eight project files that encode the architecture's dependency graph.
> **The projects contain no source files**: there is no plugin, boss, arena, or asset yet. The skeleton exists
> first on purpose — the compiler enforces the module boundaries the moment the reference lists exist, so the
> first boss grows inside them rather than being retrofitted into them later. The first implementation target
> is a single boss in a **fixed** arena; procedural arena assembly is a later goal.

## Goals & principles

- Host-authoritative boss / arena / combat; clients own their own input, inventory, and personal state.
- **Reuse** SULFUR Together's existing host-authoritative systems (level/seed sync, boss adapters,
  host-driven enemy proxy, arena lockdown) rather than adding new transport or authority.
- **Reuse vanilla assets at runtime** from the player's own game install (Addressables) instead of
  redistributing vanilla scenes, meshes, textures, or shaders.
- Keep visual geometry, physics collision, and navigation as **decoupled layers**.

## Content-authoring goals

False Gods is not limited to remixing vanilla SULFUR rooms or bosses.

Arena layouts are authored visually in a dedicated Unity project and exported as mod-owned prefabs /
AssetBundles. An arena may freely combine:

- original geometry, materials, shaders, lighting, collision, and gameplay markers;
- runtime-resolved vanilla SULFUR environment prefabs through proxy references;
- original 2D or 3D boss assets;
- original arena mechanisms and phase-specific set pieces.

The Unity-authored arena prefab is the source of truth for the fixed arena layout. Runtime code loads and
realizes that authored content; it should not require hand-writing the full layout as transform data.

## Multiplayer quality goal

SULFUR Together's existing boss support is an interoperability layer for vanilla bosses and is not assumed
to be the final replication model for original False Gods bosses.

False Gods bosses are authored as network-native encounters from the beginning:

- one **host-authoritative simulation with deterministic identifiers and explicit authoritative decisions**;
- a separate presentation layer, driven by project-owned presentation contracts rather than wire DTOs;
- explicit replicated state (`BossSnapshot` / `ArenaSnapshot`) and discrete events (`BossEvent` / `ArenaEvent`);
- an `EncounterBaseline` for join-in-progress and full recovery;
- no client-authoritative phase, damage, death, or attack selection.

**What "deterministic" does and does not mean here.** It does **not** mean Unity physics, A\* recast scans, or
client-side simulation are bit-identical across machines — we never require that, and clients never re-run the
authoritative simulation. It means: stable identifiers (`EncounterId`, `BossInstanceId`, `AttackInstanceId`,
`ArenaId`), a stable event order, idempotent event application, and authoritative decisions that are made
exactly once on the host and replicated as **results**.

The project consumes SULFUR Together's transport, session, player roster, arena readiness, and lockdown
capabilities **through project-owned ports** (never direct dependencies), while defining a purpose-built
replication contract for original bosses.

## Architecture boundaries

Learning from SULFUR Together's system debt (boundaries added too late led to transport/session/boss/UI
coupling), False Gods establishes strict boundaries **before** implementation:

- **Inward dependency rule** — the boss/encounter domain (`FalseGods.Core`) knows nothing of Unity, BepInEx,
  Harmony, SULFUR, A\*, Addressables, SULFUR Together, LiteNetLib, or Steam. Those live in outer integration
  adapters.
- **Core stays narrow** — it holds only the domain and the abstractions the domain itself calls. Asset,
  Addressables, navigation, scene, loading, channel, session, and replication ports live in the outer modules
  whose code actually consumes them.
- **Transport and Steam P2P are invisible** to boss and arena code; adding/replacing a transport changes only
  the SULFUR Together adapter.
- **Presentation never sees a wire DTO** — network snapshots/events are mapped into project-owned
  `PresentationState` / `PresentationEvent` before reaching `BossPresentation`, so single-player and multiplayer
  drive the same presentation entry point.
- **SULFUR Together is optional and never a CLR dependency of the base plugin.**
  `FalseGods.Integration.SulfurTogether` is a separate **companion BepInEx plugin** that references the stable
  `FalseGods.RuntimeContracts`, takes a hard BepInEx (GUID-string) dependency on the base plugin for load
  ordering, and self-registers through a single-slot `FalseGodsIntegrations` broker. `FalseGods.Plugin` never
  names an ST adapter type. Single-player runs with the adapter absent.
- Vanilla `BossFightHelper` / `BossPhase` / `IBossEncounterAdapter` are **reverse-engineering references, not
  base classes** for original bosses.

See [Docs/Architecture.md](Docs/Architecture.md) for the structure and
[Docs/DependencyRules.md](Docs/DependencyRules.md) for the rules. How those rules will be checked mechanically —
the `FG-ARCH-*` rule registry, CI levels, and exception process — lives in
[Docs/ArchitectureEnforcement.md](Docs/ArchitectureEnforcement.md); none of it is implemented yet.

## Repository layout

| Path | Purpose | Committed? |
|------|---------|-----------|
| `Docs/` | Research reports and architecture (see `Docs/README.md`) | ✅ |
| `src/` | The eight module projects — reference lists only, no source yet | ✅ |
| `False Gods.slnx` | Solution | ✅ |
| `Directory.Build.props` / `.targets` | Shared build settings; machine-path guards | ✅ |
| `LocalPaths.props.example` | Template for machine-specific paths | ✅ |
| `LocalPaths.props` | Your real paths (copy of the example) | ❌ gitignored |
| `Decompiled/` | Local reverse-engineering reference (see `Decompiled/README.md`) | ❌ gitignored |
| `ExtractedAssets/` | Any assets pulled from your local game install | ❌ gitignored |

The `src/` projects map one-to-one onto [Docs/Architecture.md §2](Docs/Architecture.md), and their reference
lists *are* the dependency rules — a forbidden dependency is a compile error, not a review comment.
`FalseGods.Core`, `.Protocol`, `.RuntimeContracts`, and `.Application` build with no game installed at all.

## Setup

1. Copy `LocalPaths.props.example` → `LocalPaths.props` and fill in your paths
   (SULFUR managed dir, SULFUR Together source, BepInEx core/plugins).
2. `dotnet build "False Gods.slnx"`.
   The four inner projects need nothing; the four outer ones will tell you which path is missing.
3. (Optional) Regenerate the decompile reference — see `Decompiled/README.md`.

## Reference environment (verified during investigation)

- Game: SULFUR (Unity, Mono `net472`), managed assemblies under
  `…\SULFUR\Sulfur_Data\Managed`.
- Mod platform: **BepInEx 5 + HarmonyX**, loaded via UnityDoorstop (`winhttp.dll` + `doorstop_config.ini`),
  managed by the Gale mod manager. Same toolchain as SULFUR Together.
- Navigation: **A\* Pathfinding Project** (recast graph, scanned at runtime) — *not* Unity NavMesh.
- Level content: modular **`Room` prefabs** loaded via **Addressables `AssetReference`**, assembled by a
  MakerGraph/XNode node pipeline.

See `Docs/` for the full analysis and the proof-of-concept plan.

## License / legal

This project ships only original code and original assets. It does **not** redistribute any SULFUR game
assets; vanilla content is referenced and loaded from the end user's own legitimate installation at runtime.
