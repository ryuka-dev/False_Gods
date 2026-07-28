// Addressables / Unity / game-type interop, like the other game-facing implementations in this assembly.
#nullable disable

using System;
using System.Collections;
using FalseGods.Application.Presentation;
using FalseGods.Integration.Sulfur.Arena;
using PerfectRandom.Sulfur.Gameplay.Triggers;
using UnityEngine;
using UnityEngine.AddressableAssets;
using UnityEngine.ResourceManagement.AsyncOperations;
using ILogger = FalseGods.RuntimeContracts.Diagnostics.ILogger;

namespace FalseGods.Integration.Sulfur.Presentation
{
    /// <summary>
    /// The SULFUR implementation of <see cref="IArenaAtmospherePort"/>: the arena's fog, and the cave boss's own
    /// battle music.
    /// </summary>
    /// <remarks>
    /// <para><b>The fog animates itself.</b> The engine's fog request takes a duration and interpolates the
    /// distances on its own update, so a room opening out over several seconds costs one call. All this adds is
    /// the pause in front of it, which is a coroutine on the plugin's own behaviour rather than a clock of ours.
    /// </para>
    /// <para><b>The music is the game's, played the way the game plays it.</b> The cave boss's battle music is not
    /// an asset with a name we can ask for and it is <i>not</i> on the boss prefab — measured: the standalone
    /// creature carries no music trigger at all. It is on the boss instance <b>inside the cave boss room</b>, which
    /// is the same donor prefab this arena already borrows its scenery from. So the room is loaded and the trigger
    /// read off it, and then simply told to start: that one call sets the game's own mixer snapshot and hands the
    /// game's own playlist to the game's own music player, which is what makes the player's music volume and mute
    /// apply without this mod having a single audio setting of its own (Docs/BossEncounterRunbook.md §3.14).</para>
    /// <para><b>Calling a method on a prefab's component is deliberate.</b> Nothing in the trigger's start path
    /// touches its own object — it is three calls into game-wide singletons plus two serialized references — so
    /// there is nothing to instantiate, and instantiating the vanilla boss room to play music would put a second
    /// cave boss in our arena.</para>
    /// <para>Silence and an unchanged room are the failure modes throughout: a build that cannot find the music
    /// still gets the fight.</para>
    /// </remarks>
    public sealed class SulfurArenaAtmosphere : IArenaAtmospherePort
    {
        /// <summary>The vanilla cave boss's room — the donor the battle music is read from, and the same prefab the
        /// arena's scenery is cloned from.</summary>
        private const string CaveBossRoomKey =
            "Assets/_Core/Prefabs/LevelGeneration/Chunks/Caves/CaveCousinNew.prefab";

        /// <summary>How long the music takes to fade when the fight ends — the cave boss's own number.</summary>
        private const float MusicFadeSeconds = 10f;

        private readonly MonoBehaviour _host;
        private readonly ILogger _logger;

        private AssetReference _room;
        private MusicTrigger _music;
        private bool _searchedForMusic;
        private Coroutine _pendingDepth;
        private bool _playing;

        /// <param name="host">The behaviour whose lifetime the fog's pause runs on. The plugin's own, so a pause
        /// cannot outlive the mod.</param>
        public SulfurArenaAtmosphere(MonoBehaviour host, ILogger logger = null)
        {
            _host = host != null ? host : throw new ArgumentNullException(nameof(host));
            _logger = logger;
        }

        public void Warm() => FindTheMusic();

        public void SetRoomDepth(
            float startDistance, float endDistance, float afterSeconds = 0f, float overSeconds = 0f)
        {
            CancelPendingDepth();

            if (afterSeconds <= 0f)
            {
                Apply(startDistance, endDistance, overSeconds);
                return;
            }

            _pendingDepth = _host.StartCoroutine(AfterAPause(startDistance, endDistance, afterSeconds, overSeconds));
        }

        public void StartBattleMusic()
        {
            var music = FindTheMusic();
            if (music == null)
            {
                return;
            }

            try
            {
                music.StartMusic();
                _playing = true;
                _logger?.Log("[music] the boss's own battle music is playing, on the game's music mix.");
            }
            catch (Exception exception)
            {
                _logger?.LogWarning($"[music] the battle music would not start ({exception.Message}).");
            }
        }

        public void StopBattleMusic()
        {
            if (!_playing || _music == null)
            {
                return;
            }

            _playing = false;
            try
            {
                _music.StopMusic(MusicFadeSeconds);
                _logger?.Log($"[music] the battle music is fading out over {MusicFadeSeconds:0}s.");
            }
            catch (Exception exception)
            {
                _logger?.LogWarning($"[music] the battle music would not stop ({exception.Message}).");
            }
        }

        /// <summary>Let go of the donor room. The fog is the level's and is left exactly as it is — this owns how it
        /// was asked to change, never what a level looks like after we are gone.</summary>
        public void Dispose()
        {
            CancelPendingDepth();
            _music = null;
            _searchedForMusic = false;

            if (_room == null)
            {
                return;
            }

            try
            {
                _room.ReleaseAsset();
            }
            catch (Exception)
            {
                // Not loaded, or already released.
            }

            _room = null;
        }

        private IEnumerator AfterAPause(float start, float end, float afterSeconds, float overSeconds)
        {
            // Unscaled, like the engine's own fog interpolation: an opening that stalls because the game is paused
            // would leave the players in the dark with a boss already roaring.
            yield return new WaitForSecondsRealtime(afterSeconds);
            _pendingDepth = null;
            Apply(start, end, overSeconds);
        }

        private void Apply(float start, float end, float overSeconds) =>
            SulfurLevelFog.TryApply(start, end, _logger, overSeconds);

        private void CancelPendingDepth()
        {
            if (_pendingDepth == null)
            {
                return;
            }

            _host.StopCoroutine(_pendingDepth);
            _pendingDepth = null;
        }

        /// <summary>
        /// The cave boss room's music trigger, loading the room the first time it is asked for.
        /// </summary>
        /// <remarks>
        /// Loaded synchronously, which is what the arena's own material borrow does for the same asset and for the
        /// same reason: the room is already resident while our arena stands, because our scenery came out of it.
        /// Searched once — a room that carries no trigger is not going to grow one — and reported once.
        /// </remarks>
        private MusicTrigger FindTheMusic()
        {
            if (_music != null || _searchedForMusic)
            {
                return _music;
            }

            try
            {
                if (_room == null)
                {
                    _room = new AssetReference(CaveBossRoomKey);
                }

                var handle = _room.LoadAssetAsync<GameObject>();
                var room = handle.WaitForCompletion();
                if (handle.Status != AsyncOperationStatus.Succeeded || room == null)
                {
                    _logger?.LogWarning($"[music] the cave boss's room would not load ({handle.Status}); the fight "
                        + "will be fought without its music.");
                    return null;
                }

                _searchedForMusic = true;
                _music = room.GetComponentInChildren<MusicTrigger>(includeInactive: true);
                if (_music == null)
                {
                    _logger?.LogWarning("[music] the cave boss's room carries no music trigger any more; the fight "
                        + "will be fought without its music.");
                    return null;
                }

                _logger?.Log("[music] the boss's battle music has been found on its own room.");
                return _music;
            }
            catch (Exception exception)
            {
                // Most likely the game has not finished loading its catalogue yet, which reads exactly like the
                // room being absent. Leave the next attempt to the next encounter.
                _logger?.LogWarning($"[music] the battle music could not be fetched ({exception.Message}); trying "
                    + "again at the next encounter.");
                return null;
            }
        }
    }
}
