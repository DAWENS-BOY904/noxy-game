using System;
using System.Collections.Generic;
using Noxy.Core;
using UnityEngine;
using UnityEngine.UI;

namespace Noxy.Lobby
{
    internal static class EconomyUi
    {
        public static readonly Color CoinColor = new Color(1f, 0.78f, 0.26f);
        public static readonly Color DiamondColor = UITheme.Cyan;

        public static string FormatTime(long ms)
        {
            try { return DateTimeOffset.FromUnixTimeMilliseconds(ms).LocalDateTime.ToString("yyyy-MM-dd HH:mm"); }
            catch (Exception) { return ""; }
        }

        public static string Signed(TransactionDto t)
        {
            string unit = t.currency == "diamonds" ? "diamonds" : "coins";
            return (t.amount > 0 ? "+" : "") + t.amount.ToString("N0") + " " + unit;
        }

        public static CosmeticRarity ParseRarity(string value) =>
            Enum.TryParse(value, true, out CosmeticRarity r) ? r : CosmeticRarity.Common;

        public static RowData Notice(string title, string subtitle, Color color) =>
            new RowData { Title = title, Subtitle = subtitle, TitleColor = color };

        public static RowData BalanceRow(IEconomyService economy)
        {
            var w = economy.Wallet;
            return new RowData
            {
                Title = "Your balance",
                Subtitle = w.coins.ToString("N0") + " coins   |   " + w.diamonds.ToString("N0") + " diamonds",
                TitleColor = UITheme.Accent
            };
        }
    }

    public sealed class MissionsScreen : InfoListScreen
    {
        public override LobbyScreenId Id => LobbyScreenId.Missions;
        public override string Title => "Missions";
        protected override string Banner => "Progress and rewards are tracked by the server. Free coins have a daily limit and each reward can be claimed once per period.";

        private MissionsDto data;
        private string error;
        private bool busy;

        protected override void OnShown()
        {
            base.OnShown();
            Load();
        }

        private void Load()
        {
            error = null;
            if (!AppServices.Economy.IsAvailable) { Rebuild(); return; }
            AppServices.Economy.LoadMissions(r =>
            {
                if (r.Ok) data = r.Value; else error = r.Friendly;
                Rebuild();
            });
        }

        protected override IEnumerable<RowData> GetRows()
        {
            var economy = AppServices.Economy;
            if (!economy.IsAvailable) { yield return EconomyUi.Notice("Sign in required", economy.UnavailableReason, UITheme.Warning); yield break; }
            if (error != null) { yield return EconomyUi.Notice("Couldn't load missions", error, UITheme.Danger); yield break; }
            if (data == null) { yield return EconomyUi.Notice("Loading...", "", UITheme.TextDim); yield break; }

            yield return new RowData
            {
                Title = "Free coins today",
                Subtitle = data.coinsEarnedToday.ToString("N0") + " / " + data.coinDailyCap.ToString("N0") + " from matches, training and ads",
                Progress = data.coinDailyCap > 0 ? Mathf.Clamp01((float)data.coinsEarnedToday / data.coinDailyCap) : 0f,
                TitleColor = EconomyUi.CoinColor
            };

            foreach (var m in data.missions)
            {
                var mission = m;
                yield return new RowData
                {
                    Title = mission.title,
                    Subtitle = Capitalize(mission.kind) + "  -  " + mission.progress + " / " + mission.target + "  -  +" + mission.rewardCoins + " coins",
                    Progress = mission.target > 0 ? (float)mission.progress / mission.target : 0f,
                    ActionLabel = mission.claimed ? "CLAIMED" : (mission.claimable ? "CLAIM" : "IN PROGRESS"),
                    ActionEnabled = mission.claimable && !busy,
                    Action = () => Claim(mission)
                };
            }
        }

        private static string Capitalize(string s) => string.IsNullOrEmpty(s) ? s : char.ToUpperInvariant(s[0]) + s.Substring(1);

        private void Claim(MissionDto mission)
        {
            if (busy) return;
            busy = true;
            AppServices.Economy.ClaimMission(mission.id, r =>
            {
                busy = false;
                if (r.Ok) AppServices.Notifications.Post("Reward claimed", "+" + r.Value.coins + " coins", NotificationSeverity.Success);
                else AppServices.Notifications.Post("Can't claim", EconomyText.FriendlyPaymentError(r.Code, r.Friendly), NotificationSeverity.Warning);
                Load();
            });
        }
    }

