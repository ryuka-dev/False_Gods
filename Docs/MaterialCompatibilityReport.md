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

- Our own AssetBundle (arena layout, our meshes, our lights) **must be built with the same Unity version the
  game runs**, or load/shader compatibility is unreliable. Determine the exact version from the game before
  building bundles (RiskList R2).
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
