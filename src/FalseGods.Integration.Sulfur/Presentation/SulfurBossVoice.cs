using System;
using System.Reflection;
using FalseGods.Application.Presentation;
using FalseGods.RuntimeContracts.Arena;
using PerfectRandom.Sulfur.Core;
using PerfectRandom.Sulfur.Core.Units;
using PerfectRandom.Sulfur.Gameplay;
using Sonity;
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
    /// <para><b>Fetched once, ahead of time, and never waited for.</b> Loading a prefab is asynchronous, and a
    /// roar is a moment in a fight rather than a loading beat: the fetch is started when the encounter starts, and
    /// a roar that arrives before it lands is simply not heard. Silence is the failure mode throughout — a build
    /// that cannot find the sound still gets the fight.</para>
    /// </remarks>
    public sealed class SulfurBossVoice : IBossVoicePort
    {
        /// <summary>The helper field the vanilla boss keeps its roar in, played from its own intro animation.</summary>
        private const string RoarFieldName = "roarSoundEvent";

        private readonly Transform _owner;
        private readonly ILogger? _logger;

        private SoundEvent? _roar;
        private bool _fetching;
        private bool _gaveUp;

        /// <param name="owner">The transform the sound is played under. Sonity uses it as the owner of the voice
        /// rather than as its position, so anything with the plugin's lifetime does — the position is given per
        /// roar.</param>
        public SulfurBossVoice(Transform owner, ILogger? logger = null)
        {
            _owner = owner != null ? owner : throw new ArgumentNullException(nameof(owner));
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
                roar.PlayAtPosition(_owner, new Vector3(at.X, at.Y, at.Z));
            }
            catch (Exception exception)
            {
                _logger?.LogWarning($"[voice] the boss's roar would not play ({exception.Message}).");
            }
        }

        /// <summary>Start fetching the vanilla boss's prefab and take the roar off it. At most one attempt is in
        /// flight, and a failure is remembered so a fight does not retry it every rage.</summary>
        private async void Fetch()
        {
            if (_roar != null || _fetching || _gaveUp)
            {
                return;
            }

            _fetching = true;
            try
            {
                var definition = UnitIds.GoblinCousin.GetAsset();
                if (definition == null)
                {
                    _gaveUp = true;
                    _logger?.LogWarning("[voice] the game has no definition for the cave boss; ours roars silently.");
                    return;
                }

                // Loads the creature's prefab without putting one in the world - the same handle the game's own
                // spawner would use.
                var prefab = await definition.FetchAndLoadUnitLoader().Task;
                var helper = prefab != null ? prefab.GetComponent<CousinHelper>() : null;
                if (helper == null)
                {
                    _gaveUp = true;
                    _logger?.LogWarning("[voice] the cave boss carries no helper to take a roar from; ours roars "
                        + "silently.");
                    return;
                }

                var field = typeof(CousinHelper).GetField(
                    RoarFieldName, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
                _roar = field?.GetValue(helper) as SoundEvent;
                if (_roar == null)
                {
                    _gaveUp = true;
                    _logger?.LogWarning($"[voice] the cave boss's '{RoarFieldName}' is not where it was; ours roars "
                        + "silently.");
                    return;
                }

                _logger?.Log($"[voice] the boss has found its voice ('{_roar.name}').");
            }
            catch (Exception exception)
            {
                _gaveUp = true;
                _logger?.LogWarning($"[voice] the boss's roar could not be fetched ({exception.Message}); it will "
                    + "rage silently.");
            }
            finally
            {
                _fetching = false;
            }
        }
    }
}