    public sealed class ShopScreen : InfoListScreen
    {
        private enum Tab { Diamonds, Cosmetics, History }

        public override LobbyScreenId Id => LobbyScreenId.Shop;
        public override string Title => "Shop";
        protected override string Banner => "Balances and prices live on the server. Diamonds are only added after the server verifies a payment.";
        protected override bool HasFooter => true;

        private Tab tab = Tab.Diamonds;
        private PackagesDto packages;
        private CosmeticOffersDto cosmetics;
        private PurchasesDto purchases;
        private TransactionsDto transactions;
        private string error;
        private bool busy;
        private readonly Dictionary<Tab, ButtonView> tabButtons = new Dictionary<Tab, ButtonView>();

        protected override void BuildFooter(RectTransform footer)
        {
            UIKit.HStack(footer.gameObject, 8f);
            AddTab(footer, Tab.Diamonds, "DIAMONDS");
            AddTab(footer, Tab.Cosmetics, "COSMETICS");
            AddTab(footer, Tab.History, "HISTORY");
        }

        private void AddTab(RectTransform footer, Tab t, string label)
        {
            var b = UIKit.NewButton(footer, "Tab" + t, label, UITheme.Button, () => { tab = t; Load(); }, UITheme.FontBody);
            UIKit.SetLayout(b.gameObject, flexibleWidth: 1f, preferredHeight: 64f);
            tabButtons[t] = b;
        }

        protected override void OnShown()
        {
            base.OnShown();
            Load();
        }

        private void Load()
        {
            foreach (var kv in tabButtons) kv.Value.SetColor(kv.Key == tab ? UITheme.Accent : UITheme.Button);
            error = null;
            var economy = AppServices.Economy;
            if (!economy.IsAvailable) { Rebuild(); return; }
            economy.RefreshWallet(_ => Rebuild());
            switch (tab)
            {
                case Tab.Diamonds: economy.LoadPackages(r => { if (r.Ok) packages = r.Value; else error = r.Friendly; Rebuild(); }); break;
                case Tab.Cosmetics: economy.LoadCosmetics(r => { if (r.Ok) cosmetics = r.Value; else error = r.Friendly; Rebuild(); }); break;
                default:
                    economy.LoadPurchases(r => { if (r.Ok) purchases = r.Value; else error = r.Friendly; Rebuild(); });
                    economy.LoadTransactions(r => { if (r.Ok) transactions = r.Value; else error = r.Friendly; Rebuild(); });
                    break;
            }
        }

        protected override IEnumerable<RowData> GetRows()
        {
            var economy = AppServices.Economy;
            if (!economy.IsAvailable) { yield return EconomyUi.Notice("Sign in required", economy.UnavailableReason, UITheme.Warning); yield break; }
            yield return EconomyUi.BalanceRow(economy);
            if (error != null) { yield return EconomyUi.Notice("Couldn't load this tab", error, UITheme.Danger); yield break; }

            switch (tab)
            {
                case Tab.Diamonds: foreach (var r in DiamondRows()) yield return r; break;
                case Tab.Cosmetics: foreach (var r in CosmeticRows()) yield return r; break;
                default: foreach (var r in HistoryRows()) yield return r; break;
            }
        }

        // ---------- diamonds ----------

