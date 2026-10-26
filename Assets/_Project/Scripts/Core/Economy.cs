using System;
using System.Collections.Generic;
using UnityEngine;

namespace Noxy.Core
{
    // The client only displays what the server reports. Balances, prices, rewards and payment results are all
    // decided server-side; nothing in this file can change a balance on its own.

    [Serializable] public sealed class LifetimeDto { public int diamondsEarned, diamondsPurchased, diamondsSpent, coinsEarned, coinsSpent; }
    [Serializable] public sealed class WalletDto { public int coins, diamonds; public LifetimeDto lifetime = new LifetimeDto(); }

    [Serializable] public sealed class TransactionDto { public int id; public string txnId = "", currency = "", kind = "", refId = ""; public int amount, balanceAfter; public long createdAt; }
    [Serializable] public sealed class TransactionsDto { public List<TransactionDto> transactions = new List<TransactionDto>(); }

    [Serializable] public sealed class DiamondPackageDto
    {
        public string id = "", appleProductId = "", googleProductId = "", referenceCurrency = "USD", priceNote = "";
        public int diamonds, bonusDiamonds, referencePriceMinor;
        public int TotalDiamonds => diamonds + bonusDiamonds;
    }
    [Serializable] public sealed class PackagesDto { public List<DiamondPackageDto> packages = new List<DiamondPackageDto>(); public bool testPayments; public List<string> providers = new List<string>(); }

    [Serializable] public sealed class PaymentDto { public string id = "", packageId = "", provider = "", status = "", error = ""; public int diamonds; public long createdAt, updatedAt; }
    [Serializable] public sealed class PaymentEnvelopeDto { public PaymentDto payment = new PaymentDto(); public WalletDto wallet = new WalletDto(); public bool duplicate; }
    [Serializable] public sealed class PurchasesDto { public List<PaymentDto> purchases = new List<PaymentDto>(); }

    [Serializable] public sealed class MissionDto
    {
        public string id = "", title = "", kind = "";
        public int target, progress, rewardCoins;
        public bool claimed, claimable;
    }
    [Serializable] public sealed class MissionsDto { public List<MissionDto> missions = new List<MissionDto>(); public int coinsEarnedToday, coinDailyCap; }
    [Serializable] public sealed class ClaimDto { public string missionId = ""; public int coins; public bool limitReached; public WalletDto wallet = new WalletDto(); }

    [Serializable] public sealed class RewardLineDto { public string label = ""; public int coins; }
    [Serializable] public sealed class MatchRewardDto
    {
        public string matchId = "", status = "", reason = "", notice = "";
        public int placement, kills, coins, booyahDiamonds;
        public bool booyah;
        public List<RewardLineDto> breakdown = new List<RewardLineDto>();
        public WalletDto wallet = new WalletDto();
        public long createdAt;
    }
    [Serializable] public sealed class MatchRewardsDto { public List<MatchRewardDto> matches = new List<MatchRewardDto>(); }

    [Serializable] public sealed class PerformanceMatchDto
    {
        public string matchId = "", mode = "";
        public int playerCount, durationSeconds, placement, kills, deaths, assists, damageDealt, damageTaken, headshots, shotsFired, shotsHit, survivalSeconds, rewardsCoins, rewardsDiamonds, rankPointsDelta;
        public bool booyah;
        public float accuracy, headshotRate;
        public long startedAt, endedAt, createdAt;
    }
    [Serializable] public sealed class PerformanceBestMatchDto
    {
        public string matchId = "", mode = "";
        public int placement, kills, deaths, assists, damageDealt, headshots, survivalSeconds, rewardsCoins, rewardsDiamonds;
        public bool booyah;
        public float accuracy;
        public long createdAt;
    }
    [Serializable] public sealed class PerformanceSummaryDto
    {
        public int totalMatches, totalWins, totalKills, totalDeaths, headshots, totalBooyahs, totalAssists, rankPoints;
        public float winRate, kd, avgDamage, avgSurvivalSeconds, avgPlacement, headshotRate, accuracy;
        public PerformanceBestMatchDto bestMatch = new PerformanceBestMatchDto();
    }
    [Serializable] public sealed class PerformanceSummaryEnvelopeDto { public string mode = "", scope = ""; public PerformanceSummaryDto summary = new PerformanceSummaryDto(); }
    [Serializable] public sealed class PerformanceHistoryDto { public int total, offset, limit; public List<PerformanceMatchDto> matches = new List<PerformanceMatchDto>(); }
    [Serializable] public sealed class PerformanceTrendPointDto { public int index, kills, damageDealt, placement; public bool win; public float winRate; public long at; public string matchId = ""; public int survivalSeconds; }
    [Serializable] public sealed class PerformanceTrendsDto { public string mode = ""; public int window; public List<PerformanceTrendPointDto> trends = new List<PerformanceTrendPointDto>(); }

