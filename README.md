# False Gods

A SULFUR mod that adds original bosses and dedicated boss-arena maps. It is designed to work in **both**:

- **Vanilla single-player SULFUR**, and
- **[SULFUR Together](https://github.com/ryuka-dev/SULFUR-Together)** multiplayer, where the **host is authoritative** over the
  boss, the arena, and the combat flow.

> **Status: the first boss encounter is playable end to end**, in vanilla single-player and in
> host-authoritative SULFUR Together multiplayer. Verified in game: the fight itself on two machines, the newest
> way in and out mostly on one. What is **not** here: a second boss, a public modding API for adding one, and
> procedural arena assembly. The arena is a fixed, hand-authored room — that was always the first target, and it
> is the one that is met.

## What works today

Written for someone about to change this code, not as a feature list. The fight's own mechanics are deliberately
left out; [Docs/BossEncounterRunbook.md](Docs/BossEncounterRunbook.md) is where they are written down.

- **One boss encounter, reachable in ordinary play.** Kill the vanilla cave boss and a lit portal fades up in its
  own room; walking into it takes the whole session to the arena. Killing the False God opens the way back out,
  and the payout is waiting on the other side.
- **No developer hotkeys.** The only other way in is a row in the game's own developer menu, which sits behind
  developer mode and so is not a control that has to be taken away before a release.
- **Both compositions run.** Single-player with the multiplayer adapter absent, and host-authoritative
  multiplayer through it. The boss, the arena, the summons, the room's machinery and the encounter phase are the
  host's; each peer plays its own presentation off replicated facts, and personal state (inventory, loot, health)
  stays personal.
- **The arena is delivered by driving the game's own level generation** rather than added on top of a level, so
  the game scans the navigation, spawns the player and applies the fog natively. The room itself is a
  hand-sculpted cave authored in Blender and Unity, shipped as a mod-owned AssetBundle.
- **Vanilla content is borrowed at runtime, never redistributed** — materials, props, and whole donor rooms are
  resolved from the player's own install through Addressables. The only art the assembly carries is ours.
- **The boundaries held while the code grew inside them.** All eight modules now carry real source, and the four
  game-independent ones still build and unit-test on a machine with no game and no BepInEx installed at all —
  which is the boundary doing its job rather than a claim about it.

## Goals & principles

- Host-authoritative boss / arena / combat; clients own their own input, inventory, and personal state.
- **Reuse** SULFUR Together's existing host-authoritative systems (level/seed sync, host-driven enemy proxy,
  arena lockdown) rather than adding new transport or authority. Its *vanilla boss* adapters are a reference,
  not a base — see below.
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
coupling), False Gods drew its boundaries **before** any implementation, and the encounter was then grown inside
them rather than retrofitted into them:

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
- **Five checks run on every push and block the local pre-push hook.** The evaluated **project-graph** layer of
  `FG-ARCH-002` (the plugin must not reference the optional ST adapter), `FG-ARCH-003`, `FG-ARCH-005` and
  `FG-ARCH-006`, plus `FG-ARCH-010` (every check cites a registered rule id), run in CI via
  `.\scripts\verify.ps1 -CiSafe` — the graph ones for every configuration declared in `Directory.Build.props`.
  Branch protection was removed, so CI is a visible re-check, not a merge gate; the pre-push hook (full
  `verify.ps1`) is what blocks a red push.
- **What CI cannot build stays local + pre-push.** The FG-ARCH-002 **metadata** layer (reading the compiled
  `AssemblyRef` table of the assembly built by that same run) and `FG-ARCH-011`'s field-signature scan need a
  built adapter DLL, and a full build of the outer assemblies needs the game + BepInEx DLLs a CI runner does not
  have — so they run only in the full `.\scripts\verify.ps1` (optionally `-Configuration Release`) and the
  pre-push hook. A green CI is therefore not a full-green.
- **Ten of the layers the rules name still have no check** (`Planned`), and **no rule is enforced at every layer
  it names.** The compiler stops you *using* a forbidden type; for most rules it does not yet stop you *adding
  the reference*. `Docs/ArchitectureEnforcement.md` carries the per-layer table and is the authority — this
  summary is not.

## Repository layout

| Path | Purpose | Committed? |
|------|---------|-----------|
| `Docs/` | Research reports, the architecture, and the boss-encounter runbook (see `Docs/README.md`) | ✅ |
| `src/` | The eight module projects; their reference lists *are* the dependency graph | ✅ |
| `tests/FalseGods.CoreTests/` · `.ProtocolTests/` · `.ApplicationTests/` · `.RuntimeContractsTests/` | Unit tests for the four game-independent modules | ✅ |
| `tests/FalseGods.ArchitectureTests/` | The `FG-ARCH-*` boundary checks | ✅ |
| `tests/Fixtures/` | Synthetic projects that prove the checks detect what they claim | ✅ |
| `FalseGods.Unity/` | Unity authoring project: the arena prefab, its materials, and the editor tools that build and deploy the bundle | ✅ |
| `FalseGods.Unity/Build/` | The built arena bundle + content artifact | ❌ gitignored |
| `BlenderWork/` | Blender source for the hand-sculpted cave shell | ✅ |
| `tools/FalseGods.Probe/` | Read-only in-game probe (F4–F11), outside `src/` and the FG-ARCH rules on purpose; how a measurement gets re-taken on a new game version | ✅ |
| `CONTRIBUTING.md` | The authoritative git / PR flow | ✅ |
| `scripts/verify.ps1` | The one-command local verification loop | ✅ |
| `scripts/setup-dev.ps1` | One-time per-clone hook install (`core.hooksPath`) | ✅ |
| `scripts/submit-pr.ps1` | Optional PR submission, for when a change wants review or a CI record | ✅ |
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
| **Unity 6000.3.6f1** | the game's own Unity version — a bundle built by any other one may not load | building the arena content |

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
push** — because this hook is now the blocking gate (`master` is pushed directly), and CI only builds the Debug
inner subset and no longer blocks anything server-side, so this hook is the only place a full Release build
happens before code reaches `master`. The cost is a second full build per push.

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

