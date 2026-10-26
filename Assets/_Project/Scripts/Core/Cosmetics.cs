using System;
using System.Collections.Generic;
using UnityEngine;

namespace Noxy.Core
{
    public enum CosmeticSlot
    {
        Outfit,
        Shoes,
        Mask,
        Headgear,
        Backpack,
        WeaponSkin,
        Emote
    }

    public enum CosmeticRarity
    {
        Common,
        Rare,
        Epic,
        Legendary
    }

    public static class CosmeticSlotExtensions
    {
        public static string DisplayName(this CosmeticSlot slot)
        {
            switch (slot)
            {
                case CosmeticSlot.WeaponSkin: return "Weapon Skin";
                default: return slot.ToString();
            }
        }

        /// <summary>Identifier used by the backend API (lowercase slot name).</summary>
        public static string ApiName(this CosmeticSlot slot) => slot.ToString().ToLowerInvariant();

        /// <summary>Outfit and shoes are always worn; the rest can be left empty.</summary>
        public static bool IsOptional(this CosmeticSlot slot)
        {
            return slot != CosmeticSlot.Outfit && slot != CosmeticSlot.Shoes;
        }
    }

    /// <summary>
    /// Data for one cosmetic. Visual fields (colors, Variant) drive the placeholder character;
    /// real art can key off Id instead (see PrefabCharacterView).
    /// </summary>
    public sealed class CosmeticItem
    {
        public string Id;
        public string DisplayName;
        public CosmeticSlot Slot;
        public CosmeticRarity Rarity;
        public int PriceCredits;
        public Color PrimaryColor = Color.gray;
        public Color SecondaryColor = Color.white;
        public int Variant;
    }

    public interface ICosmeticCatalog
    {
        IReadOnlyList<CosmeticItem> Items { get; }
        bool TryGet(string id, out CosmeticItem item);
        IEnumerable<CosmeticItem> InSlot(CosmeticSlot slot);
    }

    public sealed class DefaultCosmeticCatalog : ICosmeticCatalog
    {
        private readonly List<CosmeticItem> items = new List<CosmeticItem>();
        private readonly Dictionary<string, CosmeticItem> byId = new Dictionary<string, CosmeticItem>();

