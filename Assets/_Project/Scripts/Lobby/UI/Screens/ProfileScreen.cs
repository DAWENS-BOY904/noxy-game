using System;
using Noxy.Core;
using UnityEngine;
using UnityEngine.UI;

namespace Noxy.Lobby
{
    /// <summary>Player profile: identity, level/XP, statistics and equipped items. Reads from IProfileService.</summary>
    public sealed class ProfileScreen : LobbyScreen
    {
        private RectTransform content;

        public override LobbyScreenId Id => LobbyScreenId.Profile;
        public override ScreenStyle Style => ScreenStyle.SidePanel;
        public override string Title => "Character";

        protected override void BuildContent(RectTransform parent)
        {
            var scroll = UIKit.NewScroll(parent, "Scroll", out content);
            UIKit.Stretch((RectTransform)scroll.transform, 0f, 84f, 0f, 0f);
            UIKit.VStack(content.gameObject, 10f);

            var wardrobe = UIKit.NewButton(parent, "OpenWardrobe", "OPEN WARDROBE", UITheme.Accent, () => Ctx.Screens.Open(LobbyScreenId.Wardrobe));
            UIKit.Anchor(wardrobe.Background.rectTransform, new Vector2(0f, 0f), new Vector2(1f, 0f), new Vector2(0.5f, 0f), Vector2.zero, new Vector2(0f, 68f));

            AppServices.Profile.ProfileChanged += Refresh;
            Refresh();
        }

        private void OnDestroy()
        {
            if (AppServices.Profile != null) AppServices.Profile.ProfileChanged -= Refresh;
        }

        protected override void OnShown() => Refresh();

        private void Refresh()
        {
            if (content == null) return;
            UIKit.DestroyChildren(content);

            var p = AppServices.Profile.Profile;
            BuildIdentity(p);
            BuildXp(p);
            BuildStats(p.stats);
            BuildEquipped(p.loadout);
        }

        private void BuildIdentity(PlayerProfile p)
        {
            var card = UIKit.NewRounded(content, "Identity", UITheme.PanelLight);
            UIKit.SetLayout(card.gameObject, preferredHeight: 112f);

            var avatar = UIKit.NewImage(card.transform, "Avatar", UITheme.Accent, UISprites.Circle);
            UIKit.Anchor(avatar.rectTransform, new Vector2(0f, 0.5f), new Vector2(0f, 0.5f), new Vector2(0f, 0.5f), new Vector2(16f, 0f), new Vector2(80f, 80f));
            UIKit.NewText(avatar.transform, p.username.Substring(0, 1).ToUpperInvariant(), 42, new Color(0.1f, 0.06f, 0.02f), TextAnchor.MiddleCenter, FontStyle.Bold, "Initial");

            var name = UIKit.NewText(card.transform, p.username, UITheme.FontTitle, UITheme.Text, TextAnchor.MiddleLeft, FontStyle.Bold, "Name");
            UIKit.Anchor(name.rectTransform, new Vector2(0f, 1f), new Vector2(1f, 1f), new Vector2(0.5f, 1f), Vector2.zero, new Vector2(0f, 56f));
            name.rectTransform.offsetMin = new Vector2(110f, name.rectTransform.offsetMin.y);
            name.rectTransform.offsetMax = new Vector2(-12f, name.rectTransform.offsetMax.y);
            name.rectTransform.anchoredPosition = new Vector2(name.rectTransform.anchoredPosition.x, -12f);
            UIKit.FitText(name, 24);

            var id = UIKit.NewText(card.transform, "ID " + p.playerId + "  |  Local demo profile (offline)", UITheme.FontCaption, UITheme.TextDim, TextAnchor.MiddleLeft, FontStyle.Normal, "Id");
            UIKit.Anchor(id.rectTransform, new Vector2(0f, 0f), new Vector2(1f, 0f), new Vector2(0.5f, 0f), Vector2.zero, new Vector2(0f, 40f));
            id.rectTransform.offsetMin = new Vector2(110f, id.rectTransform.offsetMin.y);
            id.rectTransform.offsetMax = new Vector2(-12f, id.rectTransform.offsetMax.y);
            id.rectTransform.anchoredPosition = new Vector2(id.rectTransform.anchoredPosition.x, 12f);
            UIKit.FitText(id, 16);
        }

        private void BuildXp(PlayerProfile p)
        {
            UIWidgets.SectionTitle(content, "Level & XP");
            var row = UIKit.NewRect("XpRow", content);
            UIKit.VStack(row.gameObject, 8f);
            UIKit.SetLayout(row.gameObject, preferredHeight: 84f);

            var label = UIKit.NewText(row, "LEVEL " + p.level + "   -   " + p.xp.ToString("N0") + " / " + p.XpToNextLevel.ToString("N0") + " XP",
                UITheme.FontBody, UITheme.Text, TextAnchor.MiddleLeft, FontStyle.Bold, "XpLabel");
            UIKit.SetLayout(label.gameObject, preferredHeight: 40f);
            UIKit.FitText(label, 18);
            UIWidgets.ProgressBar(row, 18f, (float)p.xp / Math.Max(1, p.XpToNextLevel), UITheme.Cyan);
        }

        private void BuildStats(PlayerStats s)
        {
            UIWidgets.SectionTitle(content, "Statistics");
            var grid = UIWidgets.Grid(content, 150f, 96f, 10f, 4);
            UIWidgets.StatTile(grid, "Matches", s.matchesPlayed.ToString("N0"), UITheme.Text);
            UIWidgets.StatTile(grid, "Wins", s.wins.ToString("N0"), UITheme.Success);
            UIWidgets.StatTile(grid, "Win rate", (s.WinRate * 100f).ToString("0.#") + "%", UITheme.Success);
            UIWidgets.StatTile(grid, "Top 3", s.topThree.ToString("N0"), UITheme.Text);
            UIWidgets.StatTile(grid, "Kills", s.kills.ToString("N0"), UITheme.Accent);
            UIWidgets.StatTile(grid, "K/D", s.KillDeathRatio.ToString("0.00"), UITheme.Accent);
            UIWidgets.StatTile(grid, "Headshots", (s.HeadshotRate * 100f).ToString("0.#") + "%", UITheme.Cyan);
            UIWidgets.StatTile(grid, "Hours played", s.hoursPlayed.ToString("0.#"), UITheme.Text);
        }

        private void BuildEquipped(Loadout loadout)
        {
            UIWidgets.SectionTitle(content, "Equipped");
            foreach (CosmeticSlot slot in Enum.GetValues(typeof(CosmeticSlot)))
            {
                string id = loadout.Get(slot);
                if (AppServices.Catalog.TryGet(id, out var item))
                    UIWidgets.KeyValueRow(content, slot.DisplayName(), item.DisplayName, UITheme.Rarity(item.Rarity));
                else
                    UIWidgets.KeyValueRow(content, slot.DisplayName(), "None", UITheme.TextDim);
            }
        }
    }
}
