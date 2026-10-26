using System;
using System.Collections.Generic;
using Noxy.Core;
using UnityEngine;
using UnityEngine.UI;

namespace Noxy.Lobby
{
    public sealed class RowData
    {
        public string Title;
        public string Subtitle;
        public string RightText;
        public Color TitleColor = UITheme.Text;
        public Color RightColor = UITheme.TextDim;
        public float Progress = -1f;
        public string ActionLabel;
        public bool ActionEnabled;
        public Action Action;
    }

    /// <summary>Generic list screen. Subclasses only provide a banner (honest status note) and rows.</summary>
    public abstract class InfoListScreen : LobbyScreen
    {
        private RectTransform listContent;

        protected virtual string Banner => null;
        protected virtual bool HasFooter => false;
        protected abstract IEnumerable<RowData> GetRows();
        protected virtual void BuildFooter(RectTransform footer) { }

        protected override void BuildContent(RectTransform parent)
        {
            float top = 0f;
            if (!string.IsNullOrEmpty(Banner))
            {
                var banner = UIKit.NewRounded(parent, "Banner", new Color(0.35f, 0.25f, 0.05f, 0.85f));
                UIKit.Anchor(banner.rectTransform, new Vector2(0f, 1f), Vector2.one, new Vector2(0.5f, 1f), Vector2.zero, new Vector2(0f, 84f));
                var text = UIKit.NewText(banner.transform, Banner, UITheme.FontCaption, new Color(1f, 0.9f, 0.6f), TextAnchor.MiddleLeft, FontStyle.Normal, "Text");
                UIKit.Stretch(text.rectTransform, 16f, 4f, 16f, 4f);
                UIKit.FitText(text, 16);
                top = 96f;
            }

            float bottom = 0f;
            if (HasFooter)
            {
                var footer = UIKit.NewRect("Footer", parent);
                UIKit.Anchor(footer, new Vector2(0f, 0f), new Vector2(1f, 0f), new Vector2(0.5f, 0f), Vector2.zero, new Vector2(0f, 72f));
                BuildFooter(footer);
                bottom = 84f;
            }

            var scroll = UIKit.NewScroll(parent, "List", out listContent);
            UIKit.Stretch((RectTransform)scroll.transform, 0f, bottom, 0f, top);
            UIKit.VStack(listContent.gameObject, 10f);
        }

        protected override void OnShown() => Rebuild();

        protected void Rebuild()
        {
            if (listContent == null) return;
            UIKit.DestroyChildren(listContent);

            int count = 0;
            foreach (var row in GetRows())
            {
                BuildRow(row);
                count++;
            }

            if (count == 0)
            {
                var empty = UIKit.NewText(listContent, "Nothing here yet.", UITheme.FontBody, UITheme.TextDim, TextAnchor.MiddleCenter);
                UIKit.SetLayout(empty.gameObject, preferredHeight: 120f);
            }
        }

        private void BuildRow(RowData data)
        {
            bool hasButton = !string.IsNullOrEmpty(data.ActionLabel);
            var card = UIKit.NewRounded(listContent, "Row", UITheme.PanelLight);
            UIKit.SetLayout(card.gameObject, preferredHeight: data.Progress >= 0f ? 118f : 92f);

            float rightMargin = hasButton ? 232f : (string.IsNullOrEmpty(data.RightText) ? 20f : 170f);

            var title = UIKit.NewText(card.transform, data.Title, UITheme.FontButton, data.TitleColor, TextAnchor.MiddleLeft, FontStyle.Bold, "Title");
            UIKit.Anchor(title.rectTransform, new Vector2(0f, 1f), new Vector2(1f, 1f), new Vector2(0.5f, 1f), new Vector2(0f, -8f), new Vector2(0f, 38f));
            title.rectTransform.offsetMin = new Vector2(20f, title.rectTransform.offsetMin.y);
            title.rectTransform.offsetMax = new Vector2(-rightMargin, title.rectTransform.offsetMax.y);
            UIKit.FitText(title, 18);

            if (!string.IsNullOrEmpty(data.Subtitle))
            {
                var sub = UIKit.NewText(card.transform, data.Subtitle, UITheme.FontCaption, UITheme.TextDim, TextAnchor.MiddleLeft, FontStyle.Normal, "Subtitle");
                UIKit.Anchor(sub.rectTransform, new Vector2(0f, 1f), new Vector2(1f, 1f), new Vector2(0.5f, 1f), new Vector2(0f, -46f), new Vector2(0f, 32f));
                sub.rectTransform.offsetMin = new Vector2(20f, sub.rectTransform.offsetMin.y);
                sub.rectTransform.offsetMax = new Vector2(-rightMargin, sub.rectTransform.offsetMax.y);
                UIKit.FitText(sub, 16);
            }

            if (data.Progress >= 0f)
            {
                var track = UIKit.NewRounded(card.transform, "Progress", new Color(1f, 1f, 1f, 0.12f));
                UIKit.Anchor(track.rectTransform, Vector2.zero, new Vector2(1f, 0f), new Vector2(0.5f, 0f), new Vector2(0f, 14f), new Vector2(0f, 12f));
                track.rectTransform.offsetMin = new Vector2(20f, track.rectTransform.offsetMin.y);
                track.rectTransform.offsetMax = new Vector2(-rightMargin, track.rectTransform.offsetMax.y);
                var fill = UIKit.NewRounded(track.transform, "Fill", UITheme.Cyan);
                fill.rectTransform.anchorMin = Vector2.zero;
                fill.rectTransform.anchorMax = new Vector2(Mathf.Clamp01(data.Progress), 1f);
                fill.rectTransform.offsetMin = Vector2.zero;
                fill.rectTransform.offsetMax = Vector2.zero;
            }

            if (hasButton)
            {
                var action = data.Action;
                var button = UIKit.NewButton(card.transform, "Action", data.ActionLabel,
                    data.ActionEnabled ? UITheme.Accent : UITheme.Disabled, () => action?.Invoke(), UITheme.FontBody);
                UIKit.Anchor(button.Background.rectTransform, new Vector2(1f, 0.5f), new Vector2(1f, 0.5f), new Vector2(1f, 0.5f), new Vector2(-16f, 0f), new Vector2(200f, 64f));
                button.SetInteractable(data.ActionEnabled);
            }
            else if (!string.IsNullOrEmpty(data.RightText))
            {
                var right = UIKit.NewText(card.transform, data.RightText, UITheme.FontBody, data.RightColor, TextAnchor.MiddleRight, FontStyle.Bold, "Right");
                UIKit.Anchor(right.rectTransform, new Vector2(1f, 0f), Vector2.one, new Vector2(1f, 0.5f), new Vector2(-20f, 0f), new Vector2(150f, 0f));
                UIKit.FitText(right, 16);
            }
        }
    }

