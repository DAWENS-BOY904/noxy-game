using System;
using System.Collections.Generic;
using Noxy.Core;
using UnityEngine;
using UnityEngine.UI;

namespace Noxy.Lobby
{
    public struct RowButton
    {
        public string Label;
        public Color Color;
        public bool Enabled;
        public Action Click;

        public RowButton(string label, Color color, Action click, bool enabled = true)
        {
            Label = label; Color = color; Click = click; Enabled = enabled;
        }
    }

    public static class SocialUi
    {
        public static string StatusText(string status)
        {
            switch (status)
            {
                case "lobby": return "Online - in lobby";
                case "in_match": return "In match";
                case "hidden": return "Status hidden";
                default: return "Offline";
            }
        }

        public static Color StatusColor(string status)
        {
            switch (status)
            {
                case "lobby": return UITheme.Success;
                case "in_match": return UITheme.Warning;
                default: return UITheme.TextDim;
            }
        }

        public static RectTransform Row(Transform parent, string title, string subtitle, Color titleColor, params RowButton[] buttons)
        {
            var card = UIKit.NewRounded(parent, "Row", UITheme.PanelLight);
            UIKit.SetLayout(card.gameObject, preferredHeight: 96f);

            float buttonsWidth = buttons.Length * 138f + 12f;
            var t = UIKit.NewText(card.transform, title, UITheme.FontButton, titleColor, TextAnchor.MiddleLeft, FontStyle.Bold, "Title");
            UIKit.Anchor(t.rectTransform, new Vector2(0f, 1f), new Vector2(1f, 1f), new Vector2(0.5f, 1f), new Vector2(0f, -8f), new Vector2(0f, 38f));
            t.rectTransform.offsetMin = new Vector2(20f, t.rectTransform.offsetMin.y);
            t.rectTransform.offsetMax = new Vector2(-buttonsWidth, t.rectTransform.offsetMax.y);
            UIKit.FitText(t, 18);

            var s = UIKit.NewText(card.transform, subtitle, UITheme.FontCaption, UITheme.TextDim, TextAnchor.MiddleLeft, FontStyle.Normal, "Subtitle");
            UIKit.Anchor(s.rectTransform, new Vector2(0f, 0f), new Vector2(1f, 0f), new Vector2(0.5f, 0f), new Vector2(0f, 8f), new Vector2(0f, 40f));
            s.rectTransform.offsetMin = new Vector2(20f, s.rectTransform.offsetMin.y);
            s.rectTransform.offsetMax = new Vector2(-buttonsWidth, s.rectTransform.offsetMax.y);
            UIKit.FitText(s, 15);

            for (int i = 0; i < buttons.Length; i++)
            {
                var spec = buttons[i];
                var b = UIKit.NewButton(card.transform, "Btn_" + spec.Label, spec.Label, spec.Enabled ? spec.Color : UITheme.Disabled, () => spec.Click?.Invoke(), 20);
                float x = -10f - (buttons.Length - 1 - i) * 138f;
                UIKit.Anchor(b.Background.rectTransform, new Vector2(1f, 0.5f), new Vector2(1f, 0.5f), new Vector2(1f, 0.5f), new Vector2(x, 0f), new Vector2(130f, 64f));
                b.SetInteractable(spec.Enabled);
            }
            return card.rectTransform;
        }

        public static void Notice(Transform parent, string text, Color background)
        {
            var banner = UIKit.NewRounded(parent, "Notice", background);
            UIKit.SetLayout(banner.gameObject, preferredHeight: 110f);
            var t = UIKit.NewText(banner.transform, text, UITheme.FontCaption, new Color(1f, 0.92f, 0.7f), TextAnchor.MiddleLeft, FontStyle.Normal, "Text");
            UIKit.Stretch(t.rectTransform, 18f, 6f, 18f, 6f);
            UIKit.FitText(t, 16);
        }

