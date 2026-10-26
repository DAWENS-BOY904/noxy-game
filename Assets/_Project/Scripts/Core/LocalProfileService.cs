using System;
using System.IO;
using UnityEngine;

namespace Noxy.Core
{
    /// <summary>
    /// Local mock profile persisted as JSON. Offline only: no authentication, no server.
    /// Pass a null path for an in-memory profile (used by tests).
    /// </summary>
    public sealed class LocalProfileService : IProfileService
    {
        private readonly string filePath;
        private readonly ICosmeticCatalog catalog;
        private readonly Func<DateTime> utcNow;
        private PlayerProfile localProfile;

        public LocalProfileService(ICosmeticCatalog catalog, string filePath, Func<DateTime> utcNow = null)
        {
            this.catalog = catalog;
            this.filePath = filePath;
            this.utcNow = utcNow ?? (() => DateTime.UtcNow);
            Profile = TryLoad() ?? CreateDefaultProfile();
            Sanitize(Profile);
        }

        public PlayerProfile Profile { get; private set; }
        public bool IsServerBound { get; private set; }
        public event Action ProfileChanged;
        public event Action<Loadout> LoadoutEquipped;

        public bool CanClaimDailyReward => !IsServerBound && Profile.lastDailyClaimDate != Today();

        public bool Owns(string cosmeticId)
        {
            return !string.IsNullOrEmpty(cosmeticId) && Profile.ownedCosmeticIds.Contains(cosmeticId);
        }

        public void ApplyServerSnapshot(PlayerProfile snapshot)
        {
            if (snapshot == null) return;
            if (!IsServerBound) localProfile = Profile;
            snapshot.selectedModeId = Profile.selectedModeId;
            Profile = snapshot;
            IsServerBound = true;
            Sanitize(Profile);
            ProfileChanged?.Invoke();
        }

        public void RevertToLocal()
        {
            if (!IsServerBound) return;
            IsServerBound = false;
            Profile = localProfile ?? TryLoad() ?? CreateDefaultProfile();
            localProfile = null;
            Sanitize(Profile);
            ProfileChanged?.Invoke();
        }

        public ServiceResult EquipLoadout(Loadout loadout)
        {
            if (loadout == null) return ServiceResult.Fail("No loadout provided.");

            foreach (CosmeticSlot slot in Enum.GetValues(typeof(CosmeticSlot)))
            {
                string id = loadout.Get(slot);
                if (string.IsNullOrEmpty(id))
                {
                    if (!slot.IsOptional()) return ServiceResult.Fail($"{slot.DisplayName()} cannot be empty.");
                    continue;
                }
                if (!catalog.TryGet(id, out var item) || item.Slot != slot)
                    return ServiceResult.Fail($"Unknown {slot.DisplayName()} item '{id}'.");
                if (!Owns(id))
                    return ServiceResult.Fail($"You do not own {item.DisplayName}.");
            }

            Profile.loadout = loadout.Clone();
            SaveAndNotify();
            if (IsServerBound) LoadoutEquipped?.Invoke(Profile.loadout.Clone());
            return ServiceResult.Ok;
        }

        public ServiceResult SetSelectedMode(string modeId)
        {
            Profile.selectedModeId = modeId ?? "";
            Save();
            return ServiceResult.Ok;
        }

        public ServiceResult ClaimDailyReward()
        {
            if (IsServerBound) return ServiceResult.Fail("Daily rewards are not available online yet (the server grants currency).");
            if (!CanClaimDailyReward) return ServiceResult.Fail("Daily reward already claimed. Come back tomorrow.");
            Profile.lastDailyClaimDate = Today();
            Profile.credits += RewardRules.DailyRewardCredits;
            SaveAndNotify();
            return ServiceResult.Ok;
        }

        private string Today() => utcNow().ToString("yyyy-MM-dd");

        private PlayerProfile CreateDefaultProfile()
        {
            var profile = new PlayerProfile
            {
                playerId = Guid.NewGuid().ToString("N").Substring(0, 12),
                username = "Operator",
                level = 12,
                xp = 740,
                credits = 2450,
                cores = 120,
                selectedModeId = GameModeIds.Training,
                loadout = DefaultCosmeticCatalog.StartingLoadout(),
                stats = new PlayerStats
                {
                    matchesPlayed = 214, wins = 31, topThree = 78, kills = 902, deaths = 183,
                    headshots = 288, hoursPlayed = 37.5f
                }
            };
            profile.ownedCosmeticIds.AddRange(DefaultCosmeticCatalog.StartingOwnedIds);
            profile.consumables.Add(new InventoryEntry { itemId = "xp_card", displayName = "Double XP Card (1h)", count = 2 });
            profile.consumables.Add(new InventoryEntry { itemId = "rename_card", displayName = "Name Change Card", count = 1 });
            return profile;
        }

        private PlayerProfile TryLoad()
        {
            if (string.IsNullOrEmpty(filePath) || !File.Exists(filePath)) return null;
            try
            {
                var loaded = JsonUtility.FromJson<PlayerProfile>(File.ReadAllText(filePath));
                return loaded != null && !string.IsNullOrEmpty(loaded.playerId) ? loaded : null;
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[Profile] Could not read profile, creating a new one: {e.Message}");
                return null;
            }
        }

        /// <summary>Repairs hand-edited or older save data so the UI never sees null/invalid state.</summary>
        private void Sanitize(PlayerProfile p)
        {
            p.stats = p.stats ?? new PlayerStats();
            p.loadout = p.loadout ?? DefaultCosmeticCatalog.StartingLoadout();
            p.ownedCosmeticIds = p.ownedCosmeticIds ?? new System.Collections.Generic.List<string>();
            p.consumables = p.consumables ?? new System.Collections.Generic.List<InventoryEntry>();
            p.level = Math.Max(1, p.level);
            p.xp = Mathf.Clamp(p.xp, 0, p.XpToNextLevel - 1);
            if (string.IsNullOrWhiteSpace(p.username)) p.username = "Operator";

            foreach (CosmeticSlot slot in Enum.GetValues(typeof(CosmeticSlot)))
            {
                string id = p.loadout.Get(slot);
                bool valid = string.IsNullOrEmpty(id) || (catalog.TryGet(id, out var item) && item.Slot == slot && p.ownedCosmeticIds.Contains(id));
                if (!valid || (!slot.IsOptional() && string.IsNullOrEmpty(id)))
                    p.loadout.Set(slot, DefaultCosmeticCatalog.StartingLoadout().Get(slot));
            }
        }

        private void SaveAndNotify()
        {
            Save();
            ProfileChanged?.Invoke();
        }

        private void Save()
        {
            if (string.IsNullOrEmpty(filePath) || IsServerBound) return;
            try
            {
                string dir = Path.GetDirectoryName(filePath);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                File.WriteAllText(filePath, JsonUtility.ToJson(Profile, true));
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[Profile] Could not save profile: {e.Message}");
            }
        }
    }
}
