using System.Collections.Generic;
using Noxy.Core;
using UnityEngine;
using UnityEngine.UI;

namespace Noxy.Lobby
{
    /// <summary>One selectable game-mode card. Visual state mirrors GameModeSelection, nothing is decorative.</summary>
    public sealed class ModeCard
    {
        private static readonly Color SelectedBackground = new Color(0.30f, 0.17f, 0.06f, 0.96f);

        public GameModeDefinition Mode { get; private set; }
        public ButtonView View { get; private set; }

        private Image bar;
        private Text nameText, tagline, status;

        public static ModeCard Create(Transform parent, GameModeDefinition mode, UnityEngine.Events.UnityAction onClick)
        {
            var card = new ModeCard { Mode = mode };
            var view = UIKit.NewButton(parent, "Mode_" + mode.Id, "", UITheme.Button, onClick);
            card.View = view;
            Object.Destroy(view.Label.gameObject);

            card.bar = UIKit.NewImage(view.transform, "SelectedBar", UITheme.Accent);
            UIKit.Anchor(card.bar.rectTransform, new Vector2(0f, 0f), new Vector2(0f, 1f), new Vector2(0f, 0.5f), new Vector2(6f, 0f), new Vector2(6f, -24f));

            card.nameText = Line(view.transform, mode.DisplayName, UITheme.FontButton, FontStyle.Bold, -8f, 40f);
            card.tagline = Line(view.transform, mode.Tagline, UITheme.FontCaption, FontStyle.Normal, -46f, 30f);
            card.status = Line(view.transform, "", 22, FontStyle.Bold, -74f, 28f);

            if (mode.IsPlayable)
            {
                card.status.text = mode.RequiresDedicatedServer ? "ONLINE" : "OFFLINE PROTOTYPE";
                card.status.color = UITheme.Success;
            }
            else
            {
                card.status.text = "COMING SOON";
                card.status.color = UITheme.Warning;
            }
            return card;
        }

        private static Text Line(Transform parent, string text, int size, FontStyle style, float y, float height)
        {
            var t = UIKit.NewText(parent, text, size, UITheme.Text, TextAnchor.MiddleLeft, style);
            UIKit.Anchor(t.rectTransform, new Vector2(0f, 1f), new Vector2(1f, 1f), new Vector2(0.5f, 1f), new Vector2(6f, y), new Vector2(-40f, height));
            UIKit.FitText(t, Mathf.RoundToInt(size * 0.7f));
            return t;
        }

        public void SetSelected(bool selected)
        {
            bar.gameObject.SetActive(selected);
            View.SetColor(selected ? SelectedBackground : UITheme.Button);
            nameText.color = selected ? Color.white : new Color(0.82f, 0.87f, 0.94f, 1f);
            tagline.color = selected ? new Color(1f, 0.85f, 0.65f, 1f) : UITheme.TextDim;
        }
    }

    /// <summary>The lobby home screen: profile badge, currencies, navigation, mode selector and PLAY.</summary>
    public sealed class LobbyHud : LobbyScreen
    {
        private const float ToastSeconds = 3.5f;

        public override LobbyScreenId Id => LobbyScreenId.Lobby;
        public override ScreenStyle Style => ScreenStyle.Hud;
        public override bool InspectMode => false;

        private RectTransform profileGroup, currencyGroup, actionsGroup, navGroup, modeGroup, partyGroup, toast;
        private GridLayoutGroup navGrid, modeGrid;
        private Text nameText, idText, levelText, xpText, avatarLetter, creditsText, coresText, modeStatus, toastTitle, toastBody;
        private Image xpFill, toastBar;
        private ButtonView bell, playButton;
        private CanvasGroup toastGroup;
        private readonly Dictionary<LobbyScreenId, ButtonView> navButtons = new Dictionary<LobbyScreenId, ButtonView>();
        private readonly List<ModeCard> cards = new List<ModeCard>();
        private readonly List<ButtonView> partySlots = new List<ButtonView>();
        private float toastTimer;

        private const float NavRows = 3f;

