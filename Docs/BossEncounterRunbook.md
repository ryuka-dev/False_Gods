# Boss Encounter Runbook

How a boss encounter and its arena get built here, in the order that actually worked, with the measurements
they depend on and the traps that cost real time. Written while landing the first one, for whoever builds the
second.

**This document owns three things and delegates everything else:**

| Owns | Delegates to |
|---|---|
| The production sequence and what closes each step | — |
| Measurements pinned to a game version, and how to re-take them | — |
| Traps, each with the symptom that identifies it | — |
| | Module boundaries → [Architecture.md](Architecture.md), [DependencyRules.md](DependencyRules.md) |
| | What may depend on what, and how it is checked → [ArchitectureEnforcement.md](ArchitectureEnforcement.md) |
| | Content hash, wire protocol, ready gate → [MultiplayerLoadingContract.md](MultiplayerLoadingContract.md) |
| | Whether a feature is finished → [DefinitionOfDone.md](DefinitionOfDone.md) |
| | Why a structural decision was made → [ADRs/](ADRs/README.md) |

Nothing here restates a rule that lives somewhere else. Where a fact is enforceable, it is a test or an
assertion in code, and this document points at it rather than repeating the value.

---

## 0. Instruments

Everything below is verified with these. Learn them before starting; most of the wasted time in the first
encounter came from checking things by eye that one of these answers exactly.

| Instrument | Command / key | Answers |
|---|---|---|
| Build + architecture gate | `.\scripts\verify.ps1`, and `-Configuration Release` | Does it build, do the `FG-ARCH-*` checks pass, is the whitespace clean |
| Plugin deploy | `dotnet build src/FalseGods.Plugin/FalseGods.Plugin.csproj -c Debug -p:DeployPlugin=true` | Puts the DLLs in the game profiles. **The game must be closed** — a running game holds the DLLs open |
| Probe deploy | `dotnet build tools/FalseGods.Probe/FalseGods.Probe.csproj -p:DeployProbe=true` | Puts the diagnostic plugin in the profiles |
| Live-graph probe | **F10** in game | Layers, recast agent parameters, walkable nodes **by height band**, distinct areas, every `NodeLink2` and whether it attached, NavMeshCleaner state, rooms and anchors |
| Arena bundle | `False Gods/Build PoC AssetBundle`, or automatically on `CaveShell.fbx` import | Packs the arena prefab and writes the content artifact |
| Local deploy of content | `FalseGods.Unity/LocalDeployTargets.txt` (gitignored) | One absolute plugin-folder path per line; the import hook copies bundle + artifact to each |

The probe's other hotkeys (F4–F9, `=`) are one-shot experiments from the proof-of-concept phase; several of them
**load their own copy of the arena bundle** and will collide with the plugin's. F10 is the one that only reads.

**A green build is not a verified behaviour.** Compiling proves the code compiles. Anything that runs in the
game is unverified until it has run in the game, and anything involving two peers is unverified until two peers
have run it.

---

## 1. The production sequence

Each step lists what must be true to start it, what it consists of, and **what closes it** — the evidence, not
the feeling. The first encounter went through them in this order; the order matters because each step's exit
evidence is the next step's input.

### 1.1 Get the arena into the game as a level

**Entry:** nothing.
**Do:** the arena is delivered by hijacking the game's own level generation, not by overlaying a room on an
existing level. A dev key arms the hijack, then `GameManager.GoToLevel(Act_01_Caves, 0, …)`; a Harmony prefix on
`CreateStartAreaNode.Execute` substitutes our arena, wrapped as a `Room`, for the vanilla start room, and the
neutered-node set skips main-path/extra-room/enemy/event generation. Everything else runs natively.
**Closes when:** the level loads, the log lists every generation step as `native` or `skipped`, and a manually
summoned enemy paths on the arena floor.

> **Why this way.** The first attempt loaded the arena additively onto a normal level and rescanned navigation
> itself. That is a dead end: the port fail-closes on `!recast.isScanned`, and a large footprint also trips the
> "extends past the level's navigable area" check. Going through `GoToLevel` makes the game build and scan the
> navigation, place the player, and apply the level fog — all for free. See
> [ArenaLoadingProposal.md §2.1](ArenaLoadingProposal.md) for the two strategies as originally framed; the
> conclusion is that A is the vehicle for a fixed boss arena and B is not.

