using System;
using System.Collections.Generic;
using FalseGods.Application.Combat;
using FalseGods.RuntimeContracts.Arena;
using PerfectRandom.Sulfur.Core;
using PerfectRandom.Sulfur.Core.Items;
using PerfectRandom.Sulfur.Core.LevelGeneration;
using UnityEngine;
using ILogger = FalseGods.RuntimeContracts.Diagnostics.ILogger;

namespace FalseGods.Integration.Sulfur.Combat
{
    /// <summary>
    /// The SULFUR implementation of <see cref="IBossRewardPort"/>: the boss pays out in the game's own items,
    /// through the game's own loot manager.
    /// </summary>
    /// <remarks>
    /// <para><b>How the game pays a boss.</b> Measured on v0.18.5: a boss room authors <c>PlacedLoot</c> anchors
    /// with <c>spawnOnStart = false</c>, and the boss's death calls <c>PlaceLoot()</c> on each. One anchor is
    /// <b>one weighted draw</b> — <c>LootManager.SelectItemsFromTable</c> returns a single item — so how much a
    /// boss gives is the number of anchors, and how <i>good</i> it is is the weight distribution. The vanilla cave
    /// boss has exactly two anchors (its own table and a suitcase) plus a thousand in money.</para>
    /// <para><b>What this one is worth, and how that was decided.</b> Items carry a <c>basePrice</c>, so a table's
    /// payout can be priced rather than guessed: the vanilla cave boss's is <b>2473</b> a draw, its suitcase
    /// <b>10</b>, and its money <b>1000</b> — about <b>3483</b> in total. The user asked for three times that in
    /// combined value. Two draws at 4069 plus 2500 in money comes to <b>10639</b>, which is <b>3.05x</b>.</para>
    /// <para><b>Better rather than more.</b> The extra value is deliberately spent on the quality of two draws
    /// instead of the count of four: drawing a low table four times gives more of what the players already have.
    /// At 4069 a draw this table sits between the second-act boss (3640) and the desert boss (4658) — where this
    /// fight belongs — and the lowest quality band drops from the vanilla cave boss's 52% of the weight to 22%.
    /// </para>
    /// <para>Destined for authored boss content, like the minion roster and the crate constants.</para>
    /// </remarks>
    public sealed class SulfurBossReward : IBossRewardPort
    {
        /// <summary>How many independent draws the table is asked for. See the class remarks for why it is two.
        /// </summary>
        private const int Draws = 2;

        /// <summary>What the boss is carrying, in the same money the vanilla cave boss's thousand is counted in.
        /// </summary>
        private const int Money = 2500;

        /// <summary>
        /// The boss's table: the vanilla cave boss's own item pool, re-weighted to this fight's tier.
        /// </summary>
        /// <remarks>
        /// <para><b>The same pool on purpose.</b> Every item here is one the cave already gives, so nothing drops
        /// that belongs to another act — no forest cooking manual out of a goblin. What changes is the weight,
        /// which is exactly how the game itself expresses a tier: its act-two boss and its cave boss share most of
        /// a weapon list and differ almost entirely in how the weight sits on it.</para>
        /// <para><b>The four starting weapons are gone</b> rather than merely rare — Drifter9, Snut38, Arbiter2 and
        /// PloikaC, all under a thousand. The vanilla cave boss gives one of those roughly two times in five,
        /// which is defensible for the first boss in the game and not for this one.</para>
        /// <para>The bands below are by price, and the weights are what the measured 4069 came out of. Changing a
        /// weight changes what the fight is worth, so re-price it against the vanilla numbers in the class remarks
        /// rather than by eye.</para>
        /// </remarks>
        private static readonly (ItemId Item, float Weight)[] Table =
        {
            // Kept, but no longer the likely outcome: the ordinary guns of the cave, 1000-1900.
            (ItemIds.Weapon_Gravekeeper, 20f),
            (ItemIds.Weapon_Mario, 20f),
            (ItemIds.Weapon_Termite, 20f),
            (ItemIds.Weapon_Knop, 20f),
            (ItemIds.Weapon_Bronco, 20f),
            (ItemIds.Weapon_Mossman, 20f),
            (ItemIds.Weapon_Beck, 20f),
            (ItemIds.Weapon_Breacher8, 20f),
            (ItemIds.Weapon_Socom9, 20f),

            // The middle of the cave's range, 2000-3500, and the bulk of what this boss actually gives.
            (ItemIds.Weapon_WyattPulsar, 30f),
            (ItemIds.Weapon_FlickerZip, 30f),
            (ItemIds.Weapon_StarAndWitness, 30f),
            (ItemIds.Weapon_Augusta, 30f),
            (ItemIds.Weapon_PalehorseTopclipper, 30f),
            (ItemIds.Weapon_Vrede, 30f),
            (ItemIds.Weapon_Balthazar, 30f),
            (ItemIds.Weapon_D4RT, 30f),
            (ItemIds.Weapon_DeathStar, 30f),
            (ItemIds.Weapon_Dolphin99, 30f),
            (ItemIds.Weapon_ImpalaGravita, 30f),
            (ItemIds.Weapon_Flock, 30f),
            (ItemIds.Weapon_Nunchaku, 30f),
            (ItemIds.Weapon_TailorMarksman, 30f),

            // 3800-5000, the band the vanilla cave boss keeps at a weight of eight to fifteen.
            (ItemIds.Weapon_Sai, 40f),
            (ItemIds.Weapon_Bo, 40f),
            (ItemIds.Weapon_Corpsemaker, 40f),
            (ItemIds.Weapon_Katana, 40f),
            (ItemIds.Weapon_Valet, 40f),
            (ItemIds.Item_CryptKey, 40f),
            (ItemIds.Weapon_NeuraxisF22, 40f),
            (ItemIds.Weapon_PierreFusil, 40f),
            (ItemIds.Weapon_Salamander, 40f),
            (ItemIds.Weapon_Typhoon, 40f),

            // 7000-10000. Held at eighteen rather than opened up: this is the band a party should occasionally
            // walk out with, not the band it expects.
            (ItemIds.Weapon_Rokua, 18f),
            (ItemIds.Weapon_M11Ramshack, 18f),
            (ItemIds.Weapon_Catacoil, 18f),
            (ItemIds.Weapon_Duhar, 18f),
            (ItemIds.Weapon_Majordome, 18f),
            (ItemIds.Weapon_Rektor, 18f),
            (ItemIds.Weapon_Ferryman, 18f),
            (ItemIds.Attachment_Insurance, 18f),
            (ItemIds.Manual_GoblinCooking, 18f),
        };

