using System;
using Noxy.Core;
using UnityEngine;
using UnityEngine.UI;

namespace Noxy.Lobby
{
    /// <summary>Your own profile: identity, permanent Player ID, level/XP, statistics and equipped items.</summary>
    public sealed class ProfileScreen : LobbyScreen
    {
        private RectTransform content;
        private InputField nameInput;
        private Text nameStatus;

        public override LobbyScreenId Id => LobbyScreenId.Profile;
        public override ScreenStyle Style => ScreenStyle.SidePanel;
        public override string Title => "Profile";

        protected override void BuildContent(RectTransform parent)
        {
            var scroll = UIKit.NewScroll(parent, "Scroll", out content);
            UIKit.Stretch((RectTransform)scroll.transform, 0f, 84f, 0f, 0f);
            UIKit.VStack(content.gameObject, 10f);

            var vault = UIKit.NewButton(parent, "OpenVault", "OPEN VAULT", UITheme.Accent, () => Ctx.Screens.Open(LobbyScreenId.Vault));
            UIKit.Anchor(vault.Background.rectTransform, new Vector2(0f, 0f), new Vector2(1f, 0f), new Vector2(0.5f, 0f), Vector2.zero, new Vector2(0f, 68f));

            AppServices.Profile.ProfileChanged += Refresh;
            AppServices.Account.Changed += Refresh;
            Refresh();
        }

        private void OnDestroy()
        {
            if (AppServices.Profile != null) AppServices.Profile.ProfileChanged -= Refresh;
            if (AppServices.Account != null) AppServices.Account.Changed -= Refresh;
        }

        protected override void OnShown() => Refresh();

        private void Refresh()
        {
            if (content == null) return;
            UIKit.DestroyChildren(content);

            var p = AppServices.Profile.Profile;
            BuildIdentity(p);
            if (AppServices.Account.State == AccountState.SignedIn) BuildRename();
            BuildXp(p);
            BuildStats(p.stats);
            BuildEquipped(p.loadout);
        }

        private void BuildIdentity(PlayerProfile p)
        {
            var card = UIKit.NewRounded(content, "Identity", UITheme.PanelLight);
            UIKit.SetLayout(card.gameObject, preferredHeight: 150f);

            var avatar = UIKit.NewImage(card.transform, "Avatar", UITheme.Accent, UISprites.Circle);
            UIKit.Anchor(avatar.rectTransform, new Vector2(0f, 0.5f), new Vector2(0f, 0.5f), new Vector2(0f, 0.5f), new Vector2(16f, 0f), new Vector2(80f, 80f));
            UIKit.NewText(avatar.transform, p.username.Substring(0, 1).ToUpperInvariant(), 42, new Color(0.1f, 0.06f, 0.02f), TextAnchor.MiddleCenter, FontStyle.Bold, "Initial");

            var name = UIKit.NewText(card.transform, p.username, UITheme.FontTitle, UITheme.Text, TextAnchor.MiddleLeft, FontStyle.Bold, "Name");
            UIKit.Anchor(name.rectTransform, new Vector2(0f, 1f), new Vector2(1f, 1f), new Vector2(0.5f, 1f), new Vector2(0f, -10f), new Vector2(0f, 52f));
            name.rectTransform.offsetMin = new Vector2(110f, name.rectTransform.offsetMin.y);
            name.rectTransform.offsetMax = new Vector2(-12f, name.rectTransform.offsetMax.y);
            UIKit.FitText(name, 24);

            string idLine = string.IsNullOrEmpty(p.publicId) ? "OFFLINE PROFILE  -  no Player ID" : "PLAYER ID  " + p.publicId;
            var id = UIKit.NewText(card.transform, idLine, UITheme.FontCaption, string.IsNullOrEmpty(p.publicId) ? UITheme.Warning : UITheme.Cyan, TextAnchor.MiddleLeft, FontStyle.Bold, "Id");
            UIKit.Anchor(id.rectTransform, new Vector2(0f, 1f), new Vector2(1f, 1f), new Vector2(0.5f, 1f), new Vector2(0f, -64f), new Vector2(0f, 34f));
            id.rectTransform.offsetMin = new Vector2(110f, id.rectTransform.offsetMin.y);
            id.rectTransform.offsetMax = new Vector2(-12f, id.rectTransform.offsetMax.y);
            UIKit.FitText(id, 16);

            if (!string.IsNullOrEmpty(p.publicId))
            {
                var copy = UIKit.NewButton(card.transform, "CopyId", "COPY ID", UITheme.Button, () =>
                {
                    GUIUtility.systemCopyBuffer = p.publicId;
                    AppServices.Notifications.Post("Copied", "Player ID " + p.publicId + " copied to the clipboard.", NotificationSeverity.Success);
                }, 20);
                UIKit.Anchor(copy.Background.rectTransform, new Vector2(0f, 0f), new Vector2(0f, 0f), new Vector2(0f, 0f), new Vector2(110f, 12f), new Vector2(160f, 48f));
            }

            string rank = "RANK  " + (string.IsNullOrEmpty(p.rank) ? "Unranked" : p.rank);
            var rankText = UIKit.NewText(card.transform, rank, UITheme.FontCaption, UITheme.Accent, TextAnchor.MiddleRight, FontStyle.Bold, "Rank");
            UIKit.Anchor(rankText.rectTransform, new Vector2(1f, 0f), new Vector2(1f, 0f), new Vector2(1f, 0f), new Vector2(-14f, 14f), new Vector2(220f, 44f));
            UIKit.FitText(rankText, 14);
        }

