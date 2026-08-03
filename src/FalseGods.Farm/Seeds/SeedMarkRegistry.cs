#nullable disable

using System.Collections;
using System.Collections.Generic;
using BepInEx.Logging;
using PerfectRandom.Sulfur.Core;
using PerfectRandom.Sulfur.Core.Items;
using PerfectRandom.Sulfur.Core.Stats;
using UnityEngine;

namespace FalseGods.Farm.Seeds
{
    /// <summary>
    /// Owns the seed mark: one minted <see cref="EnchantmentDefinition"/> that carries no modifier at all,
    /// one minted <see cref="ItemDefinition"/> that applies it and is never obtainable, and the two
    /// operations that put the mark on a real vanilla item and take it off again.
    /// </summary>
    /// <remarks>
    /// <para><b>Why a mark and not a seed item.</b> A seed must occupy a backpack slot (inventory pressure is
    /// one of SULFUR's core tensions) and must still be usable as the ordinary oil it is. Minting a seed
    /// <i>item</i> per seedable oil would mean maintaining a parallel copy of every one of them and
    /// re-adapting after every balance patch. So the seed IS the vanilla oil, with one bit of extra state -
    /// and the only per-item state SULFUR serialises that an oil never uses is
    /// <c>InventoryData.enchantmentIds</c>. See Docs/FarmExpansionRoadmap.md §7.1.</para>
    ///
    /// <para><b>The restore path does not filter by item type.</b> <c>InventoryItem.SetupEnchantmentsFromData</c>
    /// resolves each stored <c>ItemId</c> and calls <c>AddEnchantment</c> with no check that the item is
    /// enchantable, so a mark written on an oil comes back on load. The old claim that "oils cannot carry an
    /// enchantment" came from <c>GetItemsCompatibleWithEnchantment</c>, which governs what the UI OFFERS -
    /// not what the data model stores.</para>
    ///
    /// <para><b>The failure mode is soft.</b> Ids are positional (<c>id.value == index + 1</c>), so a future
    /// vanilla item could take the id this marker was appended at. <c>ItemDatabase</c>'s indexer bounds-checks
    /// and returns null, on which the restore path warns and breaks: the mark is lost and the oil is still an
    /// ordinary oil. Compare minting a seed item, where the seed itself would silently become something else.</para>
    ///
    /// <para><b>One known side effect, and it is cosmetic.</b> <c>AddEnchantment</c> reports
    /// <c>OIL_IT_UP</c> ("Well Oiled" - apply 5 oils to one weapon) with a count of 1, and in
    /// <c>SetLocalStat</c> the bare field assignment happens before the <c>onlyIncreaseValue</c> guard. The
    /// guard still blocks everything that matters - no dirty flag, so no platform stat sync, no progress
    /// event, no unlock check - and the stored value gates nothing, because the unlock fires from the
    /// instantaneous count at the moment of enchanting. Vanilla writes the same 1 down every time a player
    /// oils a fresh weapon. Not worth a patch on the load path.</para>
    /// </remarks>
    internal sealed class SeedMarkRegistry
    {
        /// <summary>
        /// Asset names, not ids: ids are positional and can move between game versions, but the name is what
        /// a re-mint recognises and what <c>LocalizationManager</c> would key on if this is ever translated.
        /// </summary>
        public const string MarkerEnchantmentAssetName = "FalseGods_SeedMark";

        public const string MarkerItemAssetName = "FalseGods_SeedMarker";

        private const string MarkerDisplayName = "Sown";

        private readonly ManualLogSource _log;

        private EnchantmentDefinition _enchantment;
        private ItemDefinition _applier;

        public SeedMarkRegistry(ManualLogSource log)
        {
            _log = log;
        }

        /// <summary>True once both halves of the marker pair exist and marking is possible.</summary>
        public bool IsReady => _enchantment != null && _applier != null;