        private readonly ILogger? _logger;

        public SulfurBossReward(ILogger? logger = null)
        {
            _logger = logger;
        }

        public void DropReward(ArenaWorldPoint at)
        {
            var where = new Vector3(at.X, at.Y, at.Z);
            try
            {
                var loot = StaticInstance<LootManager>.Instance;
                if (loot == null)
                {
                    _logger?.LogWarning("[reward] the game has no loot manager to pay through; nothing dropped.");
                    return;
                }

                var table = BuildTable(out var missing);
                if (table == null)
                {
                    _logger?.LogWarning("[reward] not one item of the boss's table could be resolved; nothing "
                        + "dropped. The item database is loaded on demand, so this means it was asked too early.");
                    return;
                }

                // The room the pickups belong to. Our arena is the level's only room, which is the case
                // FindClosestRoom answers without a raycast — so this is exact rather than a nearest guess.
                var room = Room.FindClosestRoom(where);

                for (var i = 0; i < Draws; i++)
                {
                    loot.SpawnLootFrom(table, where, room);
                }

                loot.SpawnMoney(Money, where, room);

                _logger?.Log($"[reward] the boss pays out: {Draws} draw(s) from a table of "
                    + $"{table.entries.Count} and {Money} in money"
                    + (missing > 0 ? $" ({missing} item(s) this build does not have were left out)" : string.Empty)
                    + ". This peer rolled its own.");
            }
            catch (Exception exception)
            {
                // A reward that fails is a disappointing kill, not a broken teardown - and this is called from
                // the same pass that stops the music and takes down the bar.
                _logger?.LogWarning($"[reward] the boss's payout failed ({exception.Message}); nothing dropped.");
            }
        }

        /// <summary>
        /// Turn the table into one the game can draw from, leaving out anything this build cannot resolve.
        /// </summary>
        /// <remarks>
        /// An unresolved entry is dropped rather than added with a null item, because the game's own draw counts a
        /// null entry's weight and then hands back nothing — so a missing item would silently become a chance of
        /// no reward at all rather than one fewer thing on the list.
        /// </remarks>
        private static LootTable? BuildTable(out int missing)
        {
            missing = 0;
            var table = ScriptableObject.CreateInstance<LootTable>();
            table.entries = new List<LootEntry>(Table.Length);
            for (var i = 0; i < Table.Length; i++)
            {
                var definition = Table[i].Item.GetAsset();
                if (definition == null)
                {
                    missing++;
                    continue;
                }

                table.AddEntry(definition, Table[i].Weight);
            }

            return table.entries.Count > 0 ? table : null;
        }
    }
}