    [Serializable] public sealed class CosmeticOfferDto
    {
        public string itemId = "", slot = "", name = "", rarity = "";
        public int coinPrice, diamondPrice; // 0 = not sold for that currency
        public bool owned;
    }
    [Serializable] public sealed class CosmeticOffersDto { public List<CosmeticOfferDto> items = new List<CosmeticOfferDto>(); }
    [Serializable] public sealed class CosmeticPurchaseDto { public string itemId = "", currency = ""; public int price; public WalletDto wallet = new WalletDto(); }
    [Serializable] public sealed class AdsStatusDto { public bool enabled; public int rewardCoins, dailyCap, watchedToday; }

    public static class EconomyText
    {
        public const string PromoDisclaimer = "The Booyah bonus is an in-game promotional reward, subject to published eligibility rules. It has no cash value and cannot be withdrawn.";
        public const string PricePlaceholder = "Price shown by your store at checkout";

        public static string Describe(string kind)
        {
            switch (kind)
            {
                case "match": return "Match reward";
                case "booyah": return "Booyah reward";
                case "mission": return "Mission reward";
                case "training": return "Training reward";
                case "ad": return "Rewarded ad";
                case "purchase": return "Diamond top-up";
                case "refund": return "Purchase refunded";
                case "cosmetic": return "Cosmetic purchase";
                default: return "Adjustment";
            }
        }

        public static string StatusLabel(string status)
        {
            switch (status)
            {
                case "pending": return "Pending";
                case "succeeded": return "Completed";
                case "cancelled": return "Cancelled";
                case "failed": return "Failed";
                case "refunded": return "Refunded";
                default: return status ?? "";
            }
        }

        /// <summary>Reference price for display when no store price is available. Real mobile builds must show the store's price.</summary>
        public static string FormatPrice(int minorUnits, string currency)
        {
            string amount = (minorUnits / 100m).ToString("0.00", System.Globalization.CultureInfo.InvariantCulture);
            return (currency == "USD" ? "$" : currency + " ") + amount;
        }

        /// <summary>Maps the server's payment error codes to wording a player can act on.</summary>
        public static string FriendlyPaymentError(string code, string fallback)
        {
            switch (code)
            {
                case "network": return "No connection. You have not been charged; try again when you are back online.";
                case "test_payments_disabled": return "Test payments are turned off on this server.";
                case "provider_not_configured": return "This payment method is not available yet.";
                case "receipt_invalid":
                case "receipt_mismatch": return "The purchase could not be verified. If you were charged, contact support with your payment ID.";
                case "receipt_already_used": return "This purchase was already redeemed.";
                case "provider_unavailable": return "The payment provider could not be reached. Your purchase stays pending; try again shortly.";
                case "too_many_pending": return "You have several unfinished purchases. Finish or cancel them first.";
                case "rate_limited": return "Too many attempts. Please wait a moment.";
                case "insufficient_funds": return "You do not have enough currency for this.";
                case "already_owned": return "You already own this item.";
                default: return string.IsNullOrEmpty(fallback) ? "Something went wrong." : fallback;
            }
        }
    }

    public interface IEconomyService
    {
        bool IsAvailable { get; }
        string UnavailableReason { get; }
        WalletDto Wallet { get; }
        event Action Changed;

        void RefreshWallet(Action<ServiceResult> done = null);
        void LoadTransactions(Action<ApiResult<TransactionsDto>> done);
        void LoadPackages(Action<ApiResult<PackagesDto>> done);
        void BeginPurchase(string packageId, string provider, Action<ApiResult<PaymentEnvelopeDto>> done);
        void ConfirmTestPurchase(string paymentId, string outcome, Action<ApiResult<PaymentEnvelopeDto>> done);
        void LoadPurchases(Action<ApiResult<PurchasesDto>> done);
        void LoadMissions(Action<ApiResult<MissionsDto>> done);
        void ClaimMission(string missionId, Action<ApiResult<ClaimDto>> done);
        void ReportTrainingResult(int score, Action<ApiResult<ClaimDto>> done);
        void LoadRecentMatches(Action<ApiResult<MatchRewardsDto>> done);
        void LoadMatchRewards(string matchId, Action<ApiResult<MatchRewardDto>> done);
        void LoadPerformanceSummary(string mode, bool recentOnly, int limit, Action<ApiResult<PerformanceSummaryEnvelopeDto>> done);
        void LoadPerformanceHistory(string mode, int limit, int offset, Action<ApiResult<PerformanceHistoryDto>> done);
        void LoadPerformanceMatch(string matchId, Action<ApiResult<PerformanceMatchDto>> done);
        void LoadPerformanceTrends(string mode, int limit, Action<ApiResult<PerformanceTrendsDto>> done);
        void LoadCosmetics(Action<ApiResult<CosmeticOffersDto>> done);
        void BuyCosmetic(string itemId, string currency, Action<ApiResult<CosmeticPurchaseDto>> done);
        void LoadAdsStatus(Action<ApiResult<AdsStatusDto>> done);
    }

