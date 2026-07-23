# VendorAssets — local-only game content (NOT committed)

Everything in this folder except this README is **git-ignored** (see `FalseGods.Unity/.gitignore`).
These are assets copied from the player's own installed **SULFUR** game (reverse-engineered export),
kept locally so the arena can be authored against the real meshes/materials. They are the player's own
content and are **not redistributed through this repository**.

To reproduce this folder on another machine, re-copy from a local AssetRipper export of the installed
game (matching the compiled game version). Paths are relative to the export's `ExportedProject/Assets/`.

## Contents

### `Rocks/` — cave decoration rock meshes + material texture
Vanilla source: `_Core/Models/Nature/Rocks/`

| File | Vanilla source | What it is |
|------|----------------|------------|
| `Rock.01.asset` … `Rock.06.asset` | `_Core/Models/Nature/Rocks/Rock.0N.asset` | The six cave rock meshes (single submesh, ~flat-shaded). GUIDs preserved. |
| `Rocks_Color.png` | `Texture2D/Rocks_Color.png` | 4x4 base texture used by the rock material (`RocksColor`, shader `Universal Render Pipeline/Lit`). GUID preserved. |

The rock material itself is authored in-project (`PocRoomGenerator` builds a `URP/Lit` material pointing
at `Rocks_Color`), matching vanilla `RocksColor` — so nothing but the mesh + a 4x4 texture is borrowed.
These meshes/textures are baked into the shipped arena bundle; the cave **wall/floor/ceiling** materials
(`Shader Graphs/MasterShader`) are still resolved at runtime by the material-borrow path, not baked.
