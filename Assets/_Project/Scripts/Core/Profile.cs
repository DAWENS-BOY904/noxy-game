using System;
using System.Collections.Generic;

namespace Noxy.Core
{
    [Serializable]
    public sealed class PlayerStats
    {
        public int matchesPlayed;
        public int wins;
        public int topThree;
        public int kills;
        public int deaths;
        public int headshots;
        public float hoursPlayed;

        public float WinRate => matchesPlayed == 0 ? 0f : (float)wins / matchesPlayed;
        public float KillDeathRatio => deaths == 0 ? kills : (float)kills / deaths;
        public float HeadshotRate => kills == 0 ? 0f : (float)headshots / kills;
    }

    /// <summary>Equipped cosmetic ids per slot. Empty string means nothing equipped. JsonUtility-friendly.</summary>
    [Serializable]
    public sealed class Loadout
    {
        public string outfit = "";
        public string shoes = "";
        public string mask = "";
        public string headgear = "";
        public string backpack = "";
        public string weaponSkin = "";
        public string emote = "";

        public string Get(CosmeticSlot slot)
        {
            switch (slot)
            {
                case CosmeticSlot.Outfit: return outfit ?? "";
                case CosmeticSlot.Shoes: return shoes ?? "";
                case CosmeticSlot.Mask: return mask ?? "";
                case CosmeticSlot.Headgear: return headgear ?? "";
                case CosmeticSlot.Backpack: return backpack ?? "";
                case CosmeticSlot.WeaponSkin: return weaponSkin ?? "";
                case CosmeticSlot.Emote: return emote ?? "";
                default: return "";
            }
        }

        public void Set(CosmeticSlot slot, string id)
        {
            id = id ?? "";
            switch (slot)
            {
                case CosmeticSlot.Outfit: outfit = id; break;
                case CosmeticSlot.Shoes: shoes = id; break;
                case CosmeticSlot.Mask: mask = id; break;
                case CosmeticSlot.Headgear: headgear = id; break;
                case CosmeticSlot.Backpack: backpack = id; break;
                case CosmeticSlot.WeaponSkin: weaponSkin = id; break;
                case CosmeticSlot.Emote: emote = id; break;
            }
        }

        public Loadout Clone()
        {
            return new Loadout { outfit = outfit, shoes = shoes, mask = mask, headgear = headgear, backpack = backpack, weaponSkin = weaponSkin, emote = emote };
        }

        public bool SameAs(Loadout other)
        {
            if (other == null) return false;
            foreach (CosmeticSlot slot in Enum.GetValues(typeof(CosmeticSlot)))
                if (Get(slot) != other.Get(slot)) return false;
            return true;
        }
    }

    [Serializable]
    public sealed class InventoryEntry
    {
        public string itemId;
        public string displayName;
        public int count;
    }

    [Serializable]
    public sealed class PlayerProfile
    {
        public const int CurrentVersion = 1;

        public int version = CurrentVersion;
        public string playerId = "";

        /// <summary>Permanent 10-digit ID allocated by the backend. Empty for the offline local profile.</summary>
        public string publicId = "";
        public string rank = "Unranked";
        public string username = "";
        public int level = 1;
        public int xp;
        public int credits;
        public int cores;
        public PlayerStats stats = new PlayerStats();
        public List<string> ownedCosmeticIds = new List<string>();
        public List<InventoryEntry> consumables = new List<InventoryEntry>();
        public Loadout loadout = new Loadout();
        public string selectedModeId = GameModeIds.Training;
        public string lastDailyClaimDate = "";

        public int XpToNextLevel => XpRequiredForLevel(level);

        public static int XpRequiredForLevel(int level)
        {
            return 500 + 150 * Math.Max(0, level - 1);
        }
    }
}
