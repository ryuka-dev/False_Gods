# 1. Arena Resource Architecture

*How SULFUR cave levels are organized, and which pieces can be reused as standalone modules for a custom
arena.* Grounded in `PerfectRandom.Sulfur.Core` / `PerfectRandom.Sulfur.LevelGeneration` (see
`../Decompiled/`).

## 1.1 Answer to "is a cave a scene, a prefab, a merged mesh, or procedural?"

**Procedural assembly of modular room prefabs**, not a hand-authored scene and not one giant mesh.

A run does not load a per-level `.unity` scene for combat levels. Instead the game keeps a small set of
persistent scenes and **generates** each level at runtime by instantiating room prefabs into the world. The
only whole-scene `Addressables.LoadSceneAsync` calls found are for `MainMenu.unity` (e.g.
`SplashScreenOLD.cs`), not for gameplay levels.

### The content hierarchy (all verified types)

```
WorldEnvironment  (ScriptableObject, "Level Gen/World Environment")
  ├─ List<MakerSet> levels                 // the MakerGraph generation graphs for this chapter
  ├─ navMeshVoxelSize = 0.1                 // → recast graph cellSize for this environment
  ├─ npcActiveDistanceToPlayer / RoomMargin // enemy activation LOD tuning
  ├─ connectorEmbellishments / barricades / connectorDoorBlockers / connectorNavBlockers  // List<GameObject>
  ├─ bakedCollision : TextAsset             // precomputed collision data for the environment
  └─ loot tables, fog/atmosphere, reverb, ...

LevelBlock  (ScriptableObject, "Level Gen/Level block")
  ├─ List<AssetReference> roomPrefabsAddressable   // the interchangeable rooms for a slot
  └─ List<AssetReference> helperRoomsAddressable

Room  (MonoBehaviour, root of each room prefab)
  ├─ GameObject Structure          // the visual + collision geometry root
  ├─ GameObject Decoration         // props/embellishment root
  ├─ Connector[] connectors        // doorway sockets (with Connector.Set) used to join rooms
  ├─ NPCSpawn[] NPCSpawns / Npc[] NPCs / EventSpawner[] / Container[] / Pickup[] / Interactable[]
  ├─ NodeLink2[] nodeLinks         // A* off-mesh links (jumps/teleports across the navmesh)
  ├─ List<Transform> navMeshAnchors (tag "NavMeshAnchor")   // "this point must stay walkable"
  ├─ OffMeshLinks.OffMeshLinkSource[] bakedNavMeshLinks     // baked A* off-mesh links
  ├─ RoomLODBase roomLOD           // room-level LOD/culling
  └─ roomSize (RoomSize), flags: uniquePerLevel/Run, doNotFlip, doNotBarricade, ...
```

### The generation pipeline (MakerGraph / XNode nodes, `LevelGeneration.*Node`)

`CreateStartAreaNode` → `CreateMainPathNode` (recursive room placement using `LevelBlockInstantiation` +
`WorkingRoomCollision`) → `AddExtraRoomsNode` → `ApplyRoomRandomizationNode` /
`FinalizeAndMutateUnitsNode` → `WorkingCollisionNode` → **`BuildNavMeshNode`** → `AwaitNavMeshBuildNode` →
`SpawnEnemiesNode` / `SpawnPlayerNode` / `SetupLootNode` → `ShowLevelNode` / `FinalizeLevelNode`.

Rooms are chosen from a `LevelBlock`'s `roomPrefabsAddressable` and instantiated with a `Matrix4x4`
transform (`LevelBlockInstantiation.transform`, optional `flipped`). Placement validity is a lightweight
collision test (`WorkingCollision` / `WorkingRoomCollision`, plus `WorldEnvironment.quickCollisonCheckRadius`),
**not** full mesh physics — an early hint that the game keeps generation-time collision simple.

## 1.2 How assets are referenced and loaded

Rooms and content are **Addressables**. The load helpers live in `LevelGeneration.LevelGenGraphUtilities`
and `PerfectRandom.Sulfur.Core.AsyncAssetLoading` (a `PersistentSingleton`):

- `assetRef.LoadAssetAsync<GameObject>()` — load a room/prefab by its `AssetReference`.
- `Addressables.LoadAssetAsync<GameObject>(bakedKey)` and
  `Addressables.LoadResourceLocationsAsync(bakedKey, typeof(GameObject))` — load by a string key ("baked key",
  derived from the asset GUID).
- `AssetReference.AssetGUID` is used as a stable chunk identity (`CreateMainPathNode` collects
  `_requeredChunksGUIDs`).
- Global databases load by plain string key: `Addressables.LoadAssetAsync<UnitDatabase>("UnitDatabase")`,
  `"ItemDatabase"`, etc. — so enemies/items are also fetchable by key at runtime.
- Handles are released with `Addressables.Release(...)`.

**Implication:** any vanilla room, prop, mesh, or material that is packed into an Addressables group can be
resolved and instantiated at runtime **from the player's own install**, by GUID or key, using the game's own
API. This is the backbone of the runtime-reuse strategy (see report 2).

## 1.3 What is cleanly reusable as a standalone module

| Reuse target | Reusable? | Notes |
|---|---|---|
| Whole `Room` prefab (cave room) | ✅ best | Self-contained visual+collision+decoration; keeps materials/shaders intact. Ideal "environment module". Comes with enemy spawns/connectors you may want to strip/ignore. |
| Individual props under `Decoration` / `Structure` (pillars, rocks, wood supports, minerals) | ✅ | Reusable if separately addressable **or** extracted from a room prefab at runtime (find child by name/component and re-parent/instantiate). |
| Cave wall / floor **meshes** as raw `Mesh` + `Material` | ⚠️ | Works only if the material's shader dependencies are satisfied (see report 3). Prefer reusing the prefab that already wires them. |
| `WorldEnvironment` fog/atmosphere/reverb settings | ✅ (values) | Read the numbers; apply your own lights/fog (report 3/4). Don't depend on baked lightmaps. |
| The MakerGraph generation pipeline itself | ➖ later | Powerful but heavy to integrate for a single fixed arena; revisit for procedural arenas. |

## 1.4 Consequence for the arena design

The natural unit of reuse is the **`Room` prefab** (or hand-picked children of one). The custom arena becomes
"our own layout that *instantiates vanilla cave rooms/props* as visual dressing, over our own simple collision
and our own nav-ready ground," rather than a re-authored copy of a vanilla level. Two ways to realize that
layout — feed it through the game's `Room`/level-gen pipeline, or load it as a standalone additive
hierarchy — are compared in **[ArenaLoadingProposal.md](ArenaLoadingProposal.md)**.
