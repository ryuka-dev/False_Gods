using System;
using System.Reflection;
using FalseGods.Application.Presentation;
using FalseGods.RuntimeContracts.Arena;
using PerfectRandom.Sulfur.Core;
using PerfectRandom.Sulfur.Core.Units;
using PerfectRandom.Sulfur.Gameplay;
using Sonity;
using Sonity.Internal;
using UnityEngine;
using ILogger = FalseGods.RuntimeContracts.Diagnostics.ILogger;

namespace FalseGods.Integration.Sulfur.Presentation
{
    /// <summary>
    /// The SULFUR implementation of <see cref="IBossVoicePort"/>: our boss roars with the cave boss's own roar.
    /// </summary>
    /// <remarks>
    /// <para><b>Where the sound is.</b> It is not an asset with a name we can ask for — it is a field on the
    /// vanilla boss's own helper component, and the reverse-engineered export cannot even tell us where it
    /// originally lived (its path there is a folder the ripper invents for assets it could not place). What it is
    /// reachable through is the boss itself: the game addresses every creature through its unit definition, and a
    /// definition can be asked to load its prefab without spawning anything from it. So the prefab is fetched, the
    /// helper read off it, and the sound kept.</para>
    /// <para><b>Reflection for exactly one field.</b> The helper's sound fields are private — the game plays them
    /// from its own animation events and never had a reason to expose them. Everything else here is compile-time
    /// typed against the game's own assemblies.</para>
    /// <para><b>Played from an object standing where the boss is, which is not the same as played at the boss's
    /// position.</b> The obvious call takes an owner and a position and looks like it needs no object of its own —
    /// but the middleware treats the owner as where the voice <i>is</i> for the purpose of deciding whether anyone
    /// is close enough to hear it, and this sound carries about twenty-five metres. Owned by a plugin object left
    /// at the world origin, a roar from a boss thirty metres away was thrown away before it played. The game
    /// itself never does this: it plays the roar from the boss's own transform. So we keep an object of our own
    /// and move it to the boss first.</para>
    /// <para><b>Fetched ahead of time, and never waited for.</b> Loading a prefab is asynchronous, and a roar is a
    /// moment in a fight rather than a loading beat: the fetch is started when an encounter starts, and a roar that
    /// arrives before it lands is simply not heard. Silence is the failure mode throughout — a build that cannot
    /// find the sound still gets the fight.</para>
    /// <para><b>And retried, because "not yet" and "not there" look the same.</b> The creature database is loaded
    /// asynchronously by the game, so asking too early throws exactly as loudly as asking for something that does
    /// not exist. Treating the first failure as final made the boss silent for a whole session after one early
    /// attempt. A bounded number of tries tells the two apart without turning a genuinely missing sound into a
    /// warning every time the boss is angry.</para>
    /// </remarks>
    public sealed class SulfurBossVoice : IBossVoicePort
    {
        /// <summary>The helper field the vanilla boss keeps its roar in, played from its own intro animation.</summary>
        private const string RoarFieldName = "roarSoundEvent";

        /// <summary>
        /// How much further this roar carries than the one the cave boss makes.
        /// </summary>
        /// <remarks>
        /// <para>The sound is authored to reach about twenty-five metres, which is the room the vanilla boss is
        /// fought in and about a third of ours. Measured: a roar from a boss thirty metres off was culled before it
        /// made a sound, and one from seven metres was heard. A roar that only the nearest player hears is not a
        /// roar — it is the whole room being told what just happened.</para>
        /// <para>Four times twenty-five is a hundred, past the far corner of an eighty-metre room, which is the
        /// same number and the same reasoning as the arms' reach.</para>
        /// <para><b>Asked for per play, never written into the asset.</b> The sound belongs to the game and the
        /// vanilla boss still uses it; changing what it is would change that boss's roar too, for the rest of the
        /// session.</para>
        /// </remarks>
        private const float RoarCarriesTimes = 4f;

        private readonly Transform _mouth;

        // Built once: the sound system takes the same array every time, and a roar should not make garbage.
        private readonly SoundParameterInternals[] _carry =
        {
            new SoundParameterDistanceScale(RoarCarriesTimes),
        };
        private readonly ILogger? _logger;

        /// <summary>How many times to go looking before accepting that the sound is not there. Each rage that
        /// starts without it costs one, so this is "a few fights", not "a few frames".</summary>
        private const int MaxAttempts = 8;

        private SoundEvent? _roar;
        private bool _fetching;
        private int _attempts;
        private int _roars;