        private IEnumerable<RowData> DiamondRows()
        {
            if (packages == null) { yield return EconomyUi.Notice("Loading...", "", UITheme.TextDim); yield break; }
            bool test = packages.testPayments;
            if (busy) yield return EconomyUi.Notice("Processing payment...", "Waiting for the server to confirm. Do not close the game.", UITheme.Warning);
            if (!test)
                yield return EconomyUi.Notice("Real-money purchases are not available yet",
                    "No payment provider is configured on this server. Apple / Google billing needs store credentials before diamonds can be sold.", UITheme.Warning);
            else
                yield return EconomyUi.Notice("TEST MODE", "Sandbox purchases only: no real money is charged and the diamonds are for testing.", UITheme.Warning);

            foreach (var p in packages.packages)
            {
                var package = p;
                string bonus = package.bonusDiamonds > 0 ? "  (+" + package.bonusDiamonds + " bonus)" : "";
                yield return new RowData
                {
                    Title = package.TotalDiamonds.ToString("N0") + " Diamonds" + bonus,
                    Subtitle = EconomyText.FormatPrice(package.referencePriceMinor, package.referenceCurrency) + " reference. " + EconomyText.PricePlaceholder + ".",
                    TitleColor = EconomyUi.DiamondColor,
                    ActionLabel = test ? "TEST BUY" : "UNAVAILABLE",
                    ActionEnabled = test && !busy,
                    Action = () => ConfirmTestPurchase(package, "success")
                };
            }

            if (test && packages.packages.Count > 0)
            {
                var first = packages.packages[0];
                yield return new RowData { Title = "Test: simulate a cancelled payment", Subtitle = "Creates a pending payment, then cancels it.", ActionLabel = "RUN", ActionEnabled = !busy, Action = () => RunTestPurchase(first, "cancel") };
                yield return new RowData { Title = "Test: simulate a failed payment", Subtitle = "The server marks it failed and credits nothing.", ActionLabel = "RUN", ActionEnabled = !busy, Action = () => RunTestPurchase(first, "fail") };
            }
        }

        private void ConfirmTestPurchase(DiamondPackageDto package, string outcome)
        {
            Ctx.Confirm("Test purchase", "SANDBOX: add " + package.TotalDiamonds.ToString("N0") + " test diamonds? No real money is used.", "CONFIRM", () => RunTestPurchase(package, outcome));
        }

        private void RunTestPurchase(DiamondPackageDto package, string outcome)
        {
            if (busy) return;
            busy = true;
            Rebuild();
            var economy = AppServices.Economy;
            economy.BeginPurchase(package.id, "test", begin =>
            {
                if (!begin.Ok) { Finish(begin.Code, begin.Friendly, null); return; }
                economy.ConfirmTestPurchase(begin.Value.payment.id, outcome, done =>
                {
                    if (!done.Ok) { Finish(done.Code, done.Friendly, null); return; }
                    Finish("", "", done.Value.payment);
                });
            });
        }

        private void Finish(string code, string message, PaymentDto payment)
        {
            busy = false;
            var notes = AppServices.Notifications;
            if (payment == null) notes.Post("Payment problem", EconomyText.FriendlyPaymentError(code, message), NotificationSeverity.Error);
            else if (payment.status == "succeeded") notes.Post("Payment complete", "+" + payment.diamonds.ToString("N0") + " diamonds (test).", NotificationSeverity.Success);
            else if (payment.status == "cancelled") notes.Post("Payment cancelled", "You were not charged.", NotificationSeverity.Warning);
            else notes.Post("Payment failed", string.IsNullOrEmpty(payment.error) ? "You were not charged." : payment.error, NotificationSeverity.Error);
            Load();
        }

        // ---------- cosmetics ----------

        private IEnumerable<RowData> CosmeticRows()
        {
            if (cosmetics == null) { yield return EconomyUi.Notice("Loading...", "", UITheme.TextDim); yield break; }
            var wallet = AppServices.Economy.Wallet;
            int shown = 0;
            foreach (var o in cosmetics.items)
            {
                if (o.owned) continue;
                var offer = o;
                Color color = UITheme.Rarity(EconomyUi.ParseRarity(offer.rarity));
                if (offer.coinPrice > 0)
                {
                    shown++;
                    bool afford = wallet.coins >= offer.coinPrice;
                    yield return new RowData
                    {
                        Title = offer.name, TitleColor = color,
                        Subtitle = Cap(offer.rarity) + " " + offer.slot + "  -  " + offer.coinPrice.ToString("N0") + " coins" + (afford ? "" : "  (not enough coins)"),
                        ActionLabel = offer.coinPrice.ToString("N0") + " C", ActionEnabled = afford && !busy,
                        Action = () => ConfirmBuy(offer, "coins", offer.coinPrice)
                    };
                }
                if (offer.diamondPrice > 0)
                {
                    shown++;
                    bool afford = wallet.diamonds >= offer.diamondPrice;
                    yield return new RowData
                    {
                        Title = offer.name, TitleColor = color,
                        Subtitle = Cap(offer.rarity) + " " + offer.slot + "  -  " + offer.diamondPrice.ToString("N0") + " diamonds" + (afford ? "" : "  (not enough diamonds)"),
                        ActionLabel = offer.diamondPrice.ToString("N0") + " D", ActionEnabled = afford && !busy,
                        Action = () => ConfirmBuy(offer, "diamonds", offer.diamondPrice)
                    };
                }
            }
            if (shown == 0) yield return EconomyUi.Notice("Nothing left to buy", "You own every item currently on sale.", UITheme.TextDim);
        }

