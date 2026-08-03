#nullable disable

using PerfectRandom.Sulfur.Core.Items;
using UnityEngine;
using UnityEngine.UI;

namespace FalseGods.Farm.Seeds
{
    /// <summary>
    /// The corner badge that says an inventory item is marked, following the shape of the game's own
    /// <c>brokenIcon</c>: one <see cref="Image"/> that lives on the item and is switched on and off.
    /// </summary>
    /// <remarks>
    /// <para><b>Why a badge at all.</b> Vanilla draws nothing for an oil's enchantments - the tooltip's
    /// enchantment block sits inside the weapon branch, behind <c>TypeIsEnchantable</c> - so the mark is
    /// invisible by default and its whole presentation is ours to build.</para>
    ///
    /// <para><b>Parented to the item root, not to the artwork container.</b> The container is what the game
    /// rotates by -90 degrees for a rotated item, which is why <c>UpdateBrokenIndicators</c> has to force the
    /// broken icon's rotation back to identity every time it runs. The root never rotates and is resized to
    /// the item's current footprint, so a badge anchored to its corner is correct in every orientation with
    /// no per-frame correction and no second patch. Being the last child of the root also puts it on top.</para>
    ///
    /// <para><b>The sprite is generated, not shipped.</b> It is a two-tone glyph in a small texture created at
    /// runtime: no asset to package, and it renders through uGUI's own default material, which is the one
    /// thing this repository knows will not come out pink on someone else's machine.</para>
    /// </remarks>
    internal static class SeedBadge
    {
        private const string BadgeObjectName = "FalseGods_SeedBadge";

        private const int TextureSize = 24;
        private const float BadgePixels = 15f;
        private const float BadgeInset = 2f;

        private static Sprite _sprite;

        /// <summary>
        /// Brings the badge on this item into line with whether it is marked. Safe to call repeatedly; at most
        /// one badge is ever created per item.
        /// </summary>
        public static void Refresh(InventoryItem item, bool marked)
        {
            if (item == null)
            {
                return;
            }

            var existing = item.transform.Find(BadgeObjectName);

            if (!marked)
            {
                if (existing != null)
                {
                    existing.gameObject.SetActive(false);
                }

                return;
            }

            var badge = existing != null ? existing.gameObject : Create(item.transform);

            badge.transform.SetAsLastSibling();
            badge.SetActive(true);
        }

        private static GameObject Create(Transform parent)
        {
            var badge = new GameObject(BadgeObjectName, typeof(RectTransform), typeof(Image));

            var rect = (RectTransform)badge.transform;
            rect.SetParent(parent, worldPositionStays: false);
            rect.anchorMin = Vector2.zero;
            rect.anchorMax = Vector2.zero;
            rect.pivot = Vector2.zero;
            rect.sizeDelta = new Vector2(BadgePixels, BadgePixels);
            rect.anchoredPosition = new Vector2(BadgeInset, BadgeInset);
            rect.localRotation = Quaternion.identity;
            rect.localScale = Vector3.one;

            var image = badge.GetComponent<Image>();
            image.sprite = Glyph();
            image.color = SeedPalette.Text;

            // The item beneath owns every pointer event in this rect - drag, hover, the tooltip. A badge that
            // ate them would make a marked item behave differently from an unmarked one.
            image.raycastTarget = false;

            return badge;
        }

        /// <summary>
        /// A seed: a filled diamond inside a dark rim. The fill is white so <see cref="Image.color"/> tints it
        /// to the palette colour; the rim is black, which multiplication leaves black whatever the tint.
        /// </summary>
        private static Sprite Glyph()
        {
            if (_sprite != null)
            {
                return _sprite;
            }

            var texture = new Texture2D(TextureSize, TextureSize, TextureFormat.RGBA32, mipChain: false)
            {
                name = "FalseGods_SeedBadgeGlyph",
                hideFlags = HideFlags.HideAndDontSave,
                filterMode = FilterMode.Bilinear,
                wrapMode = TextureWrapMode.Clamp,
            };

            var pixels = new Color32[TextureSize * TextureSize];
            var centre = (TextureSize - 1) * 0.5f;
            var outerRadius = centre;
            var innerRadius = centre - 2.5f;

            for (var y = 0; y < TextureSize; y++)
            {
                for (var x = 0; x < TextureSize; x++)
                {
                    // Manhattan distance from the centre draws a diamond; two thresholds make it a rim
                    // around a fill.
                    var distance = Mathf.Abs(x - centre) + Mathf.Abs(y - centre);

                    pixels[y * TextureSize + x] =
                        distance <= innerRadius ? new Color32(255, 255, 255, 255) :
                        distance <= outerRadius ? new Color32(0, 0, 0, 255) :
                        new Color32(0, 0, 0, 0);
                }
            }

            texture.SetPixels32(pixels);
            texture.Apply(updateMipmaps: false, makeNoLongerReadable: true);

            _sprite = Sprite.Create(
                texture,
                new Rect(0f, 0f, TextureSize, TextureSize),
                new Vector2(0.5f, 0.5f),
                pixelsPerUnit: 100f);
            _sprite.name = "FalseGods_SeedBadge";
            _sprite.hideFlags = HideFlags.HideAndDontSave;

            return _sprite;
        }
    }
}
