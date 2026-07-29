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

## Original art only

Everything in this folder is compiled into a DLL that ships, so **only art we own belongs here**.

The csproj used to embed a second glob out of the repo-root `ExtractedAssets/` folder as well, so a
texture lifted from the player's own install could stand in while there was nothing else to draw.
That glob is gone. It would have put one of the game's own images inside a redistributed DLL, and it
also made "the first resource whose name ends `boss-body.png`" a question with two answers.

`ExtractedAssets/` is still there and still gitignored — it is reference material to measure against,
not a source the build reads.