        private static string Cap(string s) => string.IsNullOrEmpty(s) ? s : char.ToUpperInvariant(s[0]) + s.Substring(1);

        private void ConfirmBuy(CosmeticOfferDto offer, string currency, int price)
        {
            Ctx.Confirm("Buy " + offer.name, "Spend " + price.ToString("N0") + " " + currency + "? The server checks the price and your balance.", "BUY", () => Buy(offer, currency));
        }

        private void Buy(CosmeticOfferDto offer, string currency)
        {
            if (busy) return;
            busy = true;
            Rebuild();
            AppServices.Economy.BuyCosmetic(offer.itemId, currency, r =>
            {
                busy = false;
                if (r.Ok) AppServices.Notifications.Post("Purchased", offer.name + " added to your inventory.", NotificationSeverity.Success);
                else AppServices.Notifications.Post("Purchase failed", EconomyText.FriendlyPaymentError(r.Code, r.Friendly), NotificationSeverity.Error);
                Load();
            });
        }

        // ---------- history ----------

        private IEnumerable<RowData> HistoryRows()
        {
            if (purchases == null || transactions == null) { yield return EconomyUi.Notice("Loading...", "", UITheme.TextDim); yield break; }
            foreach (var p in purchases.purchases)
            {
                Color c = p.status == "succeeded" ? UITheme.Success : (p.status == "pending" ? UITheme.Warning : UITheme.TextDim);
                yield return new RowData
                {
                    Title = p.diamonds.ToString("N0") + " diamond top-up (" + p.provider + ")",
                    Subtitle = EconomyUi.FormatTime(p.createdAt) + "  -  " + p.id + (string.IsNullOrEmpty(p.error) ? "" : "  -  " + p.error),
                    RightText = EconomyText.StatusLabel(p.status), RightColor = c
                };
            }
            foreach (var t in transactions.transactions)
            {
                yield return new RowData
                {
                    Title = EconomyText.Describe(t.kind),
                    Subtitle = EconomyUi.FormatTime(t.createdAt) + "  -  " + t.txnId,
                    RightText = EconomyUi.Signed(t), RightColor = t.amount > 0 ? UITheme.Success : UITheme.Warning
                };
            }
            if (purchases.purchases.Count == 0 && transactions.transactions.Count == 0)
                yield return EconomyUi.Notice("No transactions yet", "Rewards, purchases and spending will show up here.", UITheme.TextDim);
        }
    }

    /// <summary>
    /// Post-match results: reward breakdown and, for a confirmed win, the Booyah bonus. Shows only what the server
    /// recorded; a "sample" preview is clearly labelled and never touches a balance.
    /// </summary>
    public sealed class MatchResultsScreen : LobbyScreen
    {
        public override LobbyScreenId Id => LobbyScreenId.MatchResults;
        public override string Title => "Match results";

        private RectTransform content;
        private MatchRewardDto data;
        private bool sample;
        private Text bonusText, titleText;
        private float animStart;
        private int bonusTarget;

        public void Present(MatchRewardDto dto, bool isSample)
        {
            data = dto;
            sample = isSample;
            Ctx.Screens.Open(LobbyScreenId.MatchResults);
            Build();
        }

        protected override void BuildContent(RectTransform parent)
        {
            var scroll = UIKit.NewScroll(parent, "Results", out content);
            UIKit.Stretch((RectTransform)scroll.transform);
            UIKit.VStack(content.gameObject, 12f);
        }

        protected override void OnShown() => Build();