        public override Rect FreeArea
        {
            get
            {
                Vector2 s = Ctx.Size;
                if (!Ctx.Portrait) return new Rect(0f, 0.02f, 1f, 0.9f);

                float top = 224f + NavRows * 72f + 16f + 12f;
                float bottom = (modeGroup != null && modeGroup.rect.height > 10f ? modeGroup.rect.height : 360f) + 24f;
                float y0 = Mathf.Clamp01(bottom / s.y);
                float y1 = Mathf.Clamp01(1f - top / s.y);
                return new Rect(0f, y0, 1f, Mathf.Max(0.2f, y1 - y0));
            }
        }

        protected override void BuildContent(RectTransform parent)
        {
            var gradient = UIKit.NewImage(parent, "TopShade", new Color(0f, 0f, 0f, 0.6f), UISprites.GradientTop);
            UIKit.Anchor(gradient.rectTransform, new Vector2(0f, 1f), Vector2.one, new Vector2(0.5f, 1f), Vector2.zero, new Vector2(0f, 170f));

            BuildProfile(parent);
            BuildCurrency(parent);
            BuildActions(parent);
            BuildNav(parent);
            BuildParty(parent);
            BuildModes(parent);
            BuildToast(parent);

            AppServices.Profile.ProfileChanged += RefreshProfile;
            AppServices.Notifications.Changed += RefreshNotifications;
            AppServices.Notifications.ToastRequested += ShowToast;
            AppServices.Modes.SelectionChanged += OnModeChanged;
            AppServices.Social.Changed += RefreshParty;
            AppServices.Account.Changed += RefreshParty;
            AppServices.Account.Changed += RefreshProfile;

            RefreshProfile();
            RefreshNotifications();
            RefreshModes();
            RefreshParty();
        }

        private void OnModeChanged(GameModeDefinition mode)
        {
            LobbyAudioService.PlayModeSelect();
            RefreshModes();
            RefreshParty();

            // The party leader's mode choice is shared with the party through the backend.
            var snap = AppServices.Social.Snapshot;
            if (!AppServices.Social.IsAvailable || !snap.InParty || mode.MaxPartySize <= 1 || snap.Party.modeId == mode.Id) return;
            if (snap.Party.leaderPublicId != AppServices.Profile.Profile.publicId) return;
            AppServices.Social.SetPartyMode(mode.Id, r => { if (!r.Success) SocialUi.Report(r, ""); });
        }

        private void OnDestroy()
        {
            if (AppServices.Profile != null) AppServices.Profile.ProfileChanged -= RefreshProfile;
            if (AppServices.Modes != null) AppServices.Modes.SelectionChanged -= OnModeChanged;
            if (AppServices.Social != null) AppServices.Social.Changed -= RefreshParty;
            if (AppServices.Account != null) { AppServices.Account.Changed -= RefreshParty; AppServices.Account.Changed -= RefreshProfile; }
            if (AppServices.Notifications != null)
            {
                AppServices.Notifications.Changed -= RefreshNotifications;
                AppServices.Notifications.ToastRequested -= ShowToast;
            }
        }

        protected override void OnShown()
        {
            RefreshProfile();
            RefreshNotifications();
            RefreshParty();
        }