        /// <param name="lifetime">What the voice's own object hangs from, so it lives and dies with the plugin
        /// rather than with a level.</param>
        public SulfurBossVoice(Transform lifetime, ILogger? logger = null)
        {
            if (lifetime == null)
            {
                throw new ArgumentNullException(nameof(lifetime));
            }

            // The boss's mouth: an object of our own that is moved to wherever the boss is before it is made to
            // speak. See the note on the class about why the sound is not simply played at a position.
            var mouth = new GameObject("FalseGodsBossVoice");
            mouth.transform.SetParent(lifetime, worldPositionStays: false);
            _mouth = mouth.transform;
            _logger = logger;
        }

        public void Warm() => Fetch();

        public void Roar(ArenaWorldPoint at)
        {
            var roar = _roar;
            if (roar == null)
            {
                Fetch(); // it will be there for the next one
                return;
            }

            try
            {
                _mouth.position = new Vector3(at.X, at.Y, at.Z);
                roar.Play(_mouth, _carry);
                CheckItCarried(roar, first: ++_roars == 1);
            }
            catch (Exception exception)
            {
                _logger?.LogWarning($"[voice] the boss's roar would not play ({exception.Message}).");
            }
        }

        /// <summary>
        /// Check that the roar actually became a sound, and say how far it reached.
        /// </summary>
        /// <remarks>
        /// <b>Kept because a silent roar is invisible from here.</b> Finding the sound, playing it and hearing
        /// nothing looks identical to everything working — which is how a roar culled for being thirty metres away
        /// survived two attempts at fixing the wrong thing. The middleware will say whether it made a voice at all;
        /// asking it, with the distance to the ear beside it, turns that failure into one line. The first roar of a
        /// session also reports the reach it ended up with, which is what says the room-sized carry is really being
        /// applied.
        /// </remarks>
        private void CheckItCarried(SoundEvent roar, bool first)
        {
            try
            {
                var listener = UnityEngine.Object.FindObjectOfType<AudioListener>();
                var away = listener != null
                    ? Vector3.Distance(listener.transform.position, _mouth.position).ToString("0.#") + "m"
                    : "nobody listening";

                var source = roar.GetLastPlayedAudioSource(_mouth);
                if (source == null)
                {
                    _logger?.LogWarning($"[voice] the roar made no sound: nothing was allocated for it, from "
                        + $"{_mouth.position.ToString("0.#")} with the ear {away} off.");
                    return;
                }

                if (first)
                {
                    _logger?.Log($"[voice] the boss roars {source.maxDistance:0.#}m, on the game's "
                        + $"'{(source.outputAudioMixerGroup == null ? "<none>" : source.outputAudioMixerGroup.name)}' "
                        + $"mix; the ear was {away} off and heard it at {source.volume:0.##}.");
                }
            }
            catch (Exception exception)
            {
                _logger?.LogWarning($"[voice] could not tell whether the roar carried ({exception.Message}).");
            }
        }

        /// <summary>Start fetching the vanilla boss's prefab and take the roar off it. At most one attempt is in
        /// flight, and a failure is remembered so a fight does not retry it every rage.</summary>
        private async void Fetch()
        {
            if (_roar != null || _fetching || _attempts >= MaxAttempts)
            {
                return;
            }

            _fetching = true;
            _attempts++;
            try
            {
                var definition = UnitIds.GoblinCousin.GetAsset();
                if (definition == null)
                {
                    Gave("the game has no definition for the cave boss");
                    return;
                }

                // Loads the creature's prefab without putting one in the world - the same handle the game's own
                // spawner would use.
                var prefab = await definition.FetchAndLoadUnitLoader().Task;
                var helper = prefab != null ? prefab.GetComponent<CousinHelper>() : null;
                if (helper == null)
                {
                    Gave("the cave boss carries no helper to take a roar from");
                    return;
                }

                var field = typeof(CousinHelper).GetField(
                    RoarFieldName, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
                _roar = field?.GetValue(helper) as SoundEvent;
                if (_roar == null)
                {
                    Gave($"the cave boss's '{RoarFieldName}' is not where it was");
                    return;
                }

                _logger?.Log($"[voice] the boss has found its voice ('{_roar.name}').");
            }
            catch (Exception exception)
            {
                // Most likely the game has not finished loading its creature database yet, which reads exactly
                // like the sound being absent. Say so quietly and leave the next attempt to the next encounter.
                Gave($"could not be fetched ({exception.Message})");
            }
            finally
            {
                _fetching = false;
            }
        }

        /// <summary>Report a failed attempt, and say it is the last one only when it really is.</summary>
        private void Gave(string reason)
        {
            _logger?.LogWarning(_attempts >= MaxAttempts
                ? $"[voice] the boss's roar {reason}; after {_attempts} tries it will rage silently."
                : $"[voice] the boss's roar {reason}; trying again at the next encounter.");
        }
    }
}