## Getting it into the game

`verify.ps1` builds and checks; it deliberately never writes outside the repository. Putting the mod in a
BepInEx profile is a separate, opt-in step, and it needs `BepInExPluginDir` set in `LocalPaths.props`.

1. **Build the arena content first.** The arena is authored in `FalseGods.Unity` and shipped as a mod-owned
   AssetBundle plus a content artifact, both build outputs under `FalseGods.Unity\Build\` (gitignored). In the
   Unity editor: **False Gods → Build PoC AssetBundle**, or **False Gods → Deploy Arena to Game** (`Ctrl+Shift+D`),
   which rebuilds and copies to every profile listed in `LocalDeployTargets.txt` at the Unity project root.
   Content can be replaced under a running game — move a prop, deploy, reload the level — because only the
   plugin DLLs are held open.
2. **Deploy the plugin.** Copies the plugin, every `FalseGods.*.dll` it needs, and the arena bundle + artifact
   into `<BepInExPluginDir>\FalseGods`:
   ```
   dotnet build src/FalseGods.Plugin/FalseGods.Plugin.csproj -p:DeployPlugin=true
   ```
   A missing bundle or artifact is **reported, not fatal** — the arena flow then fails closed at runtime with
   "content unavailable" rather than half-loading a room.
3. **Deploy the multiplayer companion** (optional — single-player runs with it absent):
   ```
   dotnet build src/FalseGods.Integration.SulfurTogether -p:DeployAdapter=true
   ```
   Into its **own** folder, `<BepInExPluginDir>\FalseGods.SulfurTogether`, and deliberately so: step 2 already
   put the shared assemblies under `FalseGods`, and two copies of one assembly identity loaded from two paths is
   its own class of bug. It takes hard BepInEx GUID-string dependencies — on the base plugin, for load ordering,
   and on SULFUR Together itself — so neither which folder loads first nor whether ST is installed is left to
   chance.
4. **Two instances at once.** Set `BepInExClientPluginDir` in `LocalPaths.props` and every deploy above also
   copies to a second profile — what a host + client test on one machine needs.
5. **The probe**, when a measurement needs re-taking:
   `dotnet build tools/FalseGods.Probe/FalseGods.Probe.csproj -p:DeployProbe=true`. It loads its own copy of the
   arena bundle, so an unexpected room in an ordinary level is usually the probe and not the mod.

## Reference environment (verified during investigation)

- Game: **SULFUR v0.18.5**, **Unity 6000.3.6f1** (Mono `net472`), managed assemblies under
  `…\SULFUR\Sulfur_Data\Managed`. Unity version confirmed at runtime by the PoC probe. Every measurement the
  docs cite is pinned to a game version, because that is the only way to tell a stale one from a wrong one —
  see [Docs/BossEncounterRunbook.md](Docs/BossEncounterRunbook.md) for the pinned values and how to re-take them.
- Mod platform: **BepInEx 5 + HarmonyX**, loaded via UnityDoorstop (`winhttp.dll` + `doorstop_config.ini`),
  managed by the Gale mod manager. Same toolchain as SULFUR Together.
- Navigation: **A\* Pathfinding Project 5.3.8** (recast graph, scanned at runtime) — *not* Unity NavMesh. The
  graph rasterizes **meshes, not colliders** (measured; see Docs report 4.2/4.4).
- Level content: modular **`Room` prefabs** loaded via **Addressables `AssetReference`**, assembled by a
  MakerGraph/XNode node pipeline. Runtime resolution of vanilla room GUIDs from mod code is **verified**
  (RiskList R1).

## Where to read next

- **[Docs/BossEncounterRunbook.md](Docs/BossEncounterRunbook.md)** — how a boss encounter and its arena actually
  get built, in the order that works, with the measurements pinned to a game version and the traps that cost
  real time. **Read this before building the next one.**
- **[CONTRIBUTING.md](CONTRIBUTING.md)** — the authoritative git flow: `master` is pushed directly, the pre-push
  hook is the gate, and pull requests are optional.
- **[Docs/DefinitionOfDone.md](Docs/DefinitionOfDone.md)** — the completion gates a change has to clear.
- **[Docs/README.md](Docs/README.md)** — the full investigation record (reports 1–9), the ADRs, and the reasoning
  behind the decisions above.

## License / legal

This project ships only original code and original assets. It does **not** redistribute any SULFUR game
assets; vanilla content is referenced and loaded from the end user's own legitimate installation at runtime.