        private void BuildRename()
        {
            UIWidgets.SectionTitle(content, "Display name");
            var row = UIKit.NewRect("RenameRow", content);
            UIKit.HStack(row.gameObject, 8f);
            UIKit.SetLayout(row.gameObject, preferredHeight: 64f);
            nameInput = UIForm.NewInput(row, "Name", "New name (3-16 letters, numbers, _)", InputField.ContentType.Standard, 16);
            UIKit.SetLayout(nameInput.gameObject, minWidth: 0f, flexibleWidth: 1f);
            var save = UIKit.NewButton(row, "Rename", "CHANGE", UITheme.Accent, RenameClicked, UITheme.FontCaption + 2);
            UIKit.SetLayout(save.gameObject, preferredWidth: 150f, flexibleWidth: 0f);
            nameStatus = UIWidgets.Paragraph(content, "You can change your name once per day. Your Player ID never changes.", UITheme.TextDim, 20);
        }

        private void RenameClicked()
        {
            string wanted = nameInput.text.Trim();
            if (wanted.Length == 0) return;
            AppServices.Account.ChangeUsername(wanted, result =>
            {
                if (nameStatus == null) return;
                nameStatus.text = result.Success ? "<color=#5CDB85>Name updated.</color>" : "<color=#F55257>" + result.Error + "</color>";
                nameStatus.color = UITheme.TextDim;
                if (result.Success) Refresh();
            });
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
            UIWidgets.SectionTitle(content, AppServices.Profile.IsServerBound ? "Statistics" : "Statistics (sample data)");
            var grid = UIWidgets.Grid(content, 150f, 96f, 10f, 4);
            UIWidgets.StatTile(grid, "Matches", s.matchesPlayed.ToString("N0"), UITheme.Text);
            UIWidgets.StatTile(grid, "Wins", s.wins.ToString("N0"), UITheme.Success);
            UIWidgets.StatTile(grid, "Win rate", (s.WinRate * 100f).ToString("0.#") + "%", UITheme.Success);
            UIWidgets.StatTile(grid, "Top 3", s.topThree.ToString("N0"), UITheme.Text);
            UIWidgets.StatTile(grid, "Eliminations", s.kills.ToString("N0"), UITheme.Accent);
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

    /// <summary>Another player's public profile (from search, friends or party). Only public fields are shown.</summary>
    public sealed class PlayerProfileScreen : LobbyScreen
    {
        public string Target;

        private RectTransform content;
        private Text status;
        private PlayerProfileDto loaded;
        private int requestToken;

        public override LobbyScreenId Id => LobbyScreenId.PlayerProfile;
        public override string Title => "Player";

        protected override void BuildContent(RectTransform parent)
        {
            status = UIKit.NewText(parent, "", UITheme.FontBody, UITheme.TextDim, TextAnchor.MiddleCenter, FontStyle.Normal, "Status");
            UIKit.Anchor(status.rectTransform, new Vector2(0f, 1f), Vector2.one, new Vector2(0.5f, 1f), Vector2.zero, new Vector2(0f, 60f));

            var scroll = UIKit.NewScroll(parent, "Scroll", out content);
            UIKit.Stretch((RectTransform)scroll.transform, 0f, 0f, 0f, 60f);
            UIKit.VStack(content.gameObject, 10f);
        }

        protected override void OnShown()
        {
            UIKit.DestroyChildren(content);
            loaded = null;
            if (string.IsNullOrEmpty(Target)) { status.text = "No player selected."; return; }

            status.text = "Loading...";
            int token = ++requestToken;
            AppServices.Social.GetProfile(Target, r =>
            {
                if (token != requestToken || this == null) return;
                if (!r.Ok) { status.text = r.Friendly; return; }
                loaded = r.Value;
                status.text = "";
                Build();
            });
        }

        private void Build()
        {
            UIKit.DestroyChildren(content);
            var p = loaded;

            var card = UIKit.NewRounded(content, "Header", UITheme.PanelLight);
            UIKit.SetLayout(card.gameObject, preferredHeight: 130f);
            var name = UIKit.NewText(card.transform, p.username, UITheme.FontTitle, UITheme.Text, TextAnchor.MiddleLeft, FontStyle.Bold, "Name");
            UIKit.Anchor(name.rectTransform, new Vector2(0f, 1f), new Vector2(1f, 1f), new Vector2(0.5f, 1f), new Vector2(0f, -10f), new Vector2(0f, 52f));
            name.rectTransform.offsetMin = new Vector2(20f, name.rectTransform.offsetMin.y);
            name.rectTransform.offsetMax = new Vector2(-20f, name.rectTransform.offsetMax.y);
            UIKit.FitText(name, 22);
            var line = UIKit.NewText(card.transform, "ID " + p.publicId + "   LV " + p.level + "   " + p.rank + "   " + StatusLabel(p.status),
                UITheme.FontCaption, UITheme.TextDim, TextAnchor.MiddleLeft, FontStyle.Normal, "Line");
            UIKit.Anchor(line.rectTransform, new Vector2(0f, 0f), new Vector2(1f, 0f), new Vector2(0.5f, 0f), new Vector2(0f, 14f), new Vector2(0f, 60f));
            line.rectTransform.offsetMin = new Vector2(20f, line.rectTransform.offsetMin.y);
            line.rectTransform.offsetMax = new Vector2(-20f, line.rectTransform.offsetMax.y);
            UIKit.FitText(line, 16);

            var actions = UIKit.NewRect("Actions", content);
            UIKit.HStack(actions.gameObject, 8f).childForceExpandWidth = true;
            UIKit.SetLayout(actions.gameObject, preferredHeight: 64f);
            if (p.relationship == "none")
                Action(actions, "ADD FRIEND", UITheme.Accent, () => AppServices.Social.SendFriendRequest(p.publicId, r => Report(r, "Friend request sent.")));
            else if (p.relationship == "request_sent")
                Action(actions, "REQUEST SENT", UITheme.Disabled, null);
            else if (p.relationship == "request_received")
                Action(actions, "ACCEPT REQUEST", UITheme.Accent, () => AcceptIncoming(p.publicId));
            else if (p.relationship == "friend")
                Action(actions, "INVITE TO PARTY", UITheme.Accent, () => AppServices.Social.Invite(p.publicId, AppServices.Modes.Selected.Id, r => Report(r, "Invitation sent.")));
            if (p.relationship != "self")
            {
                Action(actions, "BLOCK", UITheme.Button, () => Ctx.Confirm("Block player", $"Block {p.username}? You will no longer see each other.", "BLOCK",
                    () => AppServices.Social.Block(p.publicId, r => { Report(r, "Player blocked."); if (r.Success) Ctx.Screens.Back(); }), true));
                Action(actions, "REPORT", UITheme.Button, () => Ctx.Confirm("Report player", $"Report {p.username} for abusive behaviour?", "REPORT",
                    () => AppServices.Social.Report(p.publicId, "Reported from profile", r => Report(r, "Report sent. Thank you."))));
            }

            UIWidgets.SectionTitle(content, "Statistics");
            var grid = UIWidgets.Grid(content, 150f, 96f, 10f, 4);
            var s = p.stats;
            float kd = s.deaths == 0 ? s.kills : (float)s.kills / s.deaths;
            UIWidgets.StatTile(grid, "Matches", s.matches.ToString("N0"), UITheme.Text);
            UIWidgets.StatTile(grid, "Wins", s.wins.ToString("N0"), UITheme.Success);
            UIWidgets.StatTile(grid, "Eliminations", s.kills.ToString("N0"), UITheme.Accent);
            UIWidgets.StatTile(grid, "K/D", kd.ToString("0.00"), UITheme.Accent);

            UIWidgets.SectionTitle(content, "Equipped");
            var l = AppServices.Catalog;
            foreach (var pair in new[]
            {
                (CosmeticSlot.Outfit, p.loadout.outfit), (CosmeticSlot.Shoes, p.loadout.shoes), (CosmeticSlot.Mask, p.loadout.mask),
                (CosmeticSlot.Headgear, p.loadout.headgear), (CosmeticSlot.Backpack, p.loadout.backpack),
                (CosmeticSlot.WeaponSkin, p.loadout.weaponskin), (CosmeticSlot.Emote, p.loadout.emote)
            })
            {
                if (l.TryGet(pair.Item2, out var item)) UIWidgets.KeyValueRow(content, pair.Item1.DisplayName(), item.DisplayName, UITheme.Rarity(item.Rarity));
                else UIWidgets.KeyValueRow(content, pair.Item1.DisplayName(), "None", UITheme.TextDim);
            }
        }

        private static string StatusLabel(string s)
        {
            switch (s)
            {
                case "lobby": return "ONLINE - LOBBY";
                case "in_match": return "IN MATCH";
                case "hidden": return "STATUS HIDDEN";
                default: return "OFFLINE";
            }
        }

        private void Action(RectTransform row, string label, Color color, UnityEngine.Events.UnityAction onClick)
        {
            var b = UIKit.NewButton(row, "Action_" + label, label, color, onClick, UITheme.FontCaption + 2);
            UIKit.SetLayout(b.gameObject, minWidth: 0f, flexibleWidth: 1f);
            if (onClick == null) b.SetInteractable(false);
        }

        private void AcceptIncoming(string publicId)
        {
            foreach (var req in AppServices.Social.Snapshot.Incoming)
            {
                if (req.player.publicId != publicId) continue;
                AppServices.Social.RespondToRequest(req.requestId, true, r => { Report(r, "Friend added."); OnShown(); });
                return;
            }
        }

        private void Report(ServiceResult result, string success)
        {
            AppServices.Notifications.Post(result.Success ? "Done" : "Couldn't complete that", result.Success ? success : result.Error,
                result.Success ? NotificationSeverity.Success : NotificationSeverity.Warning);
            if (result.Success && loaded != null) OnShown();
        }
    }
}
