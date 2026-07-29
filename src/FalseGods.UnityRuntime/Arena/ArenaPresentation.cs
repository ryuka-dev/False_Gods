// Unity-interop file — opted out of the nullable-reference context like the other UnityRuntime renderers.
#nullable disable

using FalseGods.RuntimeContracts.Presentation;
using UnityEngine;
using ILogger = FalseGods.RuntimeContracts.Diagnostics.ILogger;

namespace FalseGods.UnityRuntime.Arena
{
    /// <summary>
    /// The arena's visual/realized reaction to the encounter's presentation cues — the arena counterpart of
    /// <c>BossPresentation</c> on the same <see cref="IEncounterPresentation"/> seam (Architecture §7).
    /// </summary>
    /// <remarks>
    /// Decides nothing: a mechanism engaging and the exit unlocking are authoritative <c>ArenaSimulation</c>
    /// results that arrive here as cues, identically in single-player, on the host, and on a client. The
    /// minimal PoC-room reactions are deliberate (the room authors no mechanism objects yet — an authoring
    /// change means a new bundle and hash): the phase-two group tints the arena's own lights, and the opened
    /// exit deactivates the authored north wall so players can actually leave. Boss cues are ignored here, and
    /// arena cues are ignored by <c>BossPresentation</c> — the composition fans each cue to both.
    /// <para><see cref="Apply"/> is a no-op: continuous arena presentation state is not modelled yet (the
    /// snapshot's active groups replay as idempotent cues on late join instead).</para>
    /// </remarks>
    public sealed class ArenaPresentation : IEncounterPresentation
    {
        /// <summary>
        /// The pile of rock closing the way out — an authored holder, so what is over the doorway is however many
        /// rocks it took to fill it and taking them away is one call.
        /// </summary>
        /// <remarks>
        /// Taken away when the exit unlocks, which every peer does for itself off the replicated fact — so a peer
        /// that arrived after the boss died finds the doorway already clear rather than having to be told.
        /// </remarks>
        private const string ExitBlockerPath = "VisualRoot/VanillaProps/ExitRoom/ExitBlocker";
        private const string LightingRootName = "LightingRoot";
        private static readonly Color EngagedLightColor = new Color(1f, 0.45f, 0.35f);

        private readonly BundleArenaRealization _realization;
        private readonly ILogger _logger;

        public ArenaPresentation(BundleArenaRealization realization, ILogger logger = null)
        {
            _realization = realization ?? throw new System.ArgumentNullException(nameof(realization));
            _logger = logger;
        }

        public void Apply(PresentationState state)
        {
            // Boss continuous state; nothing arena-visual is continuous yet.
        }

        public void Handle(IPresentationEvent presentationEvent)
        {
            var root = _realization.CurrentRoot;
            if (root == null)
            {
                return; // no realized arena to show anything on
            }

            switch (presentationEvent)
            {
                case MechanismGroupEngaged e:
                    TintLights(root);
                    _logger?.Log($"[arena-cue] MechanismGroupEngaged '{e.Group.Value}' — lights tinted");
                    break;
                case ExitOpened _:
                    OpenExit(root);
                    break;
            }
        }

        private static void TintLights(GameObject root)
        {
            var lighting = root.transform.Find(LightingRootName);
            var lights = (lighting != null ? lighting : root.transform).GetComponentsInChildren<Light>(true);
            for (var i = 0; i < lights.Length; i++)
            {
                lights[i].color = EngagedLightColor;
            }
        }

        private void OpenExit(GameObject root)
        {
            var blocker = root.transform.Find(ExitBlockerPath);
            if (blocker != null)
            {
                blocker.gameObject.SetActive(false);
                _logger?.Log("[arena-cue] ExitOpened — the rock over the doorway is gone");
            }
            else
            {
                _logger?.LogWarning($"[arena-cue] ExitOpened but '{ExitBlockerPath}' was not found");
            }
        }
    }
}
