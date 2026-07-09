# Decompiled/ — local reverse-engineering reference (never committed)

This folder holds a **local, read-only** decompile of the SULFUR game assemblies, kept so that development
does not require re-decompiling. **Everything here except this `README.md` is gitignored** (see the repo
`.gitignore`). Do **not** commit or redistribute decompiled game code.

## Regenerate

Requires [`ilspycmd`](https://github.com/icsharpcode/ILSpy) (installed as a global dotnet tool during
investigation: `ilspycmd` 10.1). With `SulfurManagedDir` = your game's `Sulfur_Data\Managed`:

```bash
M="D:/SteamLibrary/steamapps/common/SULFUR/Sulfur_Data/Managed"
for a in PerfectRandom.Sulfur.Core PerfectRandom.Sulfur.Gameplay PerfectRandom.Sulfur.LevelGeneration; do
  ilspycmd -p -o "Decompiled/$a" "$M/$a.dll"
done
```

`-p` emits a reconstructed `.csproj` per assembly with a per-namespace file tree, which greps and reads
cleanly. Add `Assembly-CSharp` if needed (it is a thin shim in this build).

## What lives where (verified)

| Assembly | Key namespaces for this project |
|----------|--------------------------------|
| `PerfectRandom.Sulfur.Core` | `…Core.LevelGeneration` (`WorldEnvironment`, `LevelBlock`, `Room`, `Connector`), `…Core.Units.AI` (`AiAgent`, `CustomRichAI`, `NavMeshManager`), `…Core` (`GameManager`, `NpcUpdateManager`, `AsyncAssetLoading`, `RecastTagVolume`), `LevelGeneration` (`LevelGenGraphUtilities`) |
| `PerfectRandom.Sulfur.Gameplay` | `…Core` (`BossFightHelper`, `BossPhase`, per-boss helpers), `…Gameplay` (`WitchBossController`, `EmperorBoss*`, spawners) |
| `PerfectRandom.Sulfur.LevelGeneration` | `LevelGeneration` (the MakerGraph node pipeline: `CreateMainPathNode`, `BuildNavMeshNode`, `NavMeshCleaner`, `LevelBlockInstantiation`, `WorkingCollisionNode`, `SpawnEnemiesNode`) |