        private void BuildProfile(Transform parent)
        {
            var group = UIKit.NewButton(parent, "ProfileBadge", "", UITheme.Chip, () => Ctx.Screens.Open(LobbyScreenId.Profile));
            Object.Destroy(group.Label.gameObject);
            profileGroup = group.Background.rectTransform;

            var avatar = UIKit.NewImage(profileGroup, "Avatar", UITheme.Accent, UISprites.Circle);
            UIKit.Anchor(avatar.rectTransform, new Vector2(0f, 0.5f), new Vector2(0f, 0.5f), new Vector2(0f, 0.5f), new Vector2(12f, 0f), new Vector2(84f, 84f));
            avatarLetter = UIKit.NewText(avatar.transform, "O", 44, new Color(0.1f, 0.06f, 0.02f), TextAnchor.MiddleCenter, FontStyle.Bold, "Initial");

            nameText = UIKit.NewText(profileGroup, "", UITheme.FontButton + 2, UITheme.Text, TextAnchor.MiddleLeft, FontStyle.Bold, "Name");
            UIKit.Anchor(nameText.rectTransform, new Vector2(0f, 1f), new Vector2(0f, 1f), new Vector2(0f, 1f), new Vector2(110f, -4f), new Vector2(350f, 38f));
            UIKit.FitText(nameText, 22);

            idText = UIKit.NewText(profileGroup, "", 22, UITheme.Cyan, TextAnchor.MiddleLeft, FontStyle.Bold, "Id");
            UIKit.Anchor(idText.rectTransform, new Vector2(0f, 1f), new Vector2(0f, 1f), new Vector2(0f, 1f), new Vector2(110f, -40f), new Vector2(350f, 28f));
            UIKit.FitText(idText, 14);

            levelText = UIKit.NewText(profileGroup, "", UITheme.FontBody, UITheme.Accent, TextAnchor.MiddleLeft, FontStyle.Bold, "Level");
            UIKit.Anchor(levelText.rectTransform, new Vector2(0f, 1f), new Vector2(0f, 1f), new Vector2(0f, 1f), new Vector2(110f, -70f), new Vector2(110f, 30f));

            xpText = UIKit.NewText(profileGroup, "", UITheme.FontCaption, UITheme.TextDim, TextAnchor.MiddleRight, FontStyle.Normal, "Xp");
            UIKit.Anchor(xpText.rectTransform, new Vector2(0f, 1f), new Vector2(0f, 1f), new Vector2(0f, 1f), new Vector2(220f, -72f), new Vector2(240f, 28f));
            UIKit.FitText(xpText, 18);

            var track = UIKit.NewRounded(profileGroup, "XpTrack", new Color(1f, 1f, 1f, 0.15f));
            UIKit.Anchor(track.rectTransform, Vector2.zero, Vector2.zero, Vector2.zero, new Vector2(110f, 12f), new Vector2(350f, 12f));
            xpFill = UIKit.NewRounded(track.transform, "XpFill", UITheme.Cyan);
            xpFill.rectTransform.anchorMin = Vector2.zero;
            xpFill.rectTransform.anchorMax = new Vector2(0.5f, 1f);
            xpFill.rectTransform.offsetMin = Vector2.zero;
            xpFill.rectTransform.offsetMax = Vector2.zero;
        }

        /// <summary>Row of up to four party slots: members with ready state, or "+" to invite.</summary>
        private void BuildParty(Transform parent)
        {
            partyGroup = UIKit.NewRect("Party", parent);
            UIKit.HStack(partyGroup.gameObject, 6f, 0, TextAnchor.MiddleCenter).childForceExpandWidth = true;
            for (int i = 0; i < 4; i++)
            {
                int index = i;
                var slot = UIKit.NewButton(partyGroup, "PartySlot" + i, "+", UITheme.Chip, () => OnPartySlot(index), 20);
                UIKit.SetLayout(slot.gameObject, minWidth: 0f, flexibleWidth: 1f, preferredHeight: 64f);
                partySlots.Add(slot);
            }
        }

        private void OnPartySlot(int index)
        {
            var snap = AppServices.Social.Snapshot;
            if (AppServices.Social.IsAvailable && snap.InParty && index < snap.Party.members.Count)
            {
                var member = snap.Party.members[index];
                if (member.publicId == AppServices.Profile.Profile.publicId) Ctx.Screens.Open(LobbyScreenId.Party);
                else Ctx.ViewPlayer(member.publicId);
                return;
            }

            if (index == 0) { Ctx.Screens.Open(LobbyScreenId.Party); return; }
            if (AppServices.Modes.Selected.MaxPartySize <= 1)
            {
                AppServices.Notifications.Post("Solo mode", AppServices.Modes.Selected.DisplayName + " can't be played with a party.", NotificationSeverity.Info);
                return;
            }
            Ctx.Screens.Open(LobbyScreenId.Friends);
        }