        public DefaultCosmeticCatalog()
        {
            // Outfits: Primary = body color, Secondary = trim color.
            Add("outfit_recruit", "Recruit Fatigues", CosmeticSlot.Outfit, CosmeticRarity.Common, 0, Hex("4B5E73"), Hex("9FB3C8"), 0);
            Add("outfit_urban", "Urban Runner", CosmeticSlot.Outfit, CosmeticRarity.Common, 0, Hex("2B2F3A"), Hex("FF8A1F"), 0);
            Add("outfit_nightops", "Night Ops", CosmeticSlot.Outfit, CosmeticRarity.Rare, 0, Hex("14171F"), Hex("2EE6FF"), 0);
            Add("outfit_desert", "Dune Scout", CosmeticSlot.Outfit, CosmeticRarity.Rare, 800, Hex("B79B6B"), Hex("5B4A2E"), 0);
            Add("outfit_crimson", "Crimson Guard", CosmeticSlot.Outfit, CosmeticRarity.Epic, 2200, Hex("7A1420"), Hex("F2C14E"), 0);
            Add("outfit_aurora", "Aurora Suit", CosmeticSlot.Outfit, CosmeticRarity.Legendary, 5000, Hex("2A1F5C"), Hex("5CFFC4"), 0);

            Add("shoes_basic", "Field Boots", CosmeticSlot.Shoes, CosmeticRarity.Common, 0, Hex("3A3A3A"), Hex("777777"), 0);
            Add("shoes_runner", "Sprint Runners", CosmeticSlot.Shoes, CosmeticRarity.Common, 0, Hex("F2F2F2"), Hex("FF8A1F"), 0);
            Add("shoes_tactical", "Tactical Treads", CosmeticSlot.Shoes, CosmeticRarity.Rare, 700, Hex("1E2A1E"), Hex("6FBF73"), 0);
            Add("shoes_neon", "Neon Soles", CosmeticSlot.Shoes, CosmeticRarity.Epic, 1800, Hex("101018"), Hex("FF3DCB"), 0);

            Add("mask_bandana", "Dust Bandana", CosmeticSlot.Mask, CosmeticRarity.Common, 0, Hex("8C2F2F"), Hex("D9D9D9"), 0);
            Add("mask_visor", "Scout Visor", CosmeticSlot.Mask, CosmeticRarity.Rare, 0, Hex("0E1116"), Hex("2EE6FF"), 1);
            Add("mask_gas", "Filter Mask", CosmeticSlot.Mask, CosmeticRarity.Rare, 900, Hex("3A4048"), Hex("A0A8B0"), 2);
            Add("mask_skull", "Bone Mask", CosmeticSlot.Mask, CosmeticRarity.Epic, 2000, Hex("E8E4D8"), Hex("20201C"), 3);

            Add("cap_standard", "Field Cap", CosmeticSlot.Headgear, CosmeticRarity.Common, 0, Hex("3C4A3E"), Hex("1E261F"), 0);
            Add("helmet_ops", "Ops Helmet", CosmeticSlot.Headgear, CosmeticRarity.Rare, 0, Hex("23272E"), Hex("FF8A1F"), 1);
            Add("beanie_ash", "Ash Beanie", CosmeticSlot.Headgear, CosmeticRarity.Common, 400, Hex("6E6E78"), Hex("45454D"), 2);
            Add("helmet_gold", "Gilded Helm", CosmeticSlot.Headgear, CosmeticRarity.Legendary, 4800, Hex("E2B34A"), Hex("7A5A12"), 1);

            Add("pack_scout", "Scout Pack", CosmeticSlot.Backpack, CosmeticRarity.Common, 0, Hex("445060"), Hex("222A33"), 0);
            Add("pack_tactical", "Tactical Rig", CosmeticSlot.Backpack, CosmeticRarity.Rare, 1100, Hex("26302A"), Hex("6FBF73"), 1);

            // Weapon skins: Primary = body tint, Secondary = accent.
            Add("wskin_default", "Standard Issue", CosmeticSlot.WeaponSkin, CosmeticRarity.Common, 0, Hex("2A2F36"), Hex("6B7480"), 0);
            Add("wskin_arctic", "Arctic Wrap", CosmeticSlot.WeaponSkin, CosmeticRarity.Common, 0, Hex("D9E2EA"), Hex("6FA8D6"), 0);
            Add("wskin_ember", "Ember Camo", CosmeticSlot.WeaponSkin, CosmeticRarity.Rare, 1200, Hex("7A2E12"), Hex("FF8A1F"), 0);
            Add("wskin_gold", "Gilded Barrel", CosmeticSlot.WeaponSkin, CosmeticRarity.Legendary, 6000, Hex("E2B34A"), Hex("7A5A12"), 0);

            // Emotes: Variant selects the placeholder animation (0 wave, 1 flex, 2 shuffle, 3 salute).
            Add("emote_wave", "Wave", CosmeticSlot.Emote, CosmeticRarity.Common, 0, Hex("5CFFC4"), Hex("2A1F5C"), 0);
            Add("emote_flex", "Flex", CosmeticSlot.Emote, CosmeticRarity.Common, 0, Hex("FF8A1F"), Hex("2A1F5C"), 1);
            Add("emote_dance", "Shuffle", CosmeticSlot.Emote, CosmeticRarity.Rare, 900, Hex("2EE6FF"), Hex("14171F"), 2);
            Add("emote_salute", "Salute", CosmeticSlot.Emote, CosmeticRarity.Epic, 1500, Hex("F2C14E"), Hex("14171F"), 3);
        }

        /// <summary>Ids granted to a brand-new local profile.</summary>
        public static readonly string[] StartingOwnedIds =
        {
            "outfit_recruit", "outfit_urban", "outfit_nightops",
            "shoes_basic", "shoes_runner",
            "mask_bandana", "mask_visor",
            "cap_standard", "helmet_ops",
            "pack_scout",
            "wskin_default", "wskin_arctic",
            "emote_wave", "emote_flex"
        };

        public static Loadout StartingLoadout()
        {
            return new Loadout
            {
                outfit = "outfit_urban",
                shoes = "shoes_basic",
                mask = "",
                headgear = "cap_standard",
                backpack = "",
                weaponSkin = "wskin_default",
                emote = "emote_wave"
            };
        }

        public IReadOnlyList<CosmeticItem> Items => items;

        public bool TryGet(string id, out CosmeticItem item)
        {
            if (string.IsNullOrEmpty(id))
            {
                item = null;
                return false;
            }
            return byId.TryGetValue(id, out item);
        }

        public IEnumerable<CosmeticItem> InSlot(CosmeticSlot slot)
        {
            for (int i = 0; i < items.Count; i++)
                if (items[i].Slot == slot) yield return items[i];
        }

        private void Add(string id, string name, CosmeticSlot slot, CosmeticRarity rarity, int price,
            Color primary, Color secondary, int variant)
        {
            var item = new CosmeticItem
            {
                Id = id, DisplayName = name, Slot = slot, Rarity = rarity, PriceCredits = price,
                PrimaryColor = primary, SecondaryColor = secondary, Variant = variant
            };
            items.Add(item);
            byId[id] = item;
        }

        private static Color Hex(string rgb)
        {
            int v = Convert.ToInt32(rgb, 16);
            return new Color(((v >> 16) & 255) / 255f, ((v >> 8) & 255) / 255f, (v & 255) / 255f, 1f);
        }
    }
}