### 1.2 Author the room

**Entry:** the arena loads as a level.
**Do:** sculpt the room in Blender as **one object with one material slot per surface kind**
(`*_WallBot`, `*_WallMid`, `*_WallTop`, `*_Floor`, `*_Ceiling` — only the part after the last underscore is
matched, so numeric ordering prefixes are free). Export straight over `CaveShell.fbx`; the import hook splits it,
re-aligns the placeholder materials, rebuilds the bundle and deploys it. Reload the level in game to see it.

Three things are decided here and are hard to change later:

- **The Floor slot is the walkable-surface declaration.** The import splits those faces onto their own object on
  the navigation layer; everything else goes to a layer the scan never looks at (§2.2). Assign to the Floor slot
  exactly what enemies should be able to stand on — not the rock mass under a terrace.
- **Materials are borrowed at runtime, never baked** (§3.1). The mesh carries UVs and face assignment; the look
  comes from the player's own install.
- **Terrain must fit the agent** (§2.3). Slopes ≤45°, no unbridged step over 0.6 m within a region meant to be
  one region, ≥1.5 m of clearance, and nothing narrower than about 2 m after the 0.5 m radius erosion.

**Closes when:** F10 reports walkable nodes in every height band that was meant to be walkable, and
`distinct areas` is small enough to be explained by the terraces you deliberately authored.

### 1.3 Connect what the walkable surface does not connect

**Entry:** the room's surfaces are walkable.
**Do:** a recast graph only connects surfaces an agent can walk between. Raised terraces scan as walkable but
isolated, and enemies never consider going up. Place off-mesh links: empty markers under
`NavigationRoot/NavLinks`, one per crossing — **the marker object is the start, its `End` child is the end** —
and the room wrap attaches a `NodeLink2` to each at load. An agent traversing one jumps it, animation and all.

Place several crossings per tier so enemies do not funnel through one spot, and remember a link is a single
authored hop: the pathfinder has no concept of tiers, so a lone link from the floor to the third terrace is
exactly what enemies will use.

**Closes when:** F10 lists every link as `status = Active`, and enemies climb tier by tier in game.

### 1.4 Give the encounter its content

**Entry:** the arena is a navigable level.
**Do:** the boss's own simulation, presentation and replication live in `FalseGods.Core` and are driven by ports;
nothing about them is arena-specific. What is worth reusing from the first encounter:

- **Damage in both directions** goes through the game's own paths — real weapon fire reaches the boss through
  `IAttackReceiver`, and the boss damages the player through `ReceiveDamage` (§2.5).
- **Props are real vanilla units.** The thrown crates are actual `Breakable`s assembled at runtime from
  catalogue assets, which is why they take weapon fire, drop ordinary loot, and behave for SULFUR Together
  exactly like any other breakable.
- **Determinism is by seed, not by sync.** Spread, telegraph timing and target selection are pure functions of a
  seed in `FalseGods.Core`, so every peer computes the same volley from the same number.

**Closes when:** the fight runs repeatedly in one session with no reload — raise, fight, die, raise again — and
the log shows the arena bundle loading exactly once.

### 1.5 Make it multiplayer

**Entry:** the encounter is complete in single-player.
**Do:** see [MultiplayerLoadingContract.md](MultiplayerLoadingContract.md) for the ready gate and the content
hash, and [OriginalBossNetworkingArchitecture.md](OriginalBossNetworkingArchitecture.md) for the replication
model. Two things are specific to an arena that is the level, and both were measured with two peers
(2026-07-26):

- **Every peer generates the level itself, so every peer must know it is the arena.** The multiplayer layer does
  not auto-follow the host into a level, and a *client*-initiated level load is intercepted and relayed so the
  **host** leads the transition and the client then re-loads under the host's seed. One request therefore
  produces up to three generation runs across two peers. Deciding "this is the arena" per load request covers
  exactly one of them; the standing declaration (arena mode, §2.6) covers all of them. **Order matters at the
  seam:** a peer that asks for the level before the other peer has declared arena mode makes that other peer
  regenerate an ordinary level. Put the host in arena mode first.
