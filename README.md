# False Gods

A SULFUR mod that adds original bosses and dedicated boss-arena maps. It is designed to work in **both**:

- **Vanilla single-player SULFUR**, and
- **[SULFUR Together](https://github.com/ryuka-dev/SULFUR-Together)** multiplayer, where the **host is authoritative** over the
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
[Docs/DependencyRules.md](Docs/DependencyRules.md) for the rules. How those rules are checked mechanically — the
`FG-ARCH-*` rule registry, CI levels, and exception process — lives in
[Docs/ArchitectureEnforcement.md](Docs/ArchitectureEnforcement.md).

**Enforcement status.** Partial, and the document is precise about which part:

- The **project reference graph already gives compile-time protection** for several rules. Core cannot see
  `UnityEngine`; `UnityRuntime` cannot see `FalseGods.Protocol`; only `Integration.Sulfur` can see `0Harmony`;
  `FalseGods.Plugin` cannot see the ST adapter. Using a forbidden type does not compile.
- **Two automated checks gate every pull request.** `FG-ARCH-002` (the plugin must not reference the optional
  ST adapter) and `FG-ARCH-010` (every check cites a registered rule id) are **`Required in CI`** — branch
  protection blocks a merge while either is red. CI runs the game-independent layer via
  `.\scripts\verify.ps1 -CiSafe`: the FG-ARCH-002 **evaluated project-graph** check (evaluated for every
  configuration declared in `Directory.Build.props`) and FG-ARCH-010.
- **What CI cannot build stays local + pre-push.** The FG-ARCH-002 **metadata** layer (reading the compiled
  `AssemblyRef` table of the assembly built by that same run) and a full build of the outer assemblies need the
  game + BepInEx DLLs a CI runner does not have, so they run only in the full `.\scripts\verify.ps1` (optionally
  `-Configuration Release`) and the pre-push hook. A green CI is therefore not a full-green.
- **The remaining eight rules have no automated check yet** (`Planned`). The compiler stops you *using* a
  forbidden type; it does not yet stop you *adding the reference*.

## Repository layout

| Path | Purpose | Committed? |
|------|---------|-----------|
| `Docs/` | Research reports and architecture (see `Docs/README.md`) | ✅ |
| `src/` | The eight module projects — reference lists only, no source yet | ✅ |
| `tests/FalseGods.ArchitectureTests/` | The `FG-ARCH-*` boundary checks | ✅ |
| `tests/Fixtures/` | Synthetic projects that prove the checks detect what they claim | ✅ |
| `tools/FalseGods.Probe/` | Throwaway read-only PoC probe (P0/P1); outside `src/`, deleted after use | ✅ |
| `scripts/verify.ps1` | The one-command local verification loop | ✅ |
| `scripts/setup-dev.ps1` | One-time per-clone hook install (`core.hooksPath`) | ✅ |
| `.githooks/pre-push` | Runs `verify.ps1` before every push; blocks on failure | ✅ |
| `.github/workflows/verify.yml` | CI: the game-independent verify subset on push + PR | ✅ |
| `False Gods.slnx` | Solution | ✅ |
| `global.json` | Pins the .NET SDK the checks were verified against | ✅ |
| `Directory.Build.props` / `.targets` | Shared build settings; machine-path guards | ✅ |
| `LocalPaths.props.example` | Template for machine-specific paths | ✅ |
| `LocalPaths.props` | Your real paths (copy of the example) | ❌ gitignored |
| `Decompiled/` | Local reverse-engineering reference (see `Decompiled/README.md`) | ❌ gitignored |
| `ExtractedAssets/` | Any assets pulled from your local game install | ❌ gitignored |

The `src/` projects map one-to-one onto [Docs/Architecture.md §2](Docs/Architecture.md), and their reference
lists *are* the dependency rules — a forbidden dependency is a compile error, not a review comment.
`FalseGods.Core`, `.Protocol`, `.RuntimeContracts`, and `.Application` build with no game installed at all.

## Prerequisites

| Requirement | Why | Needed by |
|---|---|---|
| **.NET SDK pinned by `global.json`** (10.0.301) | `.slnx` solutions, and MSBuild's `-getItem` evaluated-item output that the architecture checks read | everything |
| **.NET Framework 4.7.2 Developer Pack** (targeting pack) | the plugins target `net472`, matching the game's Unity + Mono profile | building `src/` |
| **SULFUR managed assemblies** (`<SULFUR>\Sulfur_Data\Managed`) | UnityEngine, the game DLLs, A\*, Addressables | the four outer projects |
| **BepInEx 5 core** (`BepInEx\core` of the profile you run) | `BepInEx.dll`, `0Harmony.dll` | the four outer projects |
| **`LocalPaths.props`** | tells the build where the two above live; gitignored, never committed | the four outer projects |

`FalseGods.Core`, `.Protocol`, `.RuntimeContracts`, and `.Application` need only the first two — they build on a
machine with no game and no BepInEx installed, which is what makes the domain unit-testable.

## Setup

1. Copy `LocalPaths.props.example` → `LocalPaths.props` and fill in your paths
   (SULFUR managed dir, SULFUR Together source, BepInEx core/plugins).
   `LocalPaths.props` is gitignored — do not commit it.
2. `.\scripts\setup-dev.ps1` — installs the version-controlled git hooks for this clone (see below).
   Run it **once per clone**.
3. `.\scripts\verify.ps1` — validates the SDK and configuration, builds the solution, runs the architecture
   checks against *that* build, and runs the whitespace checks: `git diff HEAD --check` (staged and unstaged)
   plus the committed range `origin/master...HEAD` — the same range CI checks on a PR (`-BaseRef` overrides
   the base). Takes about twenty seconds. Add `-Configuration Release` to verify that configuration. If a
   required path is missing, the build tells you which one.
4. (Optional) Regenerate the decompile reference — see `Decompiled/README.md`.

### Local pre-push hook

`setup-dev.ps1` points git at the tracked `.githooks/` directory (`git config --local core.hooksPath
.githooks`). That activates **`.githooks/pre-push`**, which runs the full `scripts/verify.ps1` **before every
`git push`** and **blocks the push** if verification fails. It verifies **both Debug and Release on every
push** — because under branch protection nobody pushes `master` directly (GitHub merges the PR server-side,
which never runs this hook), and CI only builds the Debug inner subset, so a feature-branch push is the only
place a full Release build happens before code reaches `master`. The cost is a second full build per push.

- **Install once per clone.** The setting lives in this clone's `.git/config`, so re-run `setup-dev.ps1` after
  a fresh clone, on a new machine, or if the repo's `.git` config is lost or reset. `setup-dev.ps1` is
  idempotent — running it again is harmless.
- **It needs a working build environment**: a valid `LocalPaths.props`, the SULFUR managed assemblies, and
  BepInEx (see Prerequisites). The hook runs the *full* verify, including the outer assemblies — so unlike the
  inner-only checks, it will not pass on a machine without the game DLLs.
- **The hook and GitHub CI cover different things and do not replace each other.** CI runs the
  game-independent subset (`verify.ps1 -CiSafe`) on a machine with no game; the hook runs the full verify
  locally, including the outer assemblies and the FG-ARCH-002 metadata layer that CI cannot build. Passing one
  does not imply the other ([Docs/ArchitectureEnforcement.md §4.1](Docs/ArchitectureEnforcement.md)).
- **`git push --no-verify`** skips the hook. It exists for a deliberate emergency (e.g. pushing a diagnostic
  branch while the build environment is broken), not for normal work — a push that skips verification is a push
  nobody checked.

## Reference environment (verified during investigation)

- Game: SULFUR, **Unity 6000.3.6f1** (Mono `net472`), managed assemblies under
  `…\SULFUR\Sulfur_Data\Managed`. Unity version confirmed at runtime by the PoC probe.
- Mod platform: **BepInEx 5 + HarmonyX**, loaded via UnityDoorstop (`winhttp.dll` + `doorstop_config.ini`),
  managed by the Gale mod manager. Same toolchain as SULFUR Together.
- Navigation: **A\* Pathfinding Project 5.3.8** (recast graph, scanned at runtime) — *not* Unity NavMesh. The
  graph rasterizes **meshes, not colliders** (measured; see Docs report 4.2/4.4).
- Level content: modular **`Room` prefabs** loaded via **Addressables `AssetReference`**, assembled by a
  MakerGraph/XNode node pipeline. Runtime resolution of vanilla room GUIDs from mod code is **verified**
  (RiskList R1).

See `Docs/` for the full analysis and the proof-of-concept plan.

## License / legal

This project ships only original code and original assets. It does **not** redistribute any SULFUR game
assets; vanilla content is referenced and loaded from the end user's own legitimate installation at runtime.
