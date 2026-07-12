# 3. Material Compatibility Report

*Dependencies and risks of reusing SULFUR's cave materials/shaders, and the safest reuse path.*

## 3.1 The core recommendation

**Reuse vanilla materials by reusing the vanilla object that already carries them** (runtime Addressables
instantiation of a `Room`/prop prefab — report 2), rather than extracting raw meshes + materials and
re-packing them into our own bundle. Runtime instantiation keeps the original `Renderer`, `Material`,
shader, and shader keywords fully wired and guarantees the shader variants exist in the running game. Raw
re-packing is where pink/black materials, missing variants, and broken projections come from.

## 3.2 Why raw extraction is risky (the pink-material failure modes)

When a mesh + material are pulled out of the game and re-imported/re-packed in a separate project, any of the
following breaks rendering. SULFUR's environment art is likely to hit several:

| Dependency | Symptom if lost | Likelihood in SULFUR caves |
|---|---|---|
| **Shader not in build / variant stripped** | Pink material | High — custom/URP-style shaders strip unused variants at build time; a variant your combo needs may not be present unless the game already uses it. |
| **Triplanar / world-space projection** | Texture swims or mis-scales when the mesh moves/rotates/scales | Plausible for cave rock/wall blends (common technique to avoid UV seams on organic rock). |
| **Vertex color** blend masks | Wrong blend weights, flat look | Plausible — rock/dirt blends and AO are often baked to vertex color. |
| **UV2 / lightmap UVs** | Wrong or missing lighting | Likely — see lightmaps below. |
| **Texture arrays / atlases** | Wrong tile or garbage sampling | Possible for a shared cave texture set. |
| **Global shader properties** | Materials look wrong out of context | Possible (wind, global fog, time). |
| **Baked lightmaps** | Dark/flat, wrong ambient | **Almost certain** — generated levels rely on realtime + probes, and a re-hosted mesh won't carry the source scene's baked lightmap. |

Reusing the **live vanilla prefab** sidesteps every row above except lightmaps, because the shader, keywords,
vertex data, and texture references travel with the object and the variants are already resident.

## 3.3 Lightmaps specifically

Do **not** depend on the source scene's baked lightmaps for the arena. SULFUR builds levels procedurally at
runtime, so cave rooms are lit by realtime lights / reflection probes / the environment's own fog and ambient
(`WorldEnvironment` carries `fogColor`, `fogStartDistance`, `fogEndDistance`, `cameraFarClip`,
`reverbEnvironment`, but **no** per-instance baked lightmap). The arena must bring its **own `LightingRoot`**:
realtime lights, ambient/fog, and (optionally) reflection probes. This matches the request's intent.

## 3.4 Can a vanilla cave material go on our own simple ground mesh?

**Sometimes — and it must be tested, not assumed.** Two outcomes:
- If the cave floor material is **triplanar / world-space projected** and uses **no vertex color and no
  UV2/lightmap**, then assigning it to our own flat ground mesh will likely look correct (projection is
  independent of our mesh UVs). This is the *ideal* case and is worth testing first.
- If it depends on **authored UVs, vertex colors, or lightmap UV2**, our untextured ground mesh won't supply
  them and the result will be wrong.

**Practical rule:** for the floor a player stands on, prefer to reuse an actual vanilla floor **mesh/prefab**
(which carries the right UVs/vertex data) as the `VisualRoot` floor, and keep our own **separate simple
collider** underneath as the `CollisionRoot` (report 4). Only try "vanilla material on our own mesh" for
large filler surfaces after confirming the shader is projection-based.

## 3.5 AssetBundle / Unity-version constraints

- **The AssetBundle is the primary carrier for original False Gods content** — original meshes, materials,
  shaders, sprites, VFX, animation, audio, lights, and the arena prefab itself. It is *not* limited to
  "layout + simple mesh + lights"; it simply **must never redistribute vanilla SULFUR assets**.
- Our own AssetBundle **must be built with the same Unity version the game runs** — verified from the game
  files as **Unity 6000.3.6f1 with URP** (RiskList R2) — or load/shader compatibility is unreliable.
- Our bundle should **not** contain vanilla shaders/materials at all (legal + variant-stripping reasons); it
  relies on the game's already-loaded shaders via the runtime-instantiated vanilla objects.
- If we ever must ship an original shader, include it in a shader-variant collection so its variants aren't
  stripped.