    public sealed class OfflineEconomyService : IEconomyService
    {
        public const string Reason = "Sign in to an account (Settings > Account) to use the store, missions and rewards.";
        public bool IsAvailable => false;
        public string UnavailableReason => Reason;
        public WalletDto Wallet { get; } = new WalletDto();
        public event Action Changed { add { } remove { } }

        private static void Fail<T>(Action<ApiResult<T>> done) where T : class =>
            done?.Invoke(new ApiResult<T> { Ok = false, Code = "offline", Message = Reason });

        public void RefreshWallet(Action<ServiceResult> done = null) => done?.Invoke(ServiceResult.Fail(Reason));
        public void LoadTransactions(Action<ApiResult<TransactionsDto>> done) => Fail(done);
        public void LoadPackages(Action<ApiResult<PackagesDto>> done) => Fail(done);
        public void BeginPurchase(string packageId, string provider, Action<ApiResult<PaymentEnvelopeDto>> done) => Fail(done);
        public void ConfirmTestPurchase(string paymentId, string outcome, Action<ApiResult<PaymentEnvelopeDto>> done) => Fail(done);
        public void LoadPurchases(Action<ApiResult<PurchasesDto>> done) => Fail(done);
        public void LoadMissions(Action<ApiResult<MissionsDto>> done) => Fail(done);
        public void ClaimMission(string missionId, Action<ApiResult<ClaimDto>> done) => Fail(done);
        public void ReportTrainingResult(int score, Action<ApiResult<ClaimDto>> done) => Fail(done);
        public void LoadRecentMatches(Action<ApiResult<MatchRewardsDto>> done) => Fail(done);
        public void LoadMatchRewards(string matchId, Action<ApiResult<MatchRewardDto>> done) => Fail(done);
        public void LoadPerformanceSummary(string mode, bool recentOnly, int limit, Action<ApiResult<PerformanceSummaryEnvelopeDto>> done) => Fail(done);
        public void LoadPerformanceHistory(string mode, int limit, int offset, Action<ApiResult<PerformanceHistoryDto>> done) => Fail(done);
        public void LoadPerformanceMatch(string matchId, Action<ApiResult<PerformanceMatchDto>> done) => Fail(done);
        public void LoadPerformanceTrends(string mode, int limit, Action<ApiResult<PerformanceTrendsDto>> done) => Fail(done);
        public void LoadCosmetics(Action<ApiResult<CosmeticOffersDto>> done) => Fail(done);
        public void BuyCosmetic(string itemId, string currency, Action<ApiResult<CosmeticPurchaseDto>> done) => Fail(done);
        public void LoadAdsStatus(Action<ApiResult<AdsStatusDto>> done) => Fail(done);
    }

    public sealed class RemoteEconomyService : IEconomyService
    {
        private readonly ApiClient api;

        public RemoteEconomyService(ApiClient api) { this.api = api; }

        /// <summary>Set by AccountService while a valid session exists.</summary>
        public bool SignedIn { get; set; }
        /// <summary>Invoked after the server changed a balance so the rest of the UI (lobby header) can re-sync.</summary>
        public Action BalanceChanged;

        public bool IsAvailable => SignedIn;
        public Func<string> UnavailableDetail;
        public string UnavailableReason => SignedIn ? "" : (UnavailableDetail?.Invoke() ?? OfflineEconomyService.Reason);
        public WalletDto Wallet { get; private set; } = new WalletDto();
        public event Action Changed;

        public void Clear() { Wallet = new WalletDto(); Changed?.Invoke(); }

        private void Call<T>(string method, string path, Dictionary<string, object> body, Action<ApiResult<T>> done, Func<T, WalletDto> walletOf = null) where T : class, new()
        {
            if (!SignedIn)
            {
                done?.Invoke(new ApiResult<T> { Ok = false, Code = "offline", Message = UnavailableReason });
                return;
            }
            api.Request<T>(method, path, body, r =>
            {
                if (r.Ok && walletOf != null)
                {
                    var w = walletOf(r.Value);
                    if (w != null) { Wallet = w; BalanceChanged?.Invoke(); Changed?.Invoke(); }
                }
                done?.Invoke(r);
            });
        }