        private void RefreshParty()
        {
            RefreshSocialBadges();
            if (partySlots.Count == 0) return;
            var snap = AppServices.Social.Snapshot;
            bool online = AppServices.Social.IsAvailable;
            int max = Mathf.Clamp(AppServices.Modes.Selected.MaxPartySize, 1, 4);

            for (int i = 0; i < partySlots.Count; i++)
            {
                var slot = partySlots[i];
                slot.gameObject.SetActive(i < max);
                if (i >= max) continue;

                if (online && snap.InParty && i < snap.Party.members.Count)
                {
                    var m = snap.Party.members[i];
                    string dot = m.ready ? "<color=#5CDB85>\u25CF</color> " : "<color=#FFC742>\u25CF</color> ";
                    string name = m.username.Length > 8 ? m.username.Substring(0, 8) : m.username;
                    slot.SetLabel(dot + name + (m.isLeader ? "*" : ""));
                    slot.SetColor(m.publicId == AppServices.Profile.Profile.publicId ? new Color(0.30f, 0.17f, 0.06f, 0.9f) : UITheme.Button);
                }
                else if (i == 0)
                {
                    slot.SetLabel("YOU");
                    slot.SetColor(new Color(0.30f, 0.17f, 0.06f, 0.9f));
                }
                else
                {
                    slot.SetLabel("+ INVITE");
                    slot.SetColor(UITheme.Chip);
                }
            }
        }

        private void BuildCurrency(Transform parent)
        {
            currencyGroup = UIKit.NewRect("Currency", parent);
            UIKit.HStack(currencyGroup.gameObject, 8f, 0, TextAnchor.MiddleLeft);
            creditsText = Chip(currencyGroup, "Coins", new Color(1f, 0.78f, 0.26f), "C");
            coresText = Chip(currencyGroup, "Diamonds", UITheme.Cyan, "D");
        }

        private Text Chip(Transform parent, string name, Color dotColor, string letter)
        {
            var chip = UIKit.NewButton(parent, name, "0", UITheme.Chip, () => Ctx.Screens.Open(LobbyScreenId.Shop), UITheme.FontBody);
            UIKit.SetLayout(chip.gameObject, preferredWidth: 165f, preferredHeight: 60f);
            var dot = UIKit.NewImage(chip.transform, "Dot", dotColor, UISprites.Circle);
            UIKit.Anchor(dot.rectTransform, new Vector2(0f, 0.5f), new Vector2(0f, 0.5f), new Vector2(0f, 0.5f), new Vector2(14f, 0f), new Vector2(28f, 28f));
            var mark = UIKit.NewText(dot.transform, letter, 18, new Color(0.08f, 0.09f, 0.12f), TextAnchor.MiddleCenter, FontStyle.Bold, "Mark");
            chip.Label.alignment = TextAnchor.MiddleRight;
            UIKit.Stretch(chip.Label.rectTransform, 48f, 0f, 14f, 0f);
            return chip.Label;
        }

        private void BuildActions(Transform parent)
        {
            actionsGroup = UIKit.NewRect("Actions", parent);
            UIKit.HStack(actionsGroup.gameObject, 8f, 0, TextAnchor.MiddleRight);

            bell = UIKit.NewButton(actionsGroup, "Alerts", "ALERTS", UITheme.Chip, () => Ctx.Screens.Open(LobbyScreenId.Notifications), UITheme.FontCaption + 2);
            UIKit.SetLayout(bell.gameObject, preferredWidth: 122f, preferredHeight: 60f);

            var settings = UIKit.NewButton(actionsGroup, "Settings", "SETTINGS", UITheme.Chip, () => Ctx.Screens.Open(LobbyScreenId.Settings), UITheme.FontCaption + 2);
            UIKit.SetLayout(settings.gameObject, preferredWidth: 146f, preferredHeight: 60f);
        }

        private void BuildNav(Transform parent)
        {
            navGroup = UIKit.NewRect("Navigation", parent);
            navGrid = UIKit.NewGrid(navGroup.gameObject, 8f);
            navGrid.cellSize = new Vector2(250f, 62f);

            AddNav("CHARACTER", LobbyScreenId.Profile, true);
            AddNav("VAULT", LobbyScreenId.Vault, true);
            AddNav("INVENTORY", LobbyScreenId.Inventory, true);
            AddNav("FRIENDS", LobbyScreenId.Friends, false);
            AddNav("PARTY", LobbyScreenId.Party, false);
            AddNav("INVITES", LobbyScreenId.Invitations, false);
            AddNav("MISSIONS", LobbyScreenId.Missions, false);
            AddNav("REWARDS", LobbyScreenId.Rewards, false);
            AddNav("SHOP", LobbyScreenId.Shop, false);
        }

