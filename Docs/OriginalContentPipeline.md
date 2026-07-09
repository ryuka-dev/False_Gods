# 8. Original Content Pipeline

*The Unity project, original assets, prefabs, and editor→runtime workflow for False Gods.* Companion to
[ArenaLoadingProposal.md](ArenaLoadingProposal.md) (how arenas load) and
[MaterialCompatibilityReport.md](MaterialCompatibilityReport.md) (material/shader risks).

> **Guiding principle:** *Original content is the primary authored content. Vanilla runtime resolution is
> optional environment reuse.* The False Gods Unity project and its AssetBundles carry original meshes,
> materials, shaders, sprites, VFX, animation, audio, and the arena/boss prefabs — and never redistribute
> vanilla SULFUR assets.

All runtime behaviour here is **proposed/unverified** until exercised by the PoC (report 7). Facts read
directly from the game files are marked *(verified from game files)*.

## 8.1 Target engine (verified from game files)

- **Unity 6000.3.6f1** — read from `Sulfur_Data/globalgamemanagers` and `UnityPlayer.dll` (ProductVersion
  `6000.3.6f1`). The False Gods Unity project **must** use this exact editor version; AssetBundle
  compatibility is version-sensitive.
- **Render pipeline: URP (Universal Render Pipeline)** — `Unity.RenderPipelines.Universal.Runtime`,
  `Unity.RenderPipeline.Universal.ShaderLibrary`, `Unity.RenderPipelines.Universal.Shaders` in the game's
  managed assemblies.
- Available in-engine (shipped assemblies): **URP 2D renderer** (`Unity.RenderPipelines.Universal.2D.Runtime`),
  **ShaderGraph** (`Unity.RenderPipelines.ShaderGraph.ShaderGraphLibrary`), **VFX Graph** (`UnityEngine.VFXModule`),
  **2D Animation / skeletal 2D** (`Unity.2D.Animation.Runtime`), **SpriteShape**, **Timeline**, and the
  **A\* Pathfinding Project** (`AstarPathfindingProject.dll`).
- **Still to confirm in the PoC:** URP asset/renderer settings the game runs with (forward vs deferred, HDR,
  MSAA, renderer features), and which URP package version matches Unity 6000.3.6f1 (RiskList R2/R13).

## 8.2 Recommended Unity project layout

A dedicated project, separate from the BepInEx plugin solution:

```text
FalseGods.Unity/
├─ Assets/FalseGods/Core          # shared runtime components authored in-editor (ArenaRoot, markers, proxy)
├─ Assets/FalseGods/Arenas        # one folder per arena; the authored ArenaRoot prefab is the source of truth
├─ Assets/FalseGods/Bosses        # boss prefabs (3D or 2D), animation, presentation rigs
├─ Assets/FalseGods/Materials     # original materials
├─ Assets/FalseGods/Shaders       # original shaders (+ ShaderVariantCollection assets)
├─ Assets/FalseGods/VFX           # original VFX Graph / particle systems
├─ Assets/FalseGods/Audio         # original audio
├─ Assets/FalseGods/Editor        # editor-only tools: proxy preview, bundle build, manifest export
├─ Assets/FalseGods/VanillaProxies# VanillaAssetProxy definitions + editor-only preview placeholders
└─ Assets/FalseGods/Debug         # DebugRoot helpers, gizmos, test scenes (never shipped)
```

Rules:
- `Assets/FalseGods/Debug` and everything under `Editor/` are **editor/dev only** and excluded from shipped
  bundles.
- Locally extracted vanilla assets (for reference/preview) live **outside** shipped folders (e.g. a
  gitignored `ExtractedAssets/` at repo root) and are **never** packed into a published bundle.

## 8.3 Arena prefab spec

The arena is authored as one `ArenaRoot` prefab (hierarchy in
[ArenaLoadingProposal.md §2.2](ArenaLoadingProposal.md)). Requirements:
- `VisualRoot` (OriginalGeometry / OriginalProps / OriginalBossSetPieces / VanillaAssetProxies),
  `CollisionRoot`, `NavigationRoot`, `LightingRoot`, `GameplayRoot` (PlayerSpawns, BossSpawn, ArenaBoundary,
  ArenaMechanisms, PhaseObjects, Exit), `DebugRoot`.