        public void RefreshWallet(Action<ServiceResult> done = null)
        {
            Call<WalletDto>("GET", "/api/me/wallet", null, r =>
            {
                if (r.Ok) { Wallet = r.Value; Changed?.Invoke(); }
                done?.Invoke(r.Ok ? ServiceResult.Ok : ServiceResult.Fail(r.Friendly));
            });
        }

        public void LoadTransactions(Action<ApiResult<TransactionsDto>> done) => Call("GET", "/api/me/transactions?limit=50", null, done);
        public void LoadPackages(Action<ApiResult<PackagesDto>> done) => Call("GET", "/api/store/packages", null, done);

        public void BeginPurchase(string packageId, string provider, Action<ApiResult<PaymentEnvelopeDto>> done) =>
            Call("POST", "/api/store/purchases", new Dictionary<string, object> { ["packageId"] = packageId, ["provider"] = provider }, done);

        // Sandbox only: the server refuses this unless its operator enabled test payments.
        public void ConfirmTestPurchase(string paymentId, string outcome, Action<ApiResult<PaymentEnvelopeDto>> done) =>
            Call("POST", "/api/store/purchases/" + Uri.EscapeDataString(paymentId) + "/test-confirm", new Dictionary<string, object> { ["outcome"] = outcome }, done, e => e.wallet);

        public void LoadPurchases(Action<ApiResult<PurchasesDto>> done) => Call("GET", "/api/store/purchases", null, done);
        public void LoadMissions(Action<ApiResult<MissionsDto>> done) => Call("GET", "/api/me/missions", null, done);

        public void ClaimMission(string missionId, Action<ApiResult<ClaimDto>> done) =>
            Call("POST", "/api/me/missions/" + Uri.EscapeDataString(missionId) + "/claim", null, done, c => c.wallet);

        public void ReportTrainingResult(int score, Action<ApiResult<ClaimDto>> done) =>
            Call("POST", "/api/me/training-result", new Dictionary<string, object> { ["score"] = score }, done, c => c.wallet);

        public void LoadRecentMatches(Action<ApiResult<MatchRewardsDto>> done) => Call("GET", "/api/me/matches", null, done);

        public void LoadMatchRewards(string matchId, Action<ApiResult<MatchRewardDto>> done) =>
            Call("GET", "/api/me/matches/" + Uri.EscapeDataString(matchId) + "/rewards", null, done, m => m.wallet);

        public void LoadPerformanceSummary(string mode, bool recentOnly, int limit, Action<ApiResult<PerformanceSummaryEnvelopeDto>> done)
        {
            string path = "/api/me/performance/summary?scope=" + (recentOnly ? "recent" : "all") + "&limit=" + Math.Max(1, limit);
            if (!string.IsNullOrEmpty(mode)) path += "&mode=" + Uri.EscapeDataString(mode);
            Call("GET", path, null, done);
        }

        public void LoadPerformanceHistory(string mode, int limit, int offset, Action<ApiResult<PerformanceHistoryDto>> done)
        {
            string path = "/api/me/performance/history?limit=" + Math.Max(1, limit) + "&offset=" + Math.Max(0, offset);
            if (!string.IsNullOrEmpty(mode)) path += "&mode=" + Uri.EscapeDataString(mode);
            Call("GET", path, null, done);
        }

        public void LoadPerformanceMatch(string matchId, Action<ApiResult<PerformanceMatchDto>> done) =>
            Call("GET", "/api/me/performance/history/" + Uri.EscapeDataString(matchId), null, done);

        public void LoadPerformanceTrends(string mode, int limit, Action<ApiResult<PerformanceTrendsDto>> done)
        {
            string path = "/api/me/performance/trends?limit=" + Math.Max(1, limit);
            if (!string.IsNullOrEmpty(mode)) path += "&mode=" + Uri.EscapeDataString(mode);
            Call("GET", path, null, done);
        }

        public void LoadCosmetics(Action<ApiResult<CosmeticOffersDto>> done) => Call("GET", "/api/store/cosmetics", null, done);

        public void BuyCosmetic(string itemId, string currency, Action<ApiResult<CosmeticPurchaseDto>> done) =>
            Call("POST", "/api/shop/purchase", new Dictionary<string, object> { ["itemId"] = itemId, ["currency"] = currency }, done, c => c.wallet);

        public void LoadAdsStatus(Action<ApiResult<AdsStatusDto>> done) => Call("GET", "/api/ads/status", null, done);
    }
}
