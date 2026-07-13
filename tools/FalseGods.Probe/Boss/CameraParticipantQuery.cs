using System.Collections.Generic;
using FalseGods.Core.Simulation;
using UnityEngine;

namespace FalseGods.Probe.Boss
{
    /// <summary>
    /// A probe stand-in for <see cref="IEncounterParticipantQuery"/>: it presents the player (the main camera) as
    /// the single encounter participant so the boss has someone to face, approach, and aim at.
    /// </summary>
    /// <remarks>
    /// This is the seam the real single-player adapter (<c>Integration.Sulfur</c>) will fill by projecting the game's
    /// players onto <see cref="ParticipantId"/> / <see cref="SimVector2"/>; the probe fills it with just the camera
    /// so the boss cycle runs against a live target without any game roster. The boss only ever <em>observes</em> it
    /// (Docs/DependencyRules.md §3): the query reads the camera, it never moves it. When there is no camera the
    /// roster is empty and the boss idles rather than attacking nothing — the same contract the real adapter honours.
    /// </remarks>
    internal sealed class CameraParticipantQuery : IEncounterParticipantQuery
    {
        private static readonly ParticipantId PlayerId = new ParticipantId(0);
        private readonly ParticipantId[] _single = { PlayerId };
        private readonly ParticipantId[] _none = new ParticipantId[0];

        public IReadOnlyList<ParticipantId> Participants => Camera.main != null ? _single : _none;

        public bool TryGetPosition(ParticipantId participant, out SimVector2 position)
        {
            var camera = Camera.main;
            if (participant != PlayerId || camera == null)
            {
                position = default;
                return false;
            }

            // The arena floor is the horizontal plane; the boss reasons in (X, Z) only (SimVector2).
            var world = camera.transform.position;
            position = new SimVector2(world.x, world.z);
            return true;
        }
    }
}
