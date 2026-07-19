namespace FalseGods.RuntimeContracts.Arena
{
    /// <summary>The outcome of loading the arena's shipped content: the artifact text on success, or a
    /// diagnostic error. Failure is an outcome, not an exception — the load flow fails closed on it.</summary>
    public sealed record ArenaAssetLoadResult(bool Success, string? Error, string? ArtifactText)
    {
        public static ArenaAssetLoadResult Loaded(string artifactText) => new ArenaAssetLoadResult(true, null, artifactText);

        public static ArenaAssetLoadResult Failed(string error) => new ArenaAssetLoadResult(false, error, null);
    }

    /// <summary>
    /// Loads and releases the arena's shipped content — the mod AssetBundle and the arena content artifact
    /// exported beside it (Docs/ArenaLoadingProposal.md §2.3, Docs/OriginalContentPipeline.md §8.6).
    /// </summary>
    /// <remarks>
    /// Implemented in <c>FalseGods.UnityRuntime</c>, which owns the AssetBundle lifecycle (Architecture §3).
    /// The port therefore lives here rather than in <c>FalseGods.Application</c> — Architecture §6 lists it
    /// under Application's ports, but UnityRuntime's reference list (Core + RuntimeContracts + UnityEngine)
    /// cannot see Application, so a UnityRuntime-implemented port must sit in this assembly, exactly like
    /// <c>IEncounterPresentation</c>. The artifact crosses the seam as raw text: this assembly and UnityRuntime
    /// may not reference <c>FalseGods.Protocol</c>, so parsing and hashing stay in <c>FalseGods.Application</c>.
    /// <para>
    /// Lifecycle: <see cref="Load"/> acquires the bundle and reads the artifact; the loaded prefab stays inside
    /// the implementation for <see cref="IArenaRealization"/> to instantiate (the two are implemented together).
    /// <see cref="Release"/> unloads the bundle and is idempotent; calling <see cref="Load"/> while loaded is an
    /// implementation error and throws.
    /// </para>
    /// </remarks>
    public interface IArenaAssetProvider
    {
        ArenaAssetLoadResult Load();

        void Release();
    }
}