    public sealed class MissionsScreen : InfoListScreen
    {
        public override LobbyScreenId Id => LobbyScreenId.Missions;
        public override string Title => "Missions";
        protected override string Banner => "Sample missions (local mock). Progress is static until a mission backend exists.";

        protected override IEnumerable<RowData> GetRows()
        {
            yield return Mission("Eliminate 10 opponents", "Daily", 6, 10);
            yield return Mission("Play 3 matches", "Daily", 1, 3);
            yield return Mission("Land 5 headshots", "Daily", 5, 5);
            yield return Mission("Win a match", "Weekly", 0, 1);
            yield return Mission("Survive 20 minutes in total", "Weekly", 12, 20);
        }

        private static RowData Mission(string name, string kind, int done, int goal)
        {
            return new RowData
            {
                Title = name,
                Subtitle = kind + " - " + done + " / " + goal,
                Progress = (float)done / goal,
                RightText = done >= goal ? "DONE" : "",
                RightColor = UITheme.Success
            };
        }
    }

    public sealed class RewardsScreen : InfoListScreen
    {
        public override LobbyScreenId Id => LobbyScreenId.Rewards;
        public override string Title => "Rewards";
        protected override string Banner => "Local mock rewards, offline profile only (stored on this device, no server validation). While signed in the server owns your currency, so claiming is disabled.";

        protected override IEnumerable<RowData> GetRows()
        {
            bool canClaim = AppServices.Profile.CanClaimDailyReward;
            yield return new RowData
            {
                Title = "Daily login reward",
                Subtitle = "+" + RewardRules.DailyRewardCredits + " Credits",
                ActionLabel = canClaim ? "CLAIM" : "CLAIMED",
                ActionEnabled = canClaim,
                Action = Claim
            };
            yield return new RowData { Title = "Level 15 reward", Subtitle = "Neon Soles preview - not active", RightText = "LV 15", TitleColor = UITheme.TextDim };
            yield return new RowData { Title = "Level 20 reward", Subtitle = "Aurora Suit preview - not active", RightText = "LV 20", TitleColor = UITheme.TextDim };
        }

        private void Claim()
        {
            var result = AppServices.Profile.ClaimDailyReward();
            if (result.Success)
                AppServices.Notifications.Post("Reward claimed", "+" + RewardRules.DailyRewardCredits + " Credits added.", NotificationSeverity.Success);
            else
                AppServices.Notifications.Post("Can't claim", result.Error, NotificationSeverity.Warning);
            Rebuild();
        }
    }

    public sealed class ShopScreen : InfoListScreen
    {
        public override LobbyScreenId Id => LobbyScreenId.Shop;
        public override string Title => "Shop";
        protected override string Banner => "Store preview only. Purchasing is disabled: there is no store backend or payment service yet.";

        protected override IEnumerable<RowData> GetRows()
        {
            foreach (var item in AppServices.Catalog.Items)
            {
                if (AppServices.Profile.Owns(item.Id)) continue;
                yield return new RowData
                {
                    Title = item.DisplayName,
                    Subtitle = item.Rarity + " " + item.Slot.DisplayName() + " - " + item.PriceCredits.ToString("N0") + " Credits",
                    TitleColor = UITheme.Rarity(item.Rarity),
                    ActionLabel = "UNAVAILABLE",
                    ActionEnabled = false
                };
            }
        }
    }

    public sealed class NotificationsScreen : InfoListScreen
    {
        public override LobbyScreenId Id => LobbyScreenId.Notifications;
        public override string Title => "Notifications";
        protected override bool HasFooter => true;

        private bool opened;

        protected override IEnumerable<RowData> GetRows()
        {
            opened = true;
            foreach (var n in AppServices.Notifications.Messages)
            {
                yield return new RowData
                {
                    Title = (n.Read ? "" : "* ") + n.Title,
                    Subtitle = n.TimeUtc.ToLocalTime().ToString("HH:mm") + " - " + n.Body,
                    TitleColor = UITheme.Severity(n.Severity)
                };
            }
        }

        protected override void BuildFooter(RectTransform footer)
        {
            var clear = UIKit.NewButton(footer, "Clear", "CLEAR ALL", UITheme.Button, () => { AppServices.Notifications.Clear(); Rebuild(); }, UITheme.FontBody);
            UIKit.Anchor(clear.Background.rectTransform, new Vector2(1f, 0f), new Vector2(1f, 0f), new Vector2(1f, 0f), Vector2.zero, new Vector2(240f, 68f));
        }

        protected override void OnHidden()
        {
            if (!opened) return;
            opened = false;
            AppServices.Notifications.MarkAllRead();
        }
    }
}
