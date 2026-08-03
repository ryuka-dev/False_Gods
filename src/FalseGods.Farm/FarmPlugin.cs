#nullable disable

using System;
using BepInEx;
using BepInEx.Configuration;
using FalseGods.Farm.Seeds;
using HarmonyLib;
using PerfectRandom.Sulfur.Core;
using PerfectRandom.Sulfur.Core.UI;
using UnityEngine.InputSystem;

namespace FalseGods.Farm
{
    /// <summary>
    /// The farm expansion's BepInEx plugin: its own entry point, its own Harmony instance, its own config
    /// file, shipped inside the base mod's package.
    /// </summary>
    /// <remarks>
    /// <para><b>Its own plugin, and no CLR coupling.</b> The <c>[BepInDependency]</c> on the base plugin is a
    /// GUID string, exactly as the SULFUR Together adapter's is - it pins load order and couples no types. The
    /// farm references no FalseGods assembly at all today, which is what keeps <c>FalseGods.Core</c>
    /// boss-shaped and makes a future separate release a packaging change rather than a rewrite
    /// (Docs/FarmExpansionRoadmap.md decision 1).</para>
    ///
    /// <para><b>What exists so far is P0: the seed mark's presentation.</b> Marking a vanilla oil, a corner
    /// badge on the item, an extra tooltip line, and a shimmer along that line. The plot, the tending and the
    /// growth ticks (P1) and the shrine transformation that would mark an oil in ordinary play (P2) are not
    /// built, which is why marking is still reached with a development key.</para>
    /// </remarks>
    [BepInPlugin(PluginGuid, PluginName, PluginVersion)]
    [BepInDependency(BasePluginGuid)]
    public sealed class FarmPlugin : BaseUnityPlugin
    {
        public const string PluginGuid = "ryuka.sulfur.false_gods_farm";
        public const string PluginName = "False Gods - Farm";
        public const string PluginVersion = "0.4.1";

        /// <summary>The False Gods base plugin (FalseGodsPlugin.PluginGuid - a string on purpose).</summary>
        private const string BasePluginGuid = "ryuka.sulfur.false_gods";

        private Harmony _harmony;
        private SeedMarkRegistry _marks;
        private ConfigEntry<Key> _markKey;

        private void Awake()
        {
            _marks = new SeedMarkRegistry(Logger);

            // TEMPORARY (P0 bring-up). Nothing in ordinary play marks an oil yet - that is P2's shrine
            // transformation station - so the mark has to be reachable by hand to be looked at in game. This
            // key and the section it lives in go when the shrine lands, the same way the arena's H key went
            // once beating the cave boss opened the way there.
            _markKey = Config.Bind("Dev", "ToggleSeedMarkKey", Key.F8,
                "[DEV/TEMPORARY - removed when the shrine transformation lands] Toggle the seed mark on the "
                + "inventory item currently hovered or navigated to. Only an oil (an enchantment item) can be "
                + "marked. The game uses the new Input System, so this is a UnityEngine.InputSystem.Key name.");

            SeedMarkPatches.Bind(_marks, Logger);

            _harmony = new Harmony(PluginGuid);

            try
            {
                _harmony.PatchAll(typeof(SeedMarkPatches));
            }
            catch (Exception exception)
            {
                // A failed patch means the mark is invisible, not that the game is broken. Say so loudly and
                // carry on rather than taking BepInEx's whole plugin load down with an exception from Awake.
                Logger.LogError($"Could not patch the inventory presentation; the seed mark will not show: {exception}");
            }

            StartCoroutine(_marks.MintWhenAssetsAreLoaded());

            Logger.LogMessage($"{PluginName} {PluginVersion} loaded. Seed mark toggle: {_markKey.Value}.");
        }

        private void OnDestroy()
        {
            _harmony?.UnpatchSelf();
            _harmony = null;

            SeedMarkPatches.Bind(null, null);
        }

        private void Update()
        {
            if (!KeyPressed(_markKey.Value))
            {
                return;
            }

            ToggleMarkOnHoveredItem();
        }

        private void ToggleMarkOnHoveredItem()
        {
            if (!_marks.IsReady)
            {
                Logger.LogWarning("The seed mark has not been minted yet; the game's databases are still loading.");
                return;
            }

            var inventory = StaticInstance<UIManager>.Instance != null
                ? StaticInstance<UIManager>.Instance.InventoryUI
                : null;

            var item = inventory != null ? inventory.hoveredOrNavigatedInventoryItem : null;

            if (item == null || item.itemDefinition == null)
            {
                Logger.LogMessage("No inventory item is hovered; open the inventory and point at one.");
                return;
            }

            if (!item.itemDefinition.IsEnchantment)
            {
                Logger.LogMessage($"{item.itemDefinition.name} is not an oil, so it cannot carry a seed mark.");
                return;
            }

            var marked = _marks.IsMarked(item) ? !_marks.TryUnmark(item) : _marks.TryMark(item);

            Logger.LogMessage($"{item.itemDefinition.name} is now {(marked ? "marked" : "unmarked")}.");

            SeedMarkPatches.RefreshItem(item);

            // Rebuild the open tooltip so the line appears or goes without needing a re-hover. This is the
            // game's own "describe this item" entry point, so the description is rebuilt exactly as a hover
            // would rebuild it - including our postfix.
            inventory.ShowInfoOnItem(item);
        }

        private static bool KeyPressed(Key key)
        {
            try
            {
                var keyboard = Keyboard.current;

                return keyboard != null && keyboard[key].wasPressedThisFrame;
            }
            catch (Exception)
            {
                // No keyboard device, or an unmapped key.
                return false;
            }
        }
    }
}