- **A client standing in the arena must adopt it, not realize its own.** The level realized it through the same
  load flow — same parity check, same recomputed hash — so its manifest is what the client would report anyway,
  and a second realization cannot even load (§3.12). Adoption also fixes ownership: the arena outlives the
  encounter, because it belongs to the level.

**Closes when:** two instances run the fight with both-way damage, identical arenas, and a clean teardown.
**Done for the first encounter** (2026-07-26): both peers in one cave, client-reported hits applied by the host,
one bundle load per peer.

---

## 2. Pinned measurements

**Game version: SULFUR v0.18.5 (buildid 24239244). Unity 6000.3.6f1, URP, A\* Pathfinding Project 5.3.8.**

Re-take these after a game update rather than trusting them. Each entry says how.

### 2.1 The three layer masks that decide everything

*How to re-take: F10, sections "GameManager (R3)" and "RecastGraph (R3 …)"; the crate mask is ours, in
`SulfurThrownCratePort.WallLayerNames`.*

| Mask | Members | What it decides |
|---|---|---|
| Recast rasterization | 3 Geometry, 12 StaticDoodad, 18 InvisibleGeometry, 30 ProjectileTrigger | What the navigation scan even looks at. `rasterizeMeshes = true`, **`rasterizeColliders = false`** |
| `GameManager.geometryLayer` | 3, 12, 18, 22 GeometryNoNavMesh, 26 LevelGenBlock | Physics and AI line of sight |
| Thrown-crate walls (ours) | 22, 12, 18, 26 | What a flying crate breaks against. Deliberately excludes 3, so floor contact is a landing |

### 2.2 Which layer to put a surface on

Derived from the three masks above; this table is the whole reason the arena is split the way it is.

| Layer | Navigable | Solid | Crate breaks on it | Use for |
|---|---|---|---|---|
| 0 Default | no | no | no | Pure décor |
| **3 Geometry** | **yes** | yes | no (it is a floor) | **The walkable surface** |
| **12 StaticDoodad** | **yes** | yes | **yes** | Obstacles enemies should path around: rocks, pillars |
| **22 GeometryNoNavMesh** | no | yes | **yes** | Walls, ceilings, anything solid the scan should ignore |

**A mesh only rasterizes if its GameObject's layer is in the mask** — layers are per GameObject, so a surface
that needs its own layer needs its own object. `rasterizeColliders = false` means a collider-only ramp is solid
but unwalkable.

### 2.3 Recast agent limits — design terrain inside these

*How to re-take: F10, "RecastGraph (R3 + §4.4 agent parameters)".*

`cellSize 0.1` · `characterRadius 0.5` · `walkableHeight 1.5` · `walkableClimb 0.6` · `maxSlope 45` ·
`maxEdgeLength 20` · `contourMaxError 2` · `minRegionSize 1` · tiles `128` (12.8 m) · `Dimension3D`
(multi-level and overhangs are supported).

The radius erodes 0.5 m of walkable surface away from every rasterized obstacle it touches — which is why walls
belong on a layer the scan ignores (§2.2), and why a 1 m ledge is not walkable at all.

### 2.4 Off-mesh links

*How to re-take: decompile `Pathfinding.NodeLink2` from the game's `AstarPathfindingProject.dll`; F10 reports
the live state.*

- `NodeLink2.StartTransform` is **the component's own transform**; `end` is a separate `Transform` field;
  `oneWay` defaults to false, so a link is two-way.
- **`maxSnappingDistance = 1`** — an endpoint further than a metre from a walkable node is **silently dropped**.
  The failure is visible only as the link source's status (`FailedToConnectStart` / `FailedToConnectEnd`), which
  is why F10 prints it.
- The level's `BuildNavMeshNode` re-walks every placed room after its scan and registers each room's
  `nodeLinks` (via `TryAddLink`) and `bakedNavMeshLinks`. A room we assemble at runtime gets this for free by
  reporting its links — no patch required.
- `AiAgent.TraverseOffMeshLink` → `JumpTo(...)` with animator `Jump`/`Land`, physics and the main collider
  disabled for the arc. Enemies really jump; they do not teleport (unless `teleportWithLinks` is set).