        public static void Report(ServiceResult r, string success)
        {
            AppServices.Notifications.Post(r.Success ? "Done" : "Couldn't complete that", r.Success ? success : r.Error,
                r.Success ? NotificationSeverity.Success : NotificationSeverity.Warning);
        }

        public static void OfflineCard(LobbyContext ctx, Transform parent)
        {
            Notice(parent, AppServices.Social.UnavailableReason, new Color(0.35f, 0.25f, 0.05f, 0.9f));
            var open = UIKit.NewButton(parent, "OpenAccount", "OPEN ACCOUNT SETTINGS", UITheme.Accent, () => ctx.Screens.Open(LobbyScreenId.Settings), UITheme.FontBody);
            UIKit.SetLayout(open.gameObject, preferredHeight: 72f);
        }
    }

    /// <summary>Base for list screens fed by the social snapshot. Rebuilds when the snapshot changes while visible.</summary>
    public abstract class SocialScreen : LobbyScreen
    {
        protected RectTransform ListRoot;
        private bool visible;

        protected abstract void BuildList();

        protected override void BuildContent(RectTransform parent)
        {
            var scroll = UIKit.NewScroll(parent, "Scroll", out ListRoot);
            UIKit.Stretch((RectTransform)scroll.transform, 0f, 0f, 0f, ReservedTop);
            UIKit.VStack(ListRoot.gameObject, 10f);
            BuildHeader(parent);
            AppServices.Social.Changed += OnSocialChanged;
            AppServices.Account.Changed += OnSocialChanged;
        }

        protected virtual float ReservedTop => 0f;
        protected virtual void BuildHeader(RectTransform parent) { }

        private void OnDestroy()
        {
            if (AppServices.Social != null) AppServices.Social.Changed -= OnSocialChanged;
            if (AppServices.Account != null) AppServices.Account.Changed -= OnSocialChanged;
        }

        private void OnSocialChanged()
        {
            if (visible) Rebuild();
        }

        protected override void OnShown()
        {
            visible = true;
            Rebuild();
            if (AppServices.Social.IsAvailable) AppServices.Social.Refresh();
        }

        protected override void OnHidden() => visible = false;

        protected void Rebuild()
        {
            UIKit.DestroyChildren(ListRoot);
            if (!AppServices.Social.IsAvailable) { SocialUi.OfflineCard(Ctx, ListRoot); return; }
            BuildList();
        }
    }

    public sealed class FriendsScreen : SocialScreen
    {
        private enum Tab { Friends, Requests, Add }

        private TabBar tabs;
        private Tab tab = Tab.Friends;
        private InputField searchInput;
        private List<PlayerBriefDto> results = new List<PlayerBriefDto>();
        private string searchMessage = "Search by exact 10-digit Player ID or by username.";
        private bool searching;

        public override LobbyScreenId Id => LobbyScreenId.Friends;
        public override string Title => "Friends";
        protected override float ReservedTop => 130f;

        protected override void BuildHeader(RectTransform parent)
        {
            tabs = TabBar.Create(parent, new[] { "FRIENDS", "REQUESTS", "ADD" }, i => { tab = (Tab)i; Rebuild(); }, 22);
            UIKit.Anchor(tabs.Root, new Vector2(0f, 1f), Vector2.one, new Vector2(0.5f, 1f), Vector2.zero, new Vector2(0f, 60f));

            var idRow = UIKit.NewRect("MyId", parent);
            UIKit.Anchor(idRow, new Vector2(0f, 1f), Vector2.one, new Vector2(0.5f, 1f), new Vector2(0f, -68f), new Vector2(0f, 54f));
            UIKit.HStack(idRow.gameObject, 8f);
            var label = UIKit.NewText(idRow, "", UITheme.FontCaption, UITheme.Cyan, TextAnchor.MiddleLeft, FontStyle.Bold, "MyIdLabel");
            label.name = "MyIdLabel";
            UIKit.SetLayout(label.gameObject, minWidth: 0f, flexibleWidth: 1f);
            var copy = UIKit.NewButton(idRow, "Copy", "COPY MY ID", UITheme.Button, () =>
            {
                string id = AppServices.Profile.Profile.publicId;
                if (string.IsNullOrEmpty(id)) return;
                GUIUtility.systemCopyBuffer = id;
                AppServices.Notifications.Post("Copied", "Your Player ID was copied.", NotificationSeverity.Success);
            }, 20);
            UIKit.SetLayout(copy.gameObject, preferredWidth: 190f, flexibleWidth: 0f);
        }