        /// <summary>
        /// Waits on the game's own asset-readiness flag, then mints the marker pair exactly once.
        /// </summary>
        /// <remarks>
        /// <c>AsyncAssetLoading.loadingDone</c> is not a heuristic: a dozen vanilla coroutines wait on this
        /// same flag before touching a database, and it is set only after the item, unit, enchantment and
        /// recipe databases have all been resolved through Addressables.
        /// </remarks>
        public IEnumerator MintWhenAssetsAreLoaded()
        {
            while (StaticInstance<AsyncAssetLoading>.Instance == null ||
                   !StaticInstance<AsyncAssetLoading>.Instance.loadingDone)
            {
                yield return null;
            }

            var loading = StaticInstance<AsyncAssetLoading>.Instance;
            Mint(loading.itemDatabase, loading.enchantmentDatabase);
        }

        /// <summary>Is this item carrying the seed mark?</summary>
        public bool IsMarked(InventoryItem item)
        {
            if (item == null || _enchantment == null)
            {
                return false;
            }

            var enchantments = item.enchantments;

            return enchantments != null && enchantments.Contains(_enchantment);
        }

        /// <summary>
        /// Puts the mark on a real item. Returns false when it was already marked or minting has not happened.
        /// </summary>
        /// <remarks>
        /// <b>Through <c>AddEnchantment</c>, never through <c>enchantments.Add</c>.</b> The list's getter is
        /// public, so adding to it directly looks like a free way to skip the achievement call - and it
        /// produces a mark that does not survive a save. <c>GetSerializedEnchantments</c> writes
        /// <c>enchantments[i].ItemThatAppliedThis.id</c>, and <c>ItemThatAppliedThis</c> is set only by the
        /// <c>RegisterAppliedBy</c> call inside <c>AddEnchantment</c>. Bypassing it means doing BOTH, which
        /// buys nothing over just calling the real method.
        /// </remarks>
        public bool TryMark(InventoryItem item)
        {
            if (!IsReady || item == null || IsMarked(item))
            {
                return false;
            }

            item.AddEnchantment(_applier, announce: false);

            return IsMarked(item);
        }

        /// <summary>Takes the mark off. Returns false when it was not marked.</summary>
        /// <remarks>
        /// Deliberately NOT <c>InventoryItem.RemoveEnchantment</c>: that method removes the enchantment's stat
        /// modifiers and never removes the entry from the list, so the mark would survive the call and be
        /// written out again on the next save. Removing from the public list is the whole operation here,
        /// because the mark contributes no modifier to withdraw in the first place.
        /// </remarks>
        public bool TryUnmark(InventoryItem item)
        {
            if (!IsMarked(item))
            {
                return false;
            }

            item.enchantments.Remove(_enchantment);
            item.SyncWithInstancedVersion();

            return true;
        }

        private void Mint(ItemDatabase items, EnchantmentDatabase enchantments)
        {
            if (IsReady)
            {
                return;
            }

            if (items == null || enchantments == null)
            {
                _log.LogError("Asset loading reported done but a database is missing; the seed mark cannot be minted.");
                return;
            }

            // Both databases are plain Lists behind a public GetRawList() returning the live list, so
            // appending needs no reflection. (An earlier draft assumed they were fixed arrays, extrapolating
            // from RecipeDatabase, which genuinely is one.)
            var enchantmentList = enchantments.GetRawList();
            var itemList = items.GetRawList();

            _enchantment = FindByName(enchantmentList, MarkerEnchantmentAssetName) ?? MintEnchantment(enchantmentList);
            _applier = FindByName(itemList, MarkerItemAssetName) ?? MintApplier(itemList, _enchantment);

            _log.LogInfo(
                $"Seed mark minted: enchantment '{MarkerEnchantmentAssetName}' id {_enchantment.id.value} " +
                $"of {enchantmentList.Count}, applier '{MarkerItemAssetName}' id {_applier.id.value} of {itemList.Count}.");
        }

