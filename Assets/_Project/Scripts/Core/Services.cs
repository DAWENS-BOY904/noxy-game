using System;

namespace Noxy.Core
{
    public readonly struct ServiceResult
    {
        public readonly bool Success;
        public readonly string Error;

        private ServiceResult(bool success, string error)
        {
            Success = success;
            Error = error;
        }

        public static ServiceResult Ok => new ServiceResult(true, null);
        public static ServiceResult Fail(string error) => new ServiceResult(false, error);
    }

    public static class RewardRules
    {
        public const int DailyRewardCredits = 150;
    }

    /// <summary>
    /// Player data access. LocalProfileService is the offline mock implementation. While signed in to a
    /// backend, the same instance mirrors the server snapshot (see ApplyServerSnapshot) and the server stays
    /// authoritative for ownership, currencies and equipped items.
    /// </summary>
    public interface IProfileService
    {
        PlayerProfile Profile { get; }
        event Action ProfileChanged;

        /// <summary>Raised after a loadout change was accepted locally; the account layer syncs it to the server.</summary>
        event Action<Loadout> LoadoutEquipped;

        bool IsServerBound { get; }
        bool Owns(string cosmeticId);
        ServiceResult EquipLoadout(Loadout loadout);
        ServiceResult SetSelectedMode(string modeId);
        bool CanClaimDailyReward { get; }
        ServiceResult ClaimDailyReward();

        /// <summary>Replace identity, currencies, inventory, stats and loadout with server data. Stops local persistence.</summary>
        void ApplyServerSnapshot(PlayerProfile snapshot);

        /// <summary>Drop the server data and go back to the offline local profile.</summary>
        void RevertToLocal();
    }
}