        protected override void BuildList()
        {
            var snap = AppServices.Social.Snapshot;
            tabs.SetSelected((int)tab);
            var myIdLabel = PanelRect.Find("Body/MyId/MyIdLabel");
            if (myIdLabel != null) myIdLabel.GetComponent<Text>().text = "YOUR ID  " + AppServices.Profile.Profile.publicId;

            switch (tab)
            {
                case Tab.Friends: BuildFriends(snap); break;
                case Tab.Requests: BuildRequests(snap); break;
                default: BuildAdd(); break;
            }
        }

        private void BuildFriends(SocialSnapshot snap)
        {
            if (snap.Friends.Count == 0)
            {
                SocialUi.Notice(ListRoot, "No friends yet. Use the ADD tab to find players by Player ID or username.", new Color(0.1f, 0.18f, 0.28f, 0.9f));
                return;
            }
            foreach (var f in snap.Friends)
            {
                var friend = f;
                SocialUi.Row(ListRoot, friend.username, "LV " + friend.level + "  -  " + SocialUi.StatusText(friend.status), friend.status == "lobby" ? UITheme.Text : UITheme.TextDim,
                    new RowButton("VIEW", UITheme.Button, () => Ctx.ViewPlayer(friend.publicId)),
                    new RowButton("INVITE", UITheme.Accent, () => Invite(friend), friend.status != "in_match"),
                    new RowButton("REMOVE", UITheme.Button, () => Ctx.Confirm("Remove friend", $"Remove {friend.username} from your friends?", "REMOVE",
                        () => AppServices.Social.RemoveFriend(friend.publicId, r => SocialUi.Report(r, "Friend removed.")), true)));
            }
        }

        private void Invite(PlayerBriefDto friend)
        {
            string mode = AppServices.Modes.Selected.Id;
            AppServices.Social.Invite(friend.publicId, mode, r => SocialUi.Report(r, "Invitation sent to " + friend.username + "."));
        }

        private void BuildRequests(SocialSnapshot snap)
        {
            UIWidgets.SectionTitle(ListRoot, "Incoming (" + snap.Incoming.Count + ")");
            if (snap.Incoming.Count == 0) UIWidgets.Paragraph(ListRoot, "No incoming requests.", UITheme.TextDim);
            foreach (var req in snap.Incoming)
            {
                var r = req;
                SocialUi.Row(ListRoot, r.player.username, "LV " + r.player.level + "  -  wants to be your friend", UITheme.Text,
                    new RowButton("DECLINE", UITheme.Button, () => AppServices.Social.RespondToRequest(r.requestId, false, x => SocialUi.Report(x, "Request declined."))),
                    new RowButton("ACCEPT", UITheme.Accent, () => AppServices.Social.RespondToRequest(r.requestId, true, x => SocialUi.Report(x, "Friend added."))));
            }

            UIWidgets.SectionTitle(ListRoot, "Sent (" + snap.Outgoing.Count + ")");
            if (snap.Outgoing.Count == 0) UIWidgets.Paragraph(ListRoot, "No pending requests.", UITheme.TextDim);
            foreach (var req in snap.Outgoing)
            {
                var r = req;
                SocialUi.Row(ListRoot, r.player.username, "Waiting for a response", UITheme.TextDim,
                    new RowButton("CANCEL", UITheme.Button, () => AppServices.Social.CancelRequest(r.requestId, x => SocialUi.Report(x, "Request cancelled."))));
            }
        }