- The component cannot be authored into the bundle: the Unity project has no A\* reference, and baking a game
  type is a bet this codebase does not take. Author markers, attach at load.

### 2.5 Units, damage and props

*How to re-take: decompile the game's Core/Gameplay assemblies from the **live install**, not from a backup copy
— see the note in the root engineering rules about which directory is complete.*

- **Enemies are not constrained by colliders.** `AiAgent` drives the transform along the navmesh; colliders are
  for hit detection and physics. If the navigation says a wall can be crossed, enemies cross it.
- `Player` is not a `Unit` — `GameManager.PlayerUnit` is. `Unit.ReceiveKnockback` explicitly skips the player;
  the working recipe is `Player.movement.SetMomentum(...)` on the CMF controller.
- `Breakable : Unit`, so a vanilla barrel or crate already has health, takes weapon fire through the game's own
  path, and drops loot on `Die()`. `BreakOnFirstContact` and `TakeDamageOnCollision` are public and can be
  turned off while we drive the object ourselves.
- Assembled-at-runtime units need every list the game indexes into initialised — `SetStats` reads the
  component's `unitSO` **field**, and `Die` reads `spawnOnDeath_LQ.Count`.

### 2.6 Level generation and the injection seam

*How to re-take: the hijack logs every step as it executes; that log is the authoritative node list.*

`Act_01_Caves` level 0 runs 17 nodes. The ones that matter: **3 `CreateStartAreaNode`** (the injection seam —
it sets `spawnPoint` from the room), **9 `FinalizeLevelNode`**, **10 `BuildNavMeshNode`** (scan + links),
**14 `SpawnPlayerNode`**. Note that finalisation runs *before* the navigation build.

`MakerNodeBase.StartExecution` is an ordinary method returning `IEnumerator` — a Harmony prefix can substitute a
whole node without touching an iterator state machine, which is how the game's own `Run.Disabled` path works.

A runtime-assembled `Room` must initialise **every** baked array to empty (the generation steps index into them
without null checks), carry a `RoomLODBase` before `Room.Awake` runs, and use **zero navigation anchors** — the
`NavMeshCleaner` marks everything outside its valid points unwalkable, and stays inert only while it has none.

**Decide "this run builds the arena" at the run, not at the request.** `MakerGraphContext.StartMaking` is called
once per whole generation graph and is reached by every path that generates a level — our own request, a peer
following the host, the host leading a peer's transition. `GameManager.currentEnvironment` and
`currentLevelIndex` are both already set when it is called, so the run can be identified there. What a peer
declares is standing ("this level is the arena until I say otherwise"); what a run gets is per-run and released
in a `finally`. See §1.5 for why the request is the wrong place.

### 2.7 Borrowed cave materials

*How to re-take: read the `.mat` YAML out of an AssetRipper export, or the live materials through a probe.*

Donor carrier: room GUID `92103c239550ca740906311170fcc458` (`CaveNormal3New`), pinned because it is a clean
room whose renderer carries the whole cave set with unique names. Available names: `CaveFloor`, `CaveWall`,
`CaveWallBot`, `CaveWallMid`, `CaveWallTop`, `CaveCeilingOther`, `Rocks_Caves`. **`CaveCeiling` does not exist**
on it — the ceiling material is `CaveCeilingOther`, and it uses the texture named `CaveCeiling.png`.

Tilings differ per material and must be matched in the authoring tool's preview or the UVs will be authored
against the wrong density: **walls 0.33, floor 0.35, ceiling 0.3**.

---

## 3. Traps

Each of these cost hours. The symptom is what identifies it from the outside.

### 3.1 Baking a material instead of borrowing it
**Symptom:** the surface looks right in the editor and renders dark in game, and adding lights to the arena
changes nothing. The game's own materials respond to the level's lighting differently than URP/Lit, and our
arena lights do not drive the level's main directional. Author mesh, UVs and face assignment; borrow the
material at load.

### 3.2 Binding sub-materials by index
**Symptom:** after a re-export, the floor wears a wall texture. Unity orders an imported mesh's sub-meshes by
the order faces first use each material, **not** by the authoring tool's slot list, so re-sculpting can permute
the indices while the slot order stays put. Bind by the placeholder material each sub-mesh already wears, and
re-align the placeholders from the model's own slot names at import.