        private void AddNav(string label, LobbyScreenId target, bool emphasis)
        {
            var color = emphasis ? new Color(0.10f, 0.26f, 0.34f, 0.94f) : UITheme.Button;
            var button = UIKit.NewButton(navGroup, "Nav_" + target, label, color, () => Ctx.Screens.Open(target), UITheme.FontBody);
            navButtons[target] = button;
        }

        private void BuildModes(Transform parent)
        {
            modeGroup = UIKit.NewRect("ModePanel", parent);
            UIKit.VStack(modeGroup.gameObject, 10f);
            var fitter = modeGroup.gameObject.AddComponent<ContentSizeFitter>();
            fitter.verticalFit = ContentSizeFitter.FitMode.PreferredSize;

            var header = UIKit.NewText(modeGroup, "SELECT MODE", UITheme.FontCaption, UITheme.TextDim, TextAnchor.MiddleLeft, FontStyle.Bold, "Header");
            UIKit.SetLayout(header.gameObject, preferredHeight: 30f);

            var gridRect = UIKit.NewRect("ModeGrid", modeGroup);
            modeGrid = UIKit.NewGrid(gridRect.gameObject, 10f);
            modeGrid.cellSize = new Vector2(460f, 100f);

            foreach (var mode in AppServices.Modes.Modes)
            {
                var captured = mode;
                var card = ModeCard.Create(gridRect, mode, () => AppServices.Modes.Select(captured.Id));
                cards.Add(card);
            }

            modeStatus = UIKit.NewText(modeGroup, "", UITheme.FontCaption, UITheme.TextDim, TextAnchor.MiddleLeft, FontStyle.Normal, "ModeStatus");
            UIKit.SetLayout(modeStatus.gameObject, minHeight: 62f);

            playButton = UIKit.NewButton(modeGroup, "PlayButton", "PLAY", UITheme.Accent, () => Ctx.Matchmaking.Play(), UITheme.FontHuge);
            playButton.Label.resizeTextForBestFit = false;
            playButton.PulseAmount = 0.015f;
            UIKit.SetLayout(playButton.gameObject, preferredHeight: 140f);
        }

        private void BuildToast(Transform parent)
        {
            var panel = UIKit.NewRounded(parent, "Toast", new Color(0.05f, 0.08f, 0.13f, 0.96f));
            toast = panel.rectTransform;
            toastGroup = panel.gameObject.AddComponent<CanvasGroup>();
            toastGroup.alpha = 0f;
            toastGroup.blocksRaycasts = false;
            toastGroup.interactable = false;

            toastBar = UIKit.NewImage(toast, "Bar", UITheme.Cyan);
            UIKit.Anchor(toastBar.rectTransform, new Vector2(0f, 0f), new Vector2(0f, 1f), new Vector2(0f, 0.5f), new Vector2(8f, 0f), new Vector2(6f, -24f));

            toastTitle = UIKit.NewText(toast, "", UITheme.FontBody, UITheme.Text, TextAnchor.MiddleLeft, FontStyle.Bold, "Title");
            UIKit.Anchor(toastTitle.rectTransform, new Vector2(0f, 1f), new Vector2(1f, 1f), new Vector2(0.5f, 1f), new Vector2(12f, -6f), new Vector2(-48f, 34f));
            UIKit.FitText(toastTitle, 20);

            toastBody = UIKit.NewText(toast, "", UITheme.FontCaption, UITheme.TextDim, TextAnchor.UpperLeft, FontStyle.Normal, "Body");
            UIKit.Anchor(toastBody.rectTransform, new Vector2(0f, 1f), new Vector2(1f, 1f), new Vector2(0.5f, 1f), new Vector2(12f, -40f), new Vector2(-48f, 56f));
            UIKit.FitText(toastBody, 18);
        }

