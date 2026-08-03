#nullable disable

using UnityEngine;

namespace FalseGods.Farm.Seeds
{
    /// <summary>
    /// The one place the seed mark's colours are decided, so the badge and the tooltip line cannot drift
    /// apart into looking like two unrelated features.
    /// </summary>
    internal static class SeedPalette
    {
        /// <summary>Resting colour of the marked line, and the badge's fill.</summary>
        public static readonly Color32 Text = new Color32(126, 200, 108, 255);

        /// <summary>What the shimmer lifts a character to as the band passes over it.</summary>
        public static readonly Color32 Highlight = new Color32(226, 255, 214, 255);
    }
}