## 3.6 Verification (feeds the PoC, report 7)
1. Instantiate a vanilla cave `Room`/wall prefab at runtime → confirm it renders correctly in the arena
   (no pink, correct lighting under our `LightingRoot`).
2. Take one vanilla cave floor material and assign it to our own flat ground mesh → observe whether it needs
   projection/vertex-color/UV2. Record the result; choose floor strategy accordingly.
3. Confirm no reliance on the source scene's baked lightmaps (lighting comes from our `LightingRoot`).

> **Result — PoC step P3, run in-game 2026-07-12 (probe `VisualProbe`, F11).**
> 1. **Vanilla prefab renders correctly, no pink.** The visible `CaveGrubGrub` (430 renderers, 0 null
>    materials, all shaders `Shader Graphs/*` / `ProBuilder6/*` reporting `supported`) rendered correctly under
>    our two-light `LightingRoot`. The reuse path of §3.1 holds in-render, not just on load.
> 2. **A vanilla floor material works on our own flat mesh — the ideal §3.4 case.** `CaveFloor`
>    (`Shader Graphs/MasterShader`) assigned to our untextured 20×20 floor rendered correctly, so it is
>    projection-based / UV-tolerant. **Floor strategy:** reusing a vanilla floor material directly on our own
>    ground mesh is viable; we need not reuse the whole vanilla floor mesh for large surfaces.
> 3. **Lightmaps:** our `LightingRoot` (realtime directional + point, no baked lightmaps) lit the stage; no
>    reliance on a source scene's bake.
>
> **New finding — our *own* materials went pink.** Our `Universal Render Pipeline/Lit` materials packed into
> the bundle rendered **pink** (the pillar), confirming §3.2 row 1 / §3.8: a stock URP shader packed by an
> authoring project that never renders it has its variants stripped, so `isSupported = true` at load yet pink
> in-render. **Measured (probe 0.4.0):** `Shader.Find("Universal Render Pipeline/Lit")` **misses** — the game
> keeps no resident stock URP/Lit (all vanilla content uses `Shader Graphs/*`), so adopting a game shader by
> name is **not** an available fix here. **Working fixes:** reuse a vanilla material (proven — a `CaveFloor`
> material renders on our own mesh; probe 0.5.0's `VisualFixOurMaterials` dresses our floor + pillar with a
> borrowed vanilla material to demonstrate it), or, for genuinely original shaders, ship a
> `ShaderVariantCollection` that preserves the needed variants. **Visual confirmation of the vanilla-material
> fix landed (F11, 2026-07-12): floor and pillar wear the borrowed `CaveFloor` material and are no longer
> pink.**

## 3.7 Original False Gods content is a first-class path

Runtime reuse of vanilla materials is optional. False Gods must also support fully original:

- meshes;
- terrain/floor materials;
- boss materials;
- sprites and sprite sheets;
- shaders;
- particle effects;
- animations;
- audio;
- phase-specific arena visuals.

The asset pipeline must not assume every renderer ultimately receives a vanilla material.

## 3.8 Original shader requirements

The PoC must identify the game's exact Unity version and render pipeline before production shaders are
authored. *(Verified from the game files: **Unity 6000.3.6f1**, **URP**. Author shaders against this URP
version — prefer ShaderGraph, which the game ships.)*

For every custom shader, verify:

- compatibility with the game's render pipeline;
- forward/deferred pass expectations;
- transparency and render-queue behaviour;
- fog and lighting integration;
- GPU instancing requirements;
- shader keyword and variant stripping;
- inclusion through a ShaderVariantCollection or equivalent preservation mechanism;
- correct behaviour when loaded from an AssetBundle;
- correct behaviour on both host and client.

## 3.9 2D boss rendering path

False Gods may use large 2D bosses or multi-part 2D mechanical characters.

Investigate and compare:

- `SpriteRenderer`;
- billboarded quad with `MeshRenderer`;
- layered sprite parts;
- sprite-sheet animation;
- skeletal 2D animation;
- hybrid 2D body + 3D projectiles/VFX.

The chosen path must define:

- camera-facing rules;
- world scale;
- pivot conventions;
- sorting and transparency;
- lighting response;
- hitbox attachment;
- muzzle/weak-point transforms;
- animation-state replication;
- multiplayer interpolation.

> The game ships URP's 2D renderer (`Unity.RenderPipelines.Universal.2D.Runtime`), `SpriteShape`, and 2D
> Animation (`Unity.2D.Animation.Runtime`, skeletal 2D), so all of the above paths are technically available
> in-engine (feasibility still to be validated in the PoC).
