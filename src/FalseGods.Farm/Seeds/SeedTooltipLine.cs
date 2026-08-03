#nullable disable

using System.Reflection;
using BepInEx.Logging;
using PerfectRandom.Sulfur.Core;
using UnityEngine;
using ItemDescriptionPanel = PerfectRandom.Sulfur.Core.UI.ItemDescription.ItemDescription;

namespace FalseGods.Farm.Seeds
{
    /// <summary>
    /// The extra tooltip line a marked item gets, and the owner of its lifetime.
    /// </summary>
    /// <remarks>
    /// <para><b>Why a clone.</b> Vanilla appends a line by taking one from <c>descriptionTextPrefabPool</c>,
    /// setting its text and giving it a sibling index. All four pools are private, so a postfix cannot borrow
    /// one. Instantiating the panel's own <c>descriptionTextPrefab</c> is what the pool's factory does anyway
    /// (<c>Instantiate(prefab, prefabPoolRoot)</c>), and it arrives with the right font, material and layout
    /// already on it - the same borrow-a-live-thing habit the rest of this mod uses instead of authoring a
    /// replacement.</para>
    ///
    /// <para><b>The lifetime trap, and how it is closed.</b> Vanilla's lines are pool-managed and released back
    /// on <c>ClearDescription</c>; a clone is not, so if one were created per hover, every hover would leak a
    /// line. This component holds at most ONE clone per description panel, reuses it for every subsequent
    /// hover, hides it when the hovered item is not marked, and destroys it explicitly when the panel goes.
    /// There are exactly two panels in the game (primary and secondary), so that is a ceiling of two.</para>
    ///
    /// <para><b>Parked as the last sibling.</b> Vanilla's <c>Setup</c> walks a running <c>childIndex</c> from
    /// the icon's sibling index and assigns it to each element it adds. Sitting at the end means those
    /// assignments never displace this line, and this line never displaces them - and the mark reads as a
    /// footnote, which is what it is.</para>
    ///
    /// <para>It is deliberately NOT registered with <c>SetParentDescription</c>: that would add it to the
    /// panel's gamepad navigation nodes, making an informational line something the player must cursor past.
    /// Its own pointer handlers no-op without a parent description.</para>
    /// </remarks>
    [DisallowMultipleComponent]
    internal sealed class SeedTooltipLine : MonoBehaviour
    {
        private const string LineObjectName = "FalseGods_SeedLine";

        /// <summary>
        /// English placeholder. The game localises through I2 (<c>LocalizationManager.GetTermTranslation</c>),
        /// and this mod already ships its own translated term for the boss title, so the same seam applies
        /// here — but the farm's wording is not settled yet and translating a string that is about to change
        /// wastes the translation. Kept literal, and listed as an open item on the roadmap, until the farm's
        /// vocabulary is fixed by P1/P2 actually being playable.
        /// </summary>
        private const string LineText = "Sown - this can be planted";

        private static readonly FieldInfo PrefabField =
            typeof(ItemDescriptionPanel).GetField("descriptionTextPrefab", BindingFlags.Instance | BindingFlags.NonPublic);

        private static readonly FieldInfo ContentTransformField =
            typeof(ItemDescriptionPanel).GetField("contentTransform", BindingFlags.Instance | BindingFlags.NonPublic);

        private ItemDescriptionText _line;

        /// <summary>
        /// Brings this panel's seed line into line with whether the item it is describing is marked.
        /// </summary>
        public static void Refresh(ItemDescriptionPanel panel, bool marked, ManualLogSource log)
        {
            if (panel == null)
            {
                return;
            }

            var owner = panel.GetComponent<SeedTooltipLine>();

            if (owner == null)
            {
                if (!marked)
                {
                    // Nothing to show and nothing built yet: do not attach a component to every panel that
                    // ever describes an ordinary item.
                    return;
                }

                owner = panel.gameObject.AddComponent<SeedTooltipLine>();
            }

            owner.Apply(panel, marked, log);
        }

        private void Apply(ItemDescriptionPanel panel, bool marked, ManualLogSource log)
        {
            if (!marked)
            {
                if (_line != null)
                {
                    _line.gameObject.SetActive(false);
                }

                return;
            }

            if (_line == null)
            {
                _line = CreateLine(panel, log);
            }

            if (_line == null || _line.textComp == null)
            {
                return;
            }

            _line.textComp.text = LineText;
            _line.textComp.color = SeedPalette.Text;

            _line.transform.SetAsLastSibling();
            _line.gameObject.SetActive(true);

            var shimmer = _line.GetComponent<SeedShimmer>();

            if (shimmer == null)
            {
                shimmer = _line.gameObject.AddComponent<SeedShimmer>();
                shimmer.Bind(_line.textComp);
            }
            else
            {
                shimmer.Restart();
            }
        }

        private static ItemDescriptionText CreateLine(ItemDescriptionPanel panel, ManualLogSource log)
        {
            var prefab = PrefabField?.GetValue(panel) as ItemDescriptionText;
            var parent = ContentTransformField?.GetValue(panel) as Transform;

            if (prefab == null)
            {
                // Fall back to any line the panel already carries - including the inactive flavour-text line
                // and the pooled instances parked under the pool root. Same font, same material, same layout.
                var candidates = panel.GetComponentsInChildren<ItemDescriptionText>(includeInactive: true);

                for (var i = 0; i < candidates.Length; i++)
                {
                    if (candidates[i] != null && candidates[i].textComp != null)
                    {
                        prefab = candidates[i];
                        parent = parent != null ? parent : candidates[i].transform.parent;
                        break;
                    }
                }
            }

            if (prefab == null)
            {
                log.LogWarning(
                    "No ItemDescriptionText to clone for the seed line; the mark will show as a badge only. " +
                    "Has the item description panel changed shape?");
                return null;
            }

            var line = Object.Instantiate(prefab, parent != null ? parent : panel.transform);
            line.name = LineObjectName;

            return line;
        }

        /// <summary>Whoever creates the clone destroys it. The panel dying is the only thing that ends it.</summary>
        private void OnDestroy()
        {
            if (_line != null)
            {
                Destroy(_line.gameObject);
                _line = null;
            }
        }
    }
}
