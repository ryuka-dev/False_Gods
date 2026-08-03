#nullable disable

using TMPro;
using UnityEngine;

namespace FalseGods.Farm.Seeds
{
    /// <summary>
    /// Sweeps a highlight along one line of TextMeshPro text by animating its vertex colours.
    /// </summary>
    /// <remarks>
    /// <para><b>Why not a shader.</b> There are two ways to move a highlight across TMP text, and this
    /// repository has already paid for the other one: our own stock-URP materials rendered pink in game, and
    /// what shipped was to borrow a vanilla material rather than distribute shader variants. A custom TMP
    /// shader would walk straight back into that and add an asset to the package besides. Writing
    /// <c>colors32</c> and calling <c>UpdateVertexData</c> needs no shader, no material and no asset, and runs
    /// on whatever material the vanilla text already carries - so it cannot come out pink.</para>
    ///
    /// <para><b>Unscaled time.</b> The inventory is open while the game is paused. On scaled time the shimmer
    /// would sit frozen for exactly as long as anyone can see it.</para>
    /// </remarks>
    [DisallowMultipleComponent]
    internal sealed class SeedShimmer : MonoBehaviour
    {
        /// <summary>Half-width of the highlight band, in characters.</summary>
        private const float BandWidthChars = 3.5f;

        private const float SpeedCharsPerSecond = 11f;

        /// <summary>Dark gap after the band leaves the end, so it reads as a sweep rather than a strobe.</summary>
        private const float TrailingGapChars = 7f;

        private TextMeshProUGUI _text;
        private float _phase;

        public void Bind(TextMeshProUGUI text)
        {
            _text = text;
            Restart();
        }

        /// <summary>Sends the band back to before the first character, so every hover starts the same way.</summary>
        public void Restart()
        {
            _phase = 0f;

            if (_text != null)
            {
                // The vertex buffer this animates does not exist until the mesh has been generated, and TMP
                // defers that to the end of frame after a text assignment.
                _text.ForceMeshUpdate();
            }
        }

        private void OnEnable() => Restart();

        private void LateUpdate()
        {
            if (_text == null)
            {
                return;
            }

            var info = _text.textInfo;

            if (info == null || info.characterCount == 0 || info.meshInfo == null)
            {
                _text.ForceMeshUpdate();
                return;
            }

            _phase += Time.unscaledDeltaTime * SpeedCharsPerSecond;

            var span = info.characterCount + BandWidthChars * 2f + TrailingGapChars;
            var head = _phase % span - BandWidthChars;

            for (var i = 0; i < info.characterCount; i++)
            {
                var character = info.characterInfo[i];

                if (!character.isVisible)
                {
                    continue;
                }

                var colours = info.meshInfo[character.materialReferenceIndex].colors32;

                if (colours == null || character.vertexIndex + 3 >= colours.Length)
                {
                    continue;
                }

                var weight = Mathf.Clamp01(1f - Mathf.Abs(i - head) / BandWidthChars);

                // Squared, so the band has a tight core and a soft tail instead of a linear ramp.
                var colour = Color32.Lerp(SeedPalette.Text, SeedPalette.Highlight, weight * weight);

                colours[character.vertexIndex] = colour;
                colours[character.vertexIndex + 1] = colour;
                colours[character.vertexIndex + 2] = colour;
                colours[character.vertexIndex + 3] = colour;
            }

            _text.UpdateVertexData(TMP_VertexDataUpdateFlags.Colors32);
        }
    }
}