- Carries an `ArenaId` + `ArenaVersion` (matches the `ArenaManifest` in
  [MultiplayerLoadingContract.md §5.2](MultiplayerLoadingContract.md)).
- An **editor tool exports an authored arena manifest** (hierarchy + transforms + proxy keys) so runtime can
  verify parity after load and proxy replacement (RiskList R14). The same export produces the `ContentHash`
  that peers exchange in `ArenaReady` — matching `ArenaVersion` names the arena, matching `ContentHash` proves
  two peers realized the same one.

## 8.4 Boss prefab spec

- A boss prefab separates **presentation** from **simulation/replication** logic (the code layers live in the
  plugin, not the bundle; the prefab supplies renderers, rigs, animation, VFX, audio, and marker transforms).
- Required marker transforms: hitboxes / weak points, muzzle / attack-origin points, and (for 2D) per-part
  pivots. These are the attachment points the simulation and replication reference.
- 2D bosses: pick a rendering path per [MaterialCompatibilityReport.md §3.9](MaterialCompatibilityReport.md)
  and pin camera-facing, world scale, pivots, sorting, and hitbox attachment in the prefab.

## 8.5 AssetBundle strategy

- **Grouping:** at least one bundle per arena and per boss, plus a shared bundle for common
  materials/shaders/VFX to avoid duplication. Keep boss and arena separable so a boss can appear in more than
  one arena.
- **Dependencies:** shared shaders/materials in a common bundle that arena/boss bundles depend on; avoid
  pulling the same shader into multiple bundles (duplicate-asset bloat and variant divergence).
- **Shaders:** every original shader must be preserved through a **ShaderVariantCollection** (or equivalent)
  so URP variant stripping does not drop the variants the material needs at runtime (RiskList R13). Prefer
  ShaderGraph (shipped by the game) over hand-written shaders where possible.
- **Versioning:** stamp each bundle with a **bundle format version** and each content prefab with its content
  version (`ArenaVersion`, boss `DefinitionId` + version). A host/client bundle-version mismatch is an
  explicit refusal, never a silent divergence.
- **No vanilla assets** in any published bundle (legal + variant-stripping) — vanilla content is resolved at
  runtime via Addressables (report 2).

## 8.6 Editor-to-runtime workflow

1. **Author** the arena/boss prefab visually; place original assets directly; add `VanillaAssetProxy` only
   where vanilla art is intentionally reused.
2. **Preview** proxies in-editor via a placeholder, a local-only extracted preview mesh, or an editor tool
   that shows the referenced asset (preview assets are dev-only, never shipped).
3. **Export** the authored arena manifest (for runtime parity checks).
4. **Build** the bundle(s) with Unity 6000.3.6f1 / URP; run the ShaderVariantCollection step.
5. **Publish-package check** (CI/pack gate): assert the bundle contains only original + proxy references and
   **no** vanilla SULFUR assets; assert bundle/content versions are stamped.
6. **Runtime load** (BepInEx plugin): load the bundle, instantiate the prefab, resolve `VanillaAssetProxy`
   objects via the player's local Addressables, register roots, compute the `ContentHash`, and report
   `ArenaReady`. Only once the gate passes do the arena/boss controllers seal, teleport, and start
   ([MultiplayerLoadingContract.md §5.3](MultiplayerLoadingContract.md)).
7. **Runtime unload:** destroy instantiated content, **release Addressables handles**, unload the bundle, drop
   bundle dependencies, and remove the arena's own nodes/links/modifiers from the active level's A\* graph
   (report 4.6 teardown) — leaving no residue in the rest of the current level, nor in the next one
   (RiskList R8).

## 8.7 Verification (PoC)
- P2 (report 7): a trivial bundle built in Unity 6000.3.6f1 loads under BepInEx.
- P3 / R13: one opaque, one transparent-sprite, and one emissive original material render correctly in-game.
- R14: runtime hierarchy/transforms match the exported arena manifest after proxy replacement.
- R8: bundle + handles fully released on teardown.