        private void BuildAdd()
        {
            var row = UIKit.NewRect("SearchRow", ListRoot);
            UIKit.HStack(row.gameObject, 8f);
            UIKit.SetLayout(row.gameObject, preferredHeight: 64f);
            searchInput = UIForm.NewInput(row, "Search", "Player ID or username", InputField.ContentType.Alphanumeric, 16);
            UIKit.SetLayout(searchInput.gameObject, minWidth: 0f, flexibleWidth: 1f);
            searchInput.onEndEdit.AddListener(_ => { if (InputCompat.KeyDown(KeyCode.Return) || InputCompat.KeyDown(KeyCode.KeypadEnter)) DoSearch(); });
            var go = UIKit.NewButton(row, "Go", searching ? "..." : "SEARCH", UITheme.Accent, DoSearch, UITheme.FontCaption + 2);
            UIKit.SetLayout(go.gameObject, preferredWidth: 170f, flexibleWidth: 0f);
            go.SetInteractable(!searching);

            UIWidgets.Paragraph(ListRoot, searchMessage, UITheme.TextDim, 20);
            foreach (var p in results)
            {
                var player = p;
                string rel = player.relationship == "friend" ? "Already friends" : player.relationship == "request_sent" ? "Request sent"
                    : player.relationship == "request_received" ? "Sent you a request" : player.relationship == "self" ? "This is you" : "";
                var buttons = new List<RowButton> { new RowButton("VIEW", UITheme.Button, () => Ctx.ViewPlayer(player.publicId)) };
                if (player.relationship == "none")
                    buttons.Add(new RowButton("ADD", UITheme.Accent, () => AppServices.Social.SendFriendRequest(player.publicId, r =>
                    {
                        SocialUi.Report(r, "Friend request sent.");
                        if (r.Success) { player.relationship = "request_sent"; Rebuild(); }
                    })));
                SocialUi.Row(ListRoot, player.username, "ID " + player.publicId + "  -  LV " + player.level + (rel.Length > 0 ? "  -  " + rel : ""), UITheme.Text, buttons.ToArray());
            }
        }

        private void DoSearch()
        {
            string q = searchInput != null ? searchInput.text.Trim() : "";
            if (q.Length == 0) return;
            searching = true;
            searchMessage = "Searching...";
            Rebuild();
            AppServices.Social.Search(q, r =>
            {
                searching = false;
                if (r.Ok)
                {
                    results = r.Value.results ?? new List<PlayerBriefDto>();
                    searchMessage = results.Count == 0 ? "No players found." : results.Count + " result(s)";
                }
                else
                {
                    results = new List<PlayerBriefDto>();
                    searchMessage = r.Friendly;
                }
                Rebuild();
            });
        }
    }

    public sealed class PartyScreen : SocialScreen
    {
        public override LobbyScreenId Id => LobbyScreenId.Party;
        public override string Title => "Team / Party";