        private void Build()
        {
            if (content == null) return;
            UIKit.DestroyChildren(content);
            bonusText = null; titleText = null;
            if (data == null) { UIWidgets.Paragraph(content, "No match selected.", UITheme.TextDim); return; }

            if (sample)
                UIWidgets.Paragraph(content, "SAMPLE PREVIEW - no match was played and nothing is added to your balance.", UITheme.Warning);

            titleText = UIKit.NewText(content, data.booyah ? "BOOYAH!" : "#" + data.placement + " PLACE", UITheme.FontHuge, data.booyah ? UITheme.Accent : UITheme.Text, TextAnchor.MiddleCenter, FontStyle.Bold, "Headline");
            UIKit.SetLayout(titleText.gameObject, preferredHeight: 110f);
            UIKit.FitText(titleText, 40);

            UIWidgets.Paragraph(content, data.kills + " elimination" + (data.kills == 1 ? "" : "s") + "   |   match " + data.matchId, UITheme.TextDim);

            UIWidgets.SectionTitle(content, "REWARD BREAKDOWN");
            if (data.breakdown.Count == 0) UIWidgets.Paragraph(content, "No coin rewards for this match.", UITheme.TextDim);
            foreach (var line in data.breakdown)
                UIWidgets.KeyValueRow(content, line.label, (line.coins >= 0 ? "+" : "") + line.coins.ToString("N0") + " coins", line.coins >= 0 ? EconomyUi.CoinColor : UITheme.Warning);
            UIWidgets.KeyValueRow(content, "Total coins", "+" + data.coins.ToString("N0"), EconomyUi.CoinColor);

            if (data.status == "granted" && data.booyahDiamonds > 0)
            {
                bonusTarget = data.booyahDiamonds;
                bonusText = UIKit.NewText(content, "+0 DIAMONDS", UITheme.FontTitle, EconomyUi.DiamondColor, TextAnchor.MiddleCenter, FontStyle.Bold, "Bonus");
                UIKit.SetLayout(bonusText.gameObject, preferredHeight: 70f);
                UIWidgets.Paragraph(content, "Booyah bonus - in-game promotional reward", UITheme.Text);
                animStart = Time.unscaledTime;
            }
            else if (data.status == "held")
                UIWidgets.Paragraph(content, "Booyah bonus held for review. " + data.reason, UITheme.Warning);
            else if (data.status == "denied" && !string.IsNullOrEmpty(data.reason))
                UIWidgets.Paragraph(content, "No Booyah bonus: " + data.reason, UITheme.TextDim);

            if (!sample)
            {
                var w = AppServices.Economy.Wallet;
                UIWidgets.SectionTitle(content, "BALANCE");
                UIWidgets.KeyValueRow(content, "Coins", w.coins.ToString("N0"), EconomyUi.CoinColor);
                UIWidgets.KeyValueRow(content, "Diamonds", w.diamonds.ToString("N0"), EconomyUi.DiamondColor);
            }

            UIWidgets.Paragraph(content, string.IsNullOrEmpty(data.notice) ? EconomyText.PromoDisclaimer : data.notice, UITheme.TextDim);
        }

        private void Update()
        {
            if (bonusText == null) return;
            float t = Mathf.Clamp01((Time.unscaledTime - animStart) / 1.4f);
            float eased = 1f - (1f - t) * (1f - t);
            bonusText.text = "+" + Mathf.RoundToInt(bonusTarget * eased).ToString("N0") + " DIAMONDS";
            if (titleText != null)
            {
                float pulse = 1f + 0.05f * Mathf.Sin((Time.unscaledTime - animStart) * 4f) * (1f - t);
                titleText.rectTransform.localScale = new Vector3(pulse, pulse, 1f);
            }
        }

        public static MatchRewardDto SampleBooyah() => new MatchRewardDto
        {
            matchId = "sample", placement = 1, kills = 4, coins = 205, booyahDiamonds = 500, booyah = true, status = "granted",
            notice = EconomyText.PromoDisclaimer,
            breakdown = new List<RewardLineDto>
            {
                new RewardLineDto { label = "Match played", coins = 20 },
                new RewardLineDto { label = "4 eliminations", coins = 20 },
                new RewardLineDto { label = "Victory", coins = 100 },
                new RewardLineDto { label = "Sample bonus", coins = 65 },
            }
        };
    }
}