### 3.3 Assuming colliders stop enemies
**Symptom:** an enemy attacks from inside solid rock, or from under the floor. Nothing stops an agent except
the absence of navigation. See §2.5.

### 3.4 A rectangular floor under an irregular room
**Symptom:** enemies walk through walls in a room whose walls are clearly solid. A sculpted wall is a *surface*,
not a volume: it blocks one column of voxels, and the navigation on either side of it stays connected. The
walkable mesh must have the room's real outline.

### 3.5 Letting the scan see the whole sculpt
**Symptom:** the graph shatters into many disconnected areas, terraces get a handful of node fragments, and a
large share of all nodes sit on surfaces nobody can reach — the outside of the shell. Walls in the rasterization
mask erode half a metre off every surface they touch. Declare the walkable faces and keep the rest off the
navigable layers (§2.2).

### 3.6 Shipping the preview textures
**Symptom:** the bundle is much larger than the geometry justifies, and its manifest lists `Texture2D` entries.
Placeholder materials that wear extracted textures make those textures dependencies of the packed prefab. The
build strips them, packs, and restores them; keep the placeholder list current when adding one.

### 3.7 Authoring UVs against the wrong tiling
**Symptom:** seams line up perfectly in the authoring tool and drift in game — a little near the UV origin and
badly far from it, so only *some* faces look wrong. A preview scale of `1/3` against a material that tiles at
`0.33` is a 1% error multiplied by distance from the origin. Fix without redoing the work by scaling the UVs by
the ratio about the UV origin.

### 3.8 Measuring walkability with the winding ignored
**Symptom:** an offline check reports healthy walkable area on surfaces the game will never let an agent stand
on. Recast cares which way a face points; a check that folds a downward-facing face into "walkable" (by treating
slope over 90° as its supplement) counts ceilings as floors.

### 3.9 Writing to a prefab asset while its stage is open
**Symptom:** a tool reports success and the change is not there afterwards. Prefab Mode's Auto Save holds the
prefab in memory and writes it back over an asset-level edit. Tools must write through the open stage when there
is one.

### 3.10 `??` on a Unity component
**Symptom:** `MissingComponentException` from a line that just added the component. A missing component compares
equal to null through the overloaded operator while still being a real reference, so the null-coalescing
operator hands back the missing one. Use an explicit `== null` check.

### 3.11 Deploying while the game runs
**Symptom:** the build fails with a cluster of file-lock errors. The running game holds the plugin DLLs open.
Content (bundle and artifact) can be redeployed with the game running — only a level reload is needed to pick it
up — but DLLs cannot.

### 3.12 Loading the arena a second time on a peer that already stands in it
**Symptom:** on a multiplayer client only, the whole encounter fails closed at the ready gate with "arena bundle
failed to load". A standing arena holds its AssetBundle open, and `AssetBundle.LoadFromFile` of a file already
loaded returns null rather than a second handle. A peer that is already in the arena must adopt the standing one
(§1.5), not realize its own copy of it.

---

## 4. Decisions worth not re-litigating

- **The arena is the level, not an overlay** (§1.1). Native navigation, player spawn and fog come free.
- **The room is ours, wearing the game's materials.** Vanilla cave rooms are combat-room sized; a boss arena is
  not. Building our own geometry and borrowing the materials keeps the look consistent without shipping any of
  the game's assets and without inventing a shader.
- **Walkability is declared, not inferred** (§1.2, §3.5).
- **The boss will teleport between fixed points** rather than path to the player, which is also how the vanilla
  cave boss behaves — it removes boss-versus-obstacle steering from the problem entirely. Decided, not yet
  built: the first boss still walks, and walks through obstacles.
- **Do not abstract the second boss before it exists.** Keep boss and arena content as data and assets, and grow
  the registry when a second one actually lands — see the commit that documents this direction and
  [DefinitionOfDone.md §3](DefinitionOfDone.md).
- **Extracted assets never ship.** They live in gitignored local folders, are used for authoring reference and
  preview only, and the build enforces that they stay out of the bundle (§3.6).
