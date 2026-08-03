#nullable disable

using System;
using BepInEx.Logging;
using HarmonyLib;
using PerfectRandom.Sulfur.Core.Items;
using UnityEngine;
using ItemDescriptionPanel = PerfectRandom.Sulfur.Core.UI.ItemDescription.ItemDescription;

namespace FalseGods.Farm.Seeds
{
    /// <summary>
    /// The two places the game itself decides what an item looks like, postfixed so the seed mark appears
    /// wherever an item does.
    /// </summary>
    /// <remarks>
    /// <para>Both are canonical event sources rather than correlates. <c>InventoryItem.Setup</c> is the method
    /// that builds an item's view AND the one that restores its enchantments from save data
    /// (<c>SetupEnchantmentsFromData</c> runs inside it), so a mark that came off disk and the badge that
    /// shows it are decided in the same call. <c>ItemDescription.Setup</c> is where the tooltip is built, once
    /// per hover. Nothing here polls, watches a name, or waits a frame.</para>
    ///
    /// <para>Every body is wrapped: a postfix that throws propagates into the caller, and a cosmetic badge
    /// must never be able to take the inventory down with it.</para>
    /// </remarks>
    internal static class SeedMarkPatches
    {
        private static SeedMarkRegistry _marks;
        private static ManualLogSource _log;

        public static void Bind(SeedMarkRegistry marks, ManualLogSource log)
        {
            _marks = marks;
            _log = log;
        }

        /// <summary>
        /// Re-runs the presentation for one item after its mark changed outside a vanilla setup call.
        /// </summary>
        public static void RefreshItem(InventoryItem item)
        {
            if (item == null || _marks == null)
            {
                return;
            }

            SeedBadge.Refresh(item, _marks.IsMarked(item));
        }

        /// <summary>
        /// Never called. It exists so the two overloads the attributes below name by reflection are also
        /// named by the compiler: a method-group conversion binds to one specific overload, so if a game
        /// update changes either signature this stops building instead of quietly failing to patch and
        /// leaving the mark invisible with nothing but a log line to say so.
        /// </summary>
        private static void PinPatchTargets(InventoryItem item, ItemDescriptionPanel panel)
        {
            Action<Vector2, ItemDefinition, ItemGrid, int, int, InventoryData, bool> itemSetup = item.Setup;
            Action<InventoryItem> descriptionSetup = panel.Setup;

            _ = itemSetup;
            _ = descriptionSetup;
        }

        [HarmonyPatch(typeof(InventoryItem), nameof(InventoryItem.Setup),
            typeof(Vector2), typeof(ItemDefinition), typeof(ItemGrid), typeof(int), typeof(int), typeof(InventoryData), typeof(bool))]
        [HarmonyPostfix]
        private static void InventoryItemSetup_Post(InventoryItem __instance)
        {
            try
            {
                if (_marks == null)
                {
                    return;
                }

                SeedBadge.Refresh(__instance, _marks.IsMarked(__instance));
            }
            catch (Exception exception)
            {
                _log?.LogError($"Seed badge failed for an inventory item: {exception}");
            }
        }

        [HarmonyPatch(typeof(ItemDescriptionPanel), nameof(ItemDescriptionPanel.Setup), typeof(InventoryItem))]
        [HarmonyPostfix]
        private static void ItemDescriptionSetup_Post(ItemDescriptionPanel __instance, InventoryItem inventoryItem)
        {
            try
            {
                if (_marks == null)
                {
                    return;
                }

                SeedTooltipLine.Refresh(__instance, _marks.IsMarked(inventoryItem), _log);
            }
            catch (Exception exception)
            {
                _log?.LogError($"Seed tooltip line failed for an item description: {exception}");
            }
        }
    }
}
