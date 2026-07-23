# Embedded boss art

PNG files placed in this folder are compiled **into** `FalseGods.UnityRuntime.dll` as embedded
resources (see the `<EmbeddedResource>` items in `FalseGods.UnityRuntime.csproj`) and loaded at
runtime by `BossPresentation`. Nothing ships as a loose image beside the plugin — the art travels
inside the DLL, so adding more bosses never scatters image files across the plugin folder.

## How the renderer picks a file

`BossPresentation` loads the boss body sprite by **resource-name suffix**: it takes the first
embedded resource whose name ends with `boss-body.png` (case-insensitive). If no such resource is
embedded, the boss falls back to a flat coloured quad — the art is a pure presentation concern and
is never required for correct behaviour.

## Original vs. extracted art

- **Original art you own** goes **here**, committed. It is yours to distribute, so it may ship in a
  release build.
- **Vanilla or otherwise extracted game art** must **never** live here or be committed. Put such a
  placeholder in the repo-root `ExtractedAssets/` folder instead — it is gitignored ("assets
  extracted from the player's local game install — never redistributed") and the csproj embeds it
  only for local testing. Swap in your own original art before publishing.

Both locations use the same `boss-body.png` name, so the renderer finds whichever one is present.
Do not have both at once — two resources with the same suffix would make the pick order ambiguous.
