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

    public sealed class RewardsScreen : InfoListScreen
    {
        public override LobbyScreenId Id => LobbyScreenId.Rewards;
        public override string Title => "Rewards";
        protected override string Banner => "The daily login reward is a local mock for the offline profile only. Match rewards below come from the server and are only recorded for matches it confirmed.";

        private MatchRewardsDto recent;

        protected override void OnShown()
        {
            base.OnShown();
            if (!AppServices.Economy.IsAvailable) return;
            AppServices.Economy.LoadRecentMatches(r => { if (r.Ok) { recent = r.Value; Rebuild(); } });
        }

        protected override IEnumerable<RowData> GetRows()
        {
            bool canClaim = AppServices.Profile.CanClaimDailyReward;
            yield return new RowData
            {
                Title = "Daily login reward",
                Subtitle = "+" + RewardRules.DailyRewardCredits + " coins (offline profile)",
                ActionLabel = canClaim ? "CLAIM" : "CLAIMED",
                ActionEnabled = canClaim,
                Action = Claim
            };
            yield return new RowData
            {
                Title = "Preview the Booyah screen",
                Subtitle = "Sample data only. No match is played and no diamonds are added.",
                ActionLabel = "PREVIEW",
                ActionEnabled = true,
                Action = () => { if (Ctx.Screens.TryGet(LobbyScreenId.MatchResults, out var s) && s is MatchResultsScreen m) m.Present(MatchResultsScreen.SampleBooyah(), true); }
            };

            if (!AppServices.Economy.IsAvailable)
            {
                yield return new RowData { Title = "Match rewards", Subtitle = AppServices.Economy.UnavailableReason, TitleColor = UITheme.TextDim };
                yield break;
            }

            if (recent == null || recent.matches.Count == 0)
            {
                yield return new RowData { Title = "No rewarded matches yet", Subtitle = "Rewards appear here after the game server reports a finished match.", TitleColor = UITheme.TextDim };
                yield break;
            }

            foreach (var m in recent.matches)
            {
                var match = m;
                yield return new RowData
                {
                    Title = match.booyah ? "BOOYAH! +" + match.booyahDiamonds + " diamonds" : "Match finished #" + match.placement,
                    Subtitle = "+" + match.coins + " coins  -  " + match.kills + " eliminations",
                    TitleColor = match.booyah ? UITheme.Accent : UITheme.Text,
                    ActionLabel = "VIEW",
                    ActionEnabled = true,
                    Action = () => { if (Ctx.Screens.TryGet(LobbyScreenId.MatchResults, out var s) && s is MatchResultsScreen r) r.Present(match, false); }
                };
            }
        }

        private void Claim()
        {
            var result = AppServices.Profile.ClaimDailyReward();
            if (result.Success)
                AppServices.Notifications.Post("Reward claimed", "+" + RewardRules.DailyRewardCredits + " coins added.", NotificationSeverity.Success);
            else
                AppServices.Notifications.Post("Can't claim", result.Error, NotificationSeverity.Warning);
            Rebuild();
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