        private EnchantmentDefinition MintEnchantment(List<EnchantmentDefinition> enchantmentList)
        {
            var enchantment = ScriptableObject.CreateInstance<EnchantmentDefinition>();

            // Not an asset on disk and not owned by a scene: without this it is a candidate for Unity's
            // unused-asset sweep on the next level load, and the mark would evaporate mid-run.
            enchantment.hideFlags = HideFlags.HideAndDontSave;
            enchantment.name = MarkerEnchantmentAssetName;
            enchantment.enchantmentName = MarkerDisplayName;
            enchantment.textColor = SeedPalette.Text;

            // AddEnchantment walks this list twice (RemoveModifiersFromList, then .Any) before it reaches any
            // null check, so it must exist. Empty is the point: the mark is data on the item and must never
            // move a number, or a marked oil would stop being an ordinary oil.
            enchantment.modifiersApplied = new List<ItemModifierContainer>();

            // InventoryItem's durability walk charges a flat cost for any enchantment that costs durability,
            // even one carrying no durability modifier of its own. It skips this one only because of this flag.
            enchantment.CostsDurability = false;
            enchantment.IsElemental = false;

            enchantmentList.Add(enchantment);

            // Positional ids: value == index + 1.
            enchantment.id = new EnchantmentId { value = (ushort)enchantmentList.Count };

            return enchantment;
        }

        private ItemDefinition MintApplier(List<ItemDefinition> itemList, EnchantmentDefinition enchantment)
        {
            var applier = ScriptableObject.CreateInstance<ItemDefinition>();

            applier.hideFlags = HideFlags.HideAndDontSave;
            applier.name = MarkerItemAssetName;
            applier.displayName = MarkerDisplayName;

            // AddEnchantment reads appliesEnchantment and nothing else; useType is set to match so the
            // definition is self-consistent if anything ever asks it what it is.
            applier.useType = UseType.Enchantment;
            applier.appliesEnchantment = enchantment.id;

            // The applier never enters a grid, but a marked item's price walk still reads it: the sell/buy
            // price adds 0.75 x basePrice for every enchantment carried. Left at the default 100, marking an
            // oil would quietly make it worth 75 more.
            applier.basePrice = 0;
            applier.sellable = false;
            applier.maxDurability = 0f;
            applier.inventorySize = new Vector2Int(1, 1);

            // Belt and braces on "never obtainable". The vanilla spawn-all-items cheat reads
            // Resources.LoadAll rather than the database, so it cannot see a runtime-appended definition at
            // all - but a future path that walks the database would honour this.
            applier.includedInSpawnAllItems = false;
            applier.excludeFromLocalization = true;
            applier.doNotAnnounce = true;
            applier.npcsCanPickup = false;

            // ScriptableObject.CreateInstance leaves every list field without an initialiser null. None of
            // them is on a path this definition can reach today; empty lists cost nothing and mean a future
            // one cannot turn into a null dereference inside vanilla code.
            applier.modifiersOnEquipNew = new List<ModifierContainer>();
            applier.modifiersOnInventoryNew = new List<ModifierContainer>();
            applier.removeStatusOnConsume = new List<EntityAttributes>();
            applier.addStatusOnConsume = new List<ApplyStatusContainer>();
            applier.modifiersOnAttachToItem = new List<ItemModifierContainer>();
            applier.buffsOnConsume = new List<BuffDefinition>();
            applier.valueChangeOnItemConsume = new List<ItemConsumeContainer>();
            applier.resourceOnConsume = new List<WorldResourceModifierContainer>();
            applier.recipesTaughtOnConsume = new List<RecipeId>();
            applier.baseAttributes = new List<ItemAttributeContainer>();

            itemList.Add(applier);

            applier.id = new ItemId { value = (ushort)itemList.Count };

            return applier;
        }

        /// <summary>
        /// Finds an already-minted half by asset name, so a plugin reload cannot append a second marker pair
        /// and leave items carrying the first one looking unmarked.
        /// </summary>
        private static T FindByName<T>(List<T> definitions, string assetName) where T : Object
        {
            for (var i = 0; i < definitions.Count; i++)
            {
                var definition = definitions[i];

                if (definition != null && definition.name == assetName)
                {
                    return definition;
                }
            }

            return null;
        }
    }
}