        protected override void BuildList()
        {
            var snap = AppServices.Social.Snapshot;
            var mode = AppServices.Modes.Selected;
            string myId = AppServices.Profile.Profile.publicId;

            if (!snap.InParty)
            {
                SocialUi.Notice(ListRoot, mode.MaxPartySize > 1
                    ? "You are queueing solo. Invite friends from the Friends screen to team up for " + mode.DisplayName + "."
                    : mode.DisplayName + " is a solo mode. Select Battle Royale to play with a party.", new Color(0.1f, 0.18f, 0.28f, 0.9f));
                var friends = UIKit.NewButton(ListRoot, "OpenFriends", "INVITE FRIENDS", UITheme.Accent, () => Ctx.Screens.Open(LobbyScreenId.Friends), UITheme.FontBody);
                UIKit.SetLayout(friends.gameObject, preferredHeight: 72f);
                return;
            }

            var party = snap.Party;
            UIWidgets.SectionTitle(ListRoot, party.modeId.Replace('_', ' ') + "  -  " + party.members.Count + " / " + party.maxSize);

            PartyMemberDto me = null;
            foreach (var m in party.members)
            {
                var member = m;
                if (member.publicId == myId) me = member;
                string tag = member.isLeader ? "LEADER  -  " : "";
                string readyTag = member.ready ? "  -  <color=#5CDB85>READY</color>" : "  -  <color=#FFC742>NOT READY</color>";
                var buttons = member.publicId == myId
                    ? new RowButton[0]
                    : new[] { new RowButton("VIEW", UITheme.Button, () => Ctx.ViewPlayer(member.publicId)) };
                SocialUi.Row(ListRoot, member.username + (member.publicId == myId ? "  (you)" : ""),
                    tag + "LV " + member.level + "  -  " + SocialUi.StatusText(member.status) + readyTag, UITheme.Text, buttons);
            }

            var actions = UIKit.NewRect("Actions", ListRoot);
            UIKit.HStack(actions.gameObject, 8f).childForceExpandWidth = true;
            UIKit.SetLayout(actions.gameObject, preferredHeight: 72f);

            bool amReady = me != null && me.ready;
            var readyBtn = UIKit.NewButton(actions, "Ready", amReady ? "NOT READY" : "READY", amReady ? UITheme.Button : UITheme.Accent,
                () => AppServices.Social.SetReady(!amReady, r => { if (!r.Success) SocialUi.Report(r, ""); }), UITheme.FontBody);
            UIKit.SetLayout(readyBtn.gameObject, minWidth: 0f, flexibleWidth: 1f);
            var invite = UIKit.NewButton(actions, "Invite", "INVITE", UITheme.Button, () => Ctx.Screens.Open(LobbyScreenId.Friends), UITheme.FontBody);
            UIKit.SetLayout(invite.gameObject, minWidth: 0f, flexibleWidth: 1f);
            var leave = UIKit.NewButton(actions, "Leave", "LEAVE", UITheme.Danger, () => Ctx.Confirm("Leave party", "Leave your current party?", "LEAVE",
                () => AppServices.Social.LeaveParty(r => SocialUi.Report(r, "You left the party.")), true), UITheme.FontBody);
            UIKit.SetLayout(leave.gameObject, minWidth: 0f, flexibleWidth: 1f);

            UIWidgets.Paragraph(ListRoot, "Party matchmaking needs the dedicated server, which isn't available yet. Ready status is shared through the backend only.", UITheme.TextDim, 20);
        }
    }

    public sealed class InvitationsScreen : SocialScreen
    {
        public override LobbyScreenId Id => LobbyScreenId.Invitations;
        public override string Title => "Invitations";

        protected override void BuildList()
        {
            var invites = AppServices.Social.Snapshot.Invitations;
            if (invites.Count == 0)
            {
                SocialUi.Notice(ListRoot, "No pending party invitations. Invitations expire after about a minute.", new Color(0.1f, 0.18f, 0.28f, 0.9f));
                return;
            }

            foreach (var inv in invites)
            {
                var i = inv;
                SocialUi.Row(ListRoot, i.fromUsername, "invited you to " + i.modeId.Replace('_', ' '), UITheme.Text,
                    new RowButton("DECLINE", UITheme.Button, () => AppServices.Social.RespondToInvitation(i.id, false, r => SocialUi.Report(r, "Invitation declined."))),
                    new RowButton("ACCEPT", UITheme.Accent, () => AppServices.Social.RespondToInvitation(i.id, true, r =>
                    {
                        SocialUi.Report(r, "You joined the party.");
                        if (r.Success) Ctx.Screens.Open(LobbyScreenId.Party);
                    })));
            }
        }
    }
}
