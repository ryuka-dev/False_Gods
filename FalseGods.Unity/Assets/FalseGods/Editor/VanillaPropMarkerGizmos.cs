using UnityEditor;
using UnityEngine;

namespace FalseGods.EditorTools
{
    /// <summary>
    /// Draws a disc in the Scene view at every vanilla-prop marker, so scenery can be placed without guessing.
    /// </summary>
    /// <remarks>
    /// <para><b>It cannot reach the game.</b> This lives in an Editor assembly and puts nothing on the markers —
    /// no component, no child, no renderer — so there is nothing for the bundle to pack even in principle. That is
    /// the whole reason it is a gizmo rather than a placeholder object: a preview mesh would have to be stripped
    /// at build time and remembered about forever, the way the preview textures already have to be.</para>
    /// <para><b>The disc shows the marker's facing, which is the thing worth seeing.</b> A clone is parented to its
    /// marker with an identity local rotation, so it wears the marker's rotation exactly — which is why a path
    /// laid flat needs its marker turned a quarter turn about X, and why the disc lying flat means the prop will
    /// too. The line out of the middle is the marker's up, so a marker that has not been turned is obvious.</para>
    /// </remarks>
    [InitializeOnLoad]
    public static class VanillaPropMarkerGizmos
    {
        /// <summary>Where the markers live, and what a marker is called. Kept in step with the runtime's own
        /// <c>VanillaPropDecoration</c> by hand — a mismatch costs a preview, never a load.</summary>
        private const string MarkerParentPath = "VisualRoot/VanillaProps";

        private const string MarkerNamePrefix = "Prop_";

        /// <summary>How big the disc is. Not the prop's real size — it cannot be known here, since the prop comes
        /// out of the player's own install at load — just enough to see where a thing will stand and which way up.
        /// </summary>
        private const float DiscRadius = 0.6f;

        private static readonly Color Face = new Color(0.35f, 0.85f, 1f, 0.18f);
        private static readonly Color Edge = new Color(0.35f, 0.85f, 1f, 0.9f);

        static VanillaPropMarkerGizmos()
        {
            SceneView.duringSceneGui -= Draw;
            SceneView.duringSceneGui += Draw;
        }

        private static void Draw(SceneView view)
        {
            var stage = UnityEditor.SceneManagement.PrefabStageUtility.GetCurrentPrefabStage();
            if (stage == null)
            {
                return; // only while the arena itself is open; nothing to preview in an ordinary scene
            }

            var parent = stage.prefabContentsRoot.transform.Find(MarkerParentPath);
            if (parent == null)
            {
                return;
            }

            foreach (Transform marker in parent)
            {
                if (!marker.name.StartsWith(MarkerNamePrefix, System.StringComparison.Ordinal))
                {
                    continue;
                }

                // The disc lies in the marker's own XY plane and looks along its forward, so it turns with the
                // marker: flat on the ground once the marker has been turned to put a prop there.
                var normal = marker.forward;
                Handles.color = Face;
                Handles.DrawSolidDisc(marker.position, normal, DiscRadius * marker.lossyScale.x);
                Handles.color = Edge;
                Handles.DrawWireDisc(marker.position, normal, DiscRadius * marker.lossyScale.x);
                Handles.DrawLine(marker.position, marker.position + (marker.up * DiscRadius));
                Handles.Label(marker.position, marker.name);
            }
        }
    }
}