        protected override void OnLayout()
        {
            if (profileGroup == null) return;
            Vector2 s = Ctx.Size;
            bool portrait = Ctx.Portrait;

            UIKit.Anchor(profileGroup, new Vector2(0f, 1f), new Vector2(0f, 1f), new Vector2(0f, 1f), new Vector2(16f, -12f), new Vector2(470f, 128f));
            UIKit.Anchor(actionsGroup, Vector2.one, Vector2.one, Vector2.one, new Vector2(-16f, -12f), new Vector2(276f, 60f));

            if (!portrait)
            {
                UIKit.Anchor(currencyGroup, Vector2.one, Vector2.one, Vector2.one, new Vector2(-304f, -12f), new Vector2(338f, 60f));
                UIKit.Anchor(partyGroup, Vector2.one, Vector2.one, Vector2.one, new Vector2(-16f, -84f), new Vector2(490f, 64f));

                UIKit.Anchor(navGroup, new Vector2(0f, 1f), new Vector2(0f, 1f), new Vector2(0f, 1f), new Vector2(16f, -152f), new Vector2(250f, 9f * 62f + 8f * 8f));
                navGrid.constraint = GridLayoutGroup.Constraint.FixedColumnCount;
                navGrid.constraintCount = 1;
                navGrid.cellSize = new Vector2(250f, 62f);

                UIKit.Anchor(modeGroup, new Vector2(1f, 0f), new Vector2(1f, 0f), new Vector2(1f, 0f), new Vector2(-16f, 16f), new Vector2(460f, 0f));
                modeGrid.constraint = GridLayoutGroup.Constraint.FixedColumnCount;
                modeGrid.constraintCount = 1;
                modeGrid.cellSize = new Vector2(460f, 104f);
                UIKit.SetLayout(playButton.gameObject, preferredHeight: 140f);

                UIKit.Anchor(toast, new Vector2(0.5f, 1f), new Vector2(0.5f, 1f), new Vector2(0.5f, 1f), new Vector2(0f, -152f), new Vector2(Mathf.Min(560f, s.x - 800f), 100f));
            }
            else
            {
                UIKit.Anchor(currencyGroup, new Vector2(0f, 1f), new Vector2(0f, 1f), new Vector2(0f, 1f), new Vector2(16f, -152f), new Vector2(338f, 60f));
                UIKit.Anchor(partyGroup, Vector2.one, Vector2.one, Vector2.one, new Vector2(-16f, -152f), new Vector2(Mathf.Max(200f, s.x - 338f - 48f), 60f));

                float navCell = Mathf.Floor((s.x - 32f - 2f * 8f) / 3f);
                UIKit.Anchor(navGroup, new Vector2(0f, 1f), new Vector2(1f, 1f), new Vector2(0.5f, 1f), new Vector2(0f, -224f), new Vector2(-32f, NavRows * 72f + 2f * 8f));
                navGrid.constraint = GridLayoutGroup.Constraint.FixedColumnCount;
                navGrid.constraintCount = 3;
                navGrid.cellSize = new Vector2(navCell, 72f);

                UIKit.Anchor(modeGroup, new Vector2(0f, 0f), new Vector2(1f, 0f), new Vector2(0.5f, 0f), new Vector2(0f, 16f), new Vector2(-32f, 0f));
                float modeCell = Mathf.Floor((s.x - 32f - 2f * 10f) / 3f);
                modeGrid.constraint = GridLayoutGroup.Constraint.FixedColumnCount;
                modeGrid.constraintCount = 3;
                modeGrid.cellSize = new Vector2(modeCell, 112f);
                UIKit.SetLayout(playButton.gameObject, preferredHeight: 120f);

                UIKit.Anchor(toast, new Vector2(0.5f, 1f), new Vector2(0.5f, 1f), new Vector2(0.5f, 1f), new Vector2(0f, -480f), new Vector2(Mathf.Min(560f, s.x - 32f), 100f));
            }
        }

