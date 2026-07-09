# False Gods

A SULFUR mod that adds original bosses and dedicated boss-arena maps. It is designed to work in **both**:

- **Vanilla single-player SULFUR**, and
- **[SULFUR Together](../SULFUR%20Together)** multiplayer, where the **host is authoritative** over the
  boss, the arena, and the combat flow.

> **Status: feasibility investigation only.** This repository currently contains research documents and a
> local reverse-engineering reference. No plugin code, boss, or arena assets exist yet. The first
> implementation target is a single boss in a **fixed** arena; procedural arena assembly is a later goal.

## Goals & principles

- Host-authoritative boss / arena / combat; clients own their own input, inventory, and personal state.
- **Reuse** SULFUR Together's existing host-authoritative systems (level/seed sync, boss adapters,
  host-driven enemy proxy, arena lockdown) rather than adding new transport or authority.
- **Reuse vanilla assets at runtime** from the player's own game install (Addressables) instead of
  redistributing vanilla scenes, meshes, textures, or shaders.
- Keep visual geometry, physics collision, and navigation as **decoupled layers**.

## Repository layout

| Path | Purpose | Committed? |
|------|---------|-----------|
| `Docs/` | Research reports (see `Docs/README.md`) | ✅ |
| `LocalPaths.props.example` | Template for machine-specific paths | ✅ |
| `LocalPaths.props` | Your real paths (copy of the example) | ❌ gitignored |
| `Decompiled/` | Local reverse-engineering reference (see `Decompiled/README.md`) | ❌ gitignored |
| `ExtractedAssets/` | Any assets pulled from your local game install | ❌ gitignored |

## Setup

1. Copy `LocalPaths.props.example` → `LocalPaths.props` and fill in your paths
   (SULFUR managed dir, SULFUR Together source, BepInEx core/plugins).
2. (Optional) Regenerate the decompile reference — see `Decompiled/README.md`.

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