        private void RefreshProfile()
        {
            if (nameText == null) return;
            var p = AppServices.Profile.Profile;
            nameText.text = p.username;
            avatarLetter.text = string.IsNullOrEmpty(p.username) ? "?" : p.username.Substring(0, 1).ToUpperInvariant();
            string rank = string.IsNullOrEmpty(p.rank) ? "Unranked" : p.rank;
            if (string.IsNullOrEmpty(p.publicId))
            {
                idText.text = "OFFLINE PROFILE";
                idText.color = UITheme.Warning;
            }
            else
            {
                idText.text = "ID " + p.publicId + "  |  " + rank;
                idText.color = UITheme.Cyan;
            }
            levelText.text = "LV " + p.level;
            xpText.text = p.xp.ToString("N0") + " / " + p.XpToNextLevel.ToString("N0") + " XP";
            xpFill.rectTransform.anchorMax = new Vector2(Mathf.Clamp01((float)p.xp / Mathf.Max(1, p.XpToNextLevel)), 1f);
            creditsText.text = p.credits.ToString("N0");
            coresText.text = p.cores.ToString("N0");
            navButtons[LobbyScreenId.Rewards].SetBadge(AppServices.Profile.CanClaimDailyReward ? 1 : 0);
            RefreshSocialBadges();
        }

        private void RefreshSocialBadges()
        {
            if (!navButtons.ContainsKey(LobbyScreenId.Friends)) return;
            var snap = AppServices.Social.Snapshot;
            bool online = AppServices.Social.IsAvailable;
            navButtons[LobbyScreenId.Friends].SetBadge(online ? snap.Incoming.Count : 0);
            navButtons[LobbyScreenId.Invitations].SetBadge(online ? snap.Invitations.Count : 0);
        }

        private void RefreshNotifications()
        {
            if (bell == null) return;
            bell.SetBadge(AppServices.Notifications.UnreadCount);
        }

        private void RefreshModes()
        {
            if (playButton == null) return;
            var selected = AppServices.Modes.Selected;
            foreach (var card in cards) card.SetSelected(card.Mode == selected);

            modeStatus.text = selected.StatusNote;
            modeStatus.color = selected.IsPlayable ? UITheme.Success : UITheme.Warning;

            bool playable = selected.IsPlayable;
            playButton.SetInteractable(playable);
            playButton.SetColor(playable ? UITheme.Accent : UITheme.Disabled);
            playButton.Label.fontSize = playable ? UITheme.FontHuge : 44;
            string sub = "<size=24>" + selected.DisplayName.ToUpperInvariant() + "</size>";
            playButton.SetLabel((playable ? "PLAY" : "COMING SOON") + "\n" + sub);
        }

        private void ShowToast(NotificationMessage message)
        {
            if (toastTitle == null) return;
            toastTitle.text = message.Title;
            toastBody.text = message.Body;
            toastBar.color = UITheme.Severity(message.Severity);
            toastTimer = ToastSeconds;
        }

        private void Update()
        {
            if (toastGroup != null)
            {
                toastTimer -= Time.unscaledDeltaTime;
                bool visible = toastTimer > 0f && Ctx.Screens.Current == LobbyScreenId.Lobby;
                toastGroup.alpha = Mathf.MoveTowards(toastGroup.alpha, visible ? 1f : 0f, Time.unscaledDeltaTime * 5f);
            }

            if (Ctx.Screens.Current != LobbyScreenId.Lobby || Ctx.Matchmaking.IsOverlayVisible || Ctx.Dialog.IsVisible) return;

            var modes = AppServices.Modes.Modes;
            if (InputCompat.KeyDown(KeyCode.Alpha1) && modes.Count > 0) AppServices.Modes.Select(modes[0].Id);
            if (InputCompat.KeyDown(KeyCode.Alpha2) && modes.Count > 1) AppServices.Modes.Select(modes[1].Id);
            if (InputCompat.KeyDown(KeyCode.Alpha3) && modes.Count > 2) AppServices.Modes.Select(modes[2].Id);
            if (InputCompat.KeyDown(KeyCode.Return) || InputCompat.KeyDown(KeyCode.KeypadEnter)) Ctx.Matchmaking.Play();
        }
    }
}
