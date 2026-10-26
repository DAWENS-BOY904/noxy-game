using System;
using System.Collections.Generic;
using Noxy.Core;
using UnityEngine;
using UnityEngine.UI;

namespace Noxy.Lobby
{
    /// <summary>
    /// Shared shell for the clan, clan-war, companion and reputation screens: tab row, optional text inputs, and
    /// loading / empty / error handling. Screens only show server data and send intents; the server decides everything.
    /// </summary>
    public abstract class CommunityScreen : InfoListScreen
    {
        protected struct TabInput
        {
            public string Placeholder, Placeholder2, GoLabel;
            public int MaxLength;
        }

        protected abstract string[] TabNames { get; }
        protected abstract TabInput InputFor(int tab);
        protected abstract void LoadTab();
        protected abstract void OnGo();

        protected int Tab;
        protected bool Busy;
        protected string Error;
        protected InputField Input, Input2;
        protected ButtonView Go;

        private readonly List<ButtonView> tabButtons = new List<ButtonView>();
        private RectTransform inputRow;

        protected static ICommunityService Svc => AppServices.Community;

        protected override bool HasFooter => true;
        protected override float FooterHeight => 150f;

        protected override void BuildFooter(RectTransform footer)
        {
            UIKit.VStack(footer.gameObject, 8f);

            var tabs = UIKit.NewRect("Tabs", footer);
            UIKit.SetLayout(tabs.gameObject, preferredHeight: 62f);
            UIKit.HStack(tabs.gameObject, 8f);
            for (int i = 0; i < TabNames.Length; i++)
            {
                int index = i;
                var b = UIKit.NewButton(tabs, "Tab" + i, TabNames[i], UITheme.Button, () => SetTab(index), UITheme.FontCaption + 2);
                UIKit.SetLayout(b.gameObject, flexibleWidth: 1f, preferredHeight: 62f);
                tabButtons.Add(b);
            }

            inputRow = UIKit.NewRect("InputRow", footer);
            UIKit.SetLayout(inputRow.gameObject, preferredHeight: 64f);
            UIKit.HStack(inputRow.gameObject, 8f);
            Input = UIForm.NewInput(inputRow, "Input", "", InputField.ContentType.Standard, 80);
            UIKit.SetLayout(Input.gameObject, minWidth: 0f, flexibleWidth: 1f);
            Input2 = UIForm.NewInput(inputRow, "Input2", "", InputField.ContentType.Standard, 80);
            UIKit.SetLayout(Input2.gameObject, minWidth: 0f, flexibleWidth: 1f);
            Go = UIKit.NewButton(inputRow, "Go", "GO", UITheme.Accent, () => { if (!Busy) OnGo(); }, UITheme.FontBody);
            UIKit.SetLayout(Go.gameObject, minWidth: 150f, preferredHeight: 64f);
        }

        protected override void OnShown() => SetTab(Tab);

        protected void SetTab(int index)
        {
            Tab = index;
            Error = null;
            for (int i = 0; i < tabButtons.Count; i++) tabButtons[i].SetColor(i == Tab ? UITheme.Accent : UITheme.Button);
            var spec = InputFor(Tab);
            bool hasRow = spec.GoLabel != null;
            inputRow.gameObject.SetActive(hasRow);
            if (hasRow)
            {
                Input.gameObject.SetActive(spec.Placeholder != null);
                Input2.gameObject.SetActive(spec.Placeholder2 != null);
                Input.text = ""; Input2.text = "";
                Input.characterLimit = spec.MaxLength > 0 ? spec.MaxLength : 80;
                SetPlaceholder(Input, spec.Placeholder);
                SetPlaceholder(Input2, spec.Placeholder2);
                Go.SetLabel(spec.GoLabel);
                Go.gameObject.SetActive(spec.GoLabel != "-");
            }
            if (!Svc.IsAvailable) { Rebuild(); return; }
            LoadTab();
            Rebuild();
        }

        private static void SetPlaceholder(InputField f, string text)
        {
            if (f != null && f.placeholder is Text t) t.text = text ?? "";
        }

        protected string In1 => Input != null ? Input.text.Trim() : "";
        protected string In2 => Input2 != null ? Input2.text.Trim() : "";

        protected bool GuardOffline(out RowData row)
        {
            row = null;
            if (Svc.IsAvailable) return false;
            row = EconomyUi.Notice("Sign in required", Svc.UnavailableReason, UITheme.Warning);
            return true;
        }

        protected RowData Status() =>
            Error != null ? EconomyUi.Notice("Couldn't load", Error, UITheme.Danger) : EconomyUi.Notice("Loading...", "", UITheme.TextDim);

        /// <summary>Standard completion handler: success toast or a friendly warning, then refresh.</summary>
        protected void Finish<T>(ApiResult<T> r, string okTitle, string okMessage, Action refresh = null) where T : class
        {
            Busy = false;
            if (r.Ok) AppServices.Notifications.Post(okTitle, okMessage ?? "", NotificationSeverity.Success);
            else AppServices.Notifications.Post("Can't do that", CommunityText.Friendly(r.Code, r.Friendly), NotificationSeverity.Warning);
            (refresh ?? LoadTab)();
            Rebuild();
        }

        protected void Load<T>(ApiResult<T> r, Action<T> store) where T : class
        {
            if (r.Ok) { Error = null; store(r.Value); }
            else Error = CommunityText.Friendly(r.Code, r.Friendly);
            Rebuild();
        }

        protected static string When(long ms) => ms > 0 ? EconomyUi.FormatTime(ms) : "";
    }

    // ------------------------------------------------------------------ clans
    public sealed class ClansScreen : CommunityScreen
    {
        public override LobbyScreenId Id => LobbyScreenId.Clans;
        public override string Title => "Clans";
        protected override string Banner => "Clan membership and roles are stored on the server, which re-checks every action. Chat is moderated and reportable.";
        protected override string[] TabNames => new[] { "MY CLAN", "FIND", "LEADERS", "CHAT", "CREATE" };

        private MyClanDto mine;
        private ClanListDto found, leaders;
        private ChatDto chat;
        private string selectedMember;
        private int leaderSort;
        private static readonly string[] Sorts = { "xp", "wins", "rating" };
        private bool searched;

        protected override TabInput InputFor(int tab)
        {
            switch (tab)
            {
                case 0: return new TabInput { Placeholder = "Player ID to invite", GoLabel = "INVITE", MaxLength = 12 };
                case 1: return new TabInput { Placeholder = "Clan name or tag", GoLabel = "SEARCH", MaxLength = 24 };
                case 2: return new TabInput { GoLabel = "SORT: " + Sorts[leaderSort].ToUpperInvariant() };
                case 3: return new TabInput { Placeholder = "Message to your clan", GoLabel = "SEND", MaxLength = 200 };
                default: return new TabInput { Placeholder = "Clan name", Placeholder2 = "TAG", GoLabel = "CREATE", MaxLength = 24 };
            }
        }

        protected override void LoadTab()
        {
            Error = null;
            switch (Tab)
            {
                case 0:
                case 4: Svc.LoadMyClan(r => Load(r, v => mine = v)); break;
                case 1: Svc.SearchClans(searched ? In1 : "", r => Load(r, v => found = v)); searched = true; break;
                case 2: Svc.LoadClanLeaderboard(Sorts[leaderSort], r => Load(r, v => leaders = v)); break;
                case 3:
                    Svc.LoadMyClan(r =>
                    {
                        if (r.Ok) mine = r.Value;
                        if (r.Ok && r.Value.clan != null) Svc.LoadChat(r.Value.clan.id, 0, c => Load(c, v => chat = v));
                        else Load(r, v => mine = v);
                    });
                    break;
            }
        }

        protected override void OnGo()
        {
            int clanId = mine != null && mine.clan != null ? mine.clan.id : 0;
            switch (Tab)
            {
                case 0:
                    if (clanId == 0 || string.IsNullOrEmpty(In1)) return;
                    Busy = true;
                    Svc.InvitePlayer(clanId, In1, r => Finish(r, "Invitation sent", "They can accept it from their Clans screen."));
                    break;
                case 1: Svc.SearchClans(In1, r => Load(r, v => found = v)); break;
                case 2:
                    leaderSort = (leaderSort + 1) % Sorts.Length;
                    Go.SetLabel("SORT: " + Sorts[leaderSort].ToUpperInvariant());
                    LoadTab();
                    break;
                case 3:
                    if (clanId == 0 || string.IsNullOrEmpty(In1)) return;
                    Busy = true;
                    string text = In1;
                    Input.text = "";
                    Svc.SendChat(clanId, text, r => Finish(r, "Sent", null));
                    break;
                default:
                    if (string.IsNullOrEmpty(In1) || string.IsNullOrEmpty(In2)) { AppServices.Notifications.Post("Missing details", "Enter a clan name and a tag.", NotificationSeverity.Warning); return; }
                    Busy = true;
                    Svc.CreateClan(In1, In2.ToUpperInvariant(), "", "request", r =>
                    {
                        Finish(r, "Clan created", r.Ok ? r.Value.name + " [" + r.Value.tag + "]" : null, () => { });
                        if (r.Ok) SetTab(0);
                    });
                    break;
            }
        }

        protected override IEnumerable<RowData> GetRows()
        {
            if (GuardOffline(out var offline)) { yield return offline; yield break; }
            switch (Tab)
            {
                case 0: foreach (var r in MineRows()) yield return r; break;
                case 1:
                    if (found == null) { yield return Status(); break; }
                    if (found.clans.Count == 0) yield return EconomyUi.Notice("No clans found", "Try a different name or tag.", UITheme.TextDim);
                    foreach (var c in found.clans) yield return ClanRow(c, true);
                    break;
                case 2:
                    if (leaders == null) { yield return Status(); break; }
                    foreach (var c in leaders.clans) yield return ClanRow(c, false);
                    break;
                case 3: foreach (var r in ChatRows()) yield return r; break;
                default:
                    yield return EconomyUi.Notice("Create a clan", "Costs " + (mine != null ? mine.createCostCoins.ToString("N0") : "?") + " coins. Names and tags must be unique. Type a name and a short tag below.", UITheme.Accent);
                    yield return EconomyUi.Notice("Rules", "Keep names respectful. Offensive names are reportable and can be removed by moderators.", UITheme.TextDim);
                    break;
            }
        }

        private RowData ClanRow(ClanDto c, bool allowJoin)
        {
            var row = new RowData
            {
                Title = (c.rank > 0 ? "#" + c.rank + "  " : "") + c.name + " [" + c.tag + "]",
                Subtitle = "Lv " + c.level + "  -  " + c.memberCount + "/" + c.memberLimit + " members  -  " + c.wins + " wins  -  " + c.xp.ToString("N0") + " XP",
            };
            if (allowJoin)
            {
                bool inviteOnly = c.joinPolicy == "invite";
                bool full = c.memberCount >= c.memberLimit;
                bool inClan = mine != null && mine.clan != null;
                row.ActionLabel = inviteOnly ? "INVITE ONLY" : (full ? "FULL" : (c.joinPolicy == "open" ? "JOIN" : "APPLY"));
                row.ActionEnabled = !inviteOnly && !full && !inClan && !Busy;
                var clan = c;
                row.Action = () => { Busy = true; Svc.ApplyToClan(clan.id, "", r => Finish(r, r.Ok && r.Value.joined ? "Joined clan" : "Application sent", clan.name, () => { })); };
            }
            return row;
        }

        private IEnumerable<RowData> MineRows()
        {
            if (mine == null) { yield return Status(); yield break; }

            foreach (var inv in mine.invitations)
            {
                var i = inv;
                yield return new RowData
                {
                    Title = "Invitation: " + i.clanName + " [" + i.clanTag + "]", Subtitle = "From " + i.inviter,
                    ActionLabel = "ACCEPT", ActionEnabled = !Busy && mine.clan == null,
                    Action = () => { Busy = true; Svc.RespondInvitation(i.id, "accept", r => Finish(r, "Joined clan", i.clanName)); }
                };
                yield return new RowData
                {
                    Title = "Decline " + i.clanName, TitleColor = UITheme.TextDim, ActionLabel = "DECLINE", ActionEnabled = !Busy,
                    Action = () => { Busy = true; Svc.RespondInvitation(i.id, "reject", r => Finish(r, "Declined", null)); }
                };
            }
            foreach (var app in mine.applications)
                yield return EconomyUi.Notice("Application pending: " + app.clanName + " [" + app.clanTag + "]", "Waiting for an officer to review it.", UITheme.TextDim);

            var clan = mine.clan;
            if (clan == null)
            {
                yield return EconomyUi.Notice("You're not in a clan", "Use FIND to apply to one or CREATE to start your own.", UITheme.Accent);
                yield break;
            }

            string role = clan.myRole;
            yield return new RowData
            {
                Title = clan.name + " [" + clan.tag + "]  -  Lv " + clan.level,
                Subtitle = ClanRoles.Label(role) + "  -  " + clan.memberCount + "/" + clan.memberLimit + " members  -  " + clan.joinPolicy + " join",
                Progress = clan.xpPerLevel > 0 ? Mathf.Clamp01((float)clan.xpIntoLevel / clan.xpPerLevel) : 0f, TitleColor = UITheme.Accent
            };
            yield return EconomyUi.Notice("Statistics",
                clan.wins + "W / " + clan.losses + "L / " + clan.draws + "D  -  rating " + clan.rating + "  -  last 7 days: " + clan.activity.activeMembers7d + " active, " + clan.activity.matches7d + " wars, " + clan.activity.messages7d + " messages", UITheme.Text);
            if (!string.IsNullOrEmpty(clan.description)) yield return EconomyUi.Notice("About", clan.description, UITheme.TextDim);

            foreach (var m in clan.members)
            {
                var member = m;
                bool canManage = ClanRoles.CanKick(role, member.role) || ClanRoles.CanPromote(role, member.role) || ClanRoles.CanDemote(role, member.role);
                yield return new RowData
                {
                    Title = member.username + "  (" + ClanRoles.Label(member.role) + ")",
                    Subtitle = "ID " + member.publicId + "  -  " + member.contributionXp.ToString("N0") + " clan XP",
                    ActionLabel = canManage ? (selectedMember == member.publicId ? "CLOSE" : "MANAGE") : null, ActionEnabled = !Busy,
                    Action = () => { selectedMember = selectedMember == member.publicId ? null : member.publicId; Rebuild(); }
                };
                if (selectedMember != member.publicId) continue;
                if (ClanRoles.CanPromote(role, member.role)) yield return MemberAct(clan, member, "promote", "Promote");
                if (ClanRoles.CanDemote(role, member.role)) yield return MemberAct(clan, member, "demote", "Demote");
                if (ClanRoles.CanKick(role, member.role)) yield return MemberAct(clan, member, "kick", "Remove from clan");
                if (ClanRoles.CanDisband(role))
                    yield return new RowData
                    {
                        Title = "Transfer leadership to " + member.username, TitleColor = UITheme.Warning, ActionLabel = "TRANSFER", ActionEnabled = !Busy,
                        Action = () => Ctx.Confirm("Transfer leadership", "You will become a co-leader. This cannot be undone by you.", "TRANSFER",
                            () => { Busy = true; Svc.TransferLeadership(clan.id, member.publicId, r => Finish(r, "Leadership transferred", member.username)); }, true)
                    };
            }

            if (ClanRoles.CanReviewApplications(role))
                foreach (var a in clan.applications)
                {
                    var app = a;
                    yield return new RowData
                    {
                        Title = "Applicant: " + app.username + " (Lv " + app.level + ")", Subtitle = string.IsNullOrEmpty(app.message) ? "No message" : app.message,
                        ActionLabel = "ACCEPT", ActionEnabled = !Busy,
                        Action = () => { Busy = true; Svc.RespondApplication(app.id, "accept", r => Finish(r, "Member added", app.username)); }
                    };
                    yield return new RowData
                    {
                        Title = "Reject " + app.username, TitleColor = UITheme.TextDim, ActionLabel = "REJECT", ActionEnabled = !Busy,
                        Action = () => { Busy = true; Svc.RespondApplication(app.id, "reject", r => Finish(r, "Rejected", null)); }
                    };
                }

            if (ClanRoles.CanEdit(role))
                yield return new RowData
                {
                    Title = "Set description", Subtitle = "Type it in the box below (use the INVITE box), then press this.", ActionLabel = "SAVE", ActionEnabled = !Busy,
                    Action = () => { if (string.IsNullOrEmpty(In1)) return; Busy = true; Svc.UpdateClan(clan.id, In1, null, r => Finish(r, "Clan updated", null)); }
                };

            yield return new RowData
            {
                Title = "Report this clan", Subtitle = "Type a reason in the box below first. Moderators review reports.", TitleColor = UITheme.TextDim, ActionLabel = "REPORT", ActionEnabled = !Busy,
                Action = () => { if (In1.Length < 5) { AppServices.Notifications.Post("Add a reason", "Type at least 5 characters.", NotificationSeverity.Warning); return; } Busy = true; Svc.ReportClan(clan.id, "other", In1, r => Finish(r, "Report sent", "Thanks. A moderator will review it.", () => { })); }
            };

            if (!ClanRoles.CanDisband(role))
                yield return new RowData
                {
                    Title = "Leave clan", TitleColor = UITheme.Warning, ActionLabel = "LEAVE", ActionEnabled = !Busy,
                    Action = () => Ctx.Confirm("Leave clan", "Leave " + clan.name + "?", "LEAVE", () => { Busy = true; Svc.LeaveClan(clan.id, r => Finish(r, "Left clan", null)); }, true)
                };
            else
                yield return new RowData
                {
                    Title = "Disband clan", Subtitle = "Permanently deletes the clan. Transfer leadership instead to keep it.", TitleColor = UITheme.Danger, ActionLabel = "DISBAND", ActionEnabled = !Busy,
                    Action = () => Ctx.Confirm("Disband clan", "This permanently deletes " + clan.name + " for all members.", "DISBAND", () => { Busy = true; Svc.DisbandClan(clan.id, r => Finish(r, "Clan disbanded", null)); }, true)
                };
        }

        private RowData MemberAct(ClanDto clan, ClanMemberDto member, string action, string label)
        {
            bool destructive = action == "kick";
            return new RowData
            {
                Title = "  " + label + " " + member.username, TitleColor = destructive ? UITheme.Danger : UITheme.Text, ActionLabel = label.ToUpperInvariant().Split(' ')[0], ActionEnabled = !Busy,
                Action = () => Ctx.Confirm(label, label + " " + member.username + "?", label.ToUpperInvariant().Split(' ')[0],
                    () => { Busy = true; selectedMember = null; Svc.MemberAction(clan.id, member.publicId, action, r => Finish(r, "Done", member.username)); }, destructive)
            };
        }

        private IEnumerable<RowData> ChatRows()
        {
            if (mine != null && mine.clan == null) { yield return EconomyUi.Notice("No clan chat", "Join a clan to chat with its members.", UITheme.TextDim); yield break; }
            if (chat == null) { yield return Status(); yield break; }
            yield return new RowData { Title = "Clan chat", Subtitle = "Messages from players you blocked are hidden. Be respectful.", TitleColor = UITheme.Accent, ActionLabel = "REFRESH", ActionEnabled = !Busy, Action = LoadTab };
            if (chat.messages.Count == 0) yield return EconomyUi.Notice("No messages yet", "Say hello!", UITheme.TextDim);
            for (int i = chat.messages.Count - 1; i >= 0; i--)
            {
                var m = chat.messages[i];
                yield return new RowData { Title = m.username, Subtitle = m.text, RightText = When(m.createdAt) };
            }
        }
    }

    // -------------------------------------------------------------- clan wars
    public sealed class ClanWarsScreen : CommunityScreen
    {
        public override LobbyScreenId Id => LobbyScreenId.ClanWars;
        public override string Title => "Clan Wars";
        protected override string Banner => CommunityText.WarNotice;
        protected override string[] TabNames => new[] { "OVERVIEW", "QUEUE", "HISTORY", "STATS" };

        private WarOverviewDto war;
        private ClanDto clan;
        private ContributionsDto stats;
        private string formatId;
        private readonly HashSet<string> roster = new HashSet<string>();

        protected override TabInput InputFor(int tab) =>
            tab == 2 ? new TabInput { Placeholder = "Dispute reason", GoLabel = "-", MaxLength = 200 } : new TabInput();

        protected override void LoadTab()
        {
            Error = null;
            Svc.LoadMyClan(c => { if (c.Ok) clan = c.Value.clan; });
            Svc.LoadWarOverview(r => Load(r, v => { war = v; if (formatId == null && v.formats.Count > 0) formatId = v.formats[0].id; }));
            if (Tab == 3) Svc.LoadWarContributions(r => Load(r, v => stats = v));
        }

        protected override void OnGo() { }

        private WarFormatDto Format() => war?.formats.Find(f => f.id == formatId);

        protected override IEnumerable<RowData> GetRows()
        {
            if (GuardOffline(out var offline)) { yield return offline; yield break; }
            if (war == null) { yield return Status(); yield break; }
            if (war.clanId == 0) { yield return EconomyUi.Notice("Join a clan first", "Clan wars are fought by clans. Open Clans to join or create one.", UITheme.Warning); yield break; }
            if (!war.eligibility.competitiveAllowed)
                yield return EconomyUi.Notice("Competitive play restricted", string.Join(" ", war.eligibility.reasons), UITheme.Danger);

            switch (Tab)
            {
                case 0: foreach (var r in Overview()) yield return r; break;
                case 1: foreach (var r in QueueRows()) yield return r; break;
                case 2: foreach (var r in HistoryRows()) yield return r; break;
                default:
                    if (stats == null) { yield return Status(); break; }
                    if (stats.members.Count == 0) yield return EconomyUi.Notice("No contribution data yet", "Stats appear after verified clan war matches.", UITheme.TextDim);
                    foreach (var m in stats.members)
                        yield return new RowData { Title = m.username, Subtitle = m.matches + " matches  -  " + m.kills + " K / " + m.deaths + " D  -  " + m.points + " pts" };
                    break;
            }
        }

        private IEnumerable<RowData> Overview()
        {
            var rw = war.rewards;
            yield return EconomyUi.Notice("Rewards (set by the server)", "Win +" + rw.winXp + " clan XP, loss +" + rw.lossXp + ". Coins " + rw.winCoins + " + " + rw.participationCoins + " for verified results. Repeat pairings are capped to stop farming.", UITheme.Accent);
            if (war.activeMatch != null)
                yield return new RowData { Title = "Match " + war.activeMatch.status.ToUpperInvariant(), Subtitle = war.activeMatch.clanA.name + " vs " + war.activeMatch.clanB.name + "  -  " + war.activeMatch.map, TitleColor = UITheme.Warning };
            if (war.queue != null) yield return EconomyUi.Notice("In the queue", "Format " + war.queue.formatId + ". You'll be paired with a similar-rated clan.", UITheme.Cyan);
            if (war.events.Count == 0) yield return EconomyUi.Notice("No upcoming events", "Scheduled clan battles appear here when the operator creates them.", UITheme.TextDim);
            foreach (var e in war.events)
            {
                var ev = e;
                yield return new RowData
                {
                    Title = ev.title, Subtitle = ev.teamSize + "v" + ev.teamSize + "  -  " + ev.map + "  -  starts " + When(ev.startsAt) + "  -  " + ev.registeredClans + " clans",
                    ActionLabel = ev.registered ? "WITHDRAW" : "REGISTER", ActionEnabled = war.canManage && !Busy,
                    Action = () =>
                    {
                        Busy = true;
                        if (ev.registered) Svc.UnregisterFromEvent(ev.id, r => Finish(r, "Withdrawn", null));
                        else Svc.RegisterForEvent(ev.id, new List<string>(roster), r => Finish(r, "Registered", ev.title));
                    }
                };
            }
            if (!war.canManage) yield return EconomyUi.Notice("Only co-leaders and leaders can register or queue", "", UITheme.TextDim);
            else yield return EconomyUi.Notice("Roster", "Pick your players on the QUEUE tab first (" + roster.Count + " selected). Events use the same roster.", UITheme.TextDim);
        }

        private IEnumerable<RowData> QueueRows()
        {
            foreach (var f in war.formats)
            {
                var fmt = f;
                yield return new RowData
                {
                    Title = fmt.teamSize + "v" + fmt.teamSize + "  -  " + fmt.roundLimit + " rounds  -  " + fmt.durationSeconds / 60 + " min",
                    TitleColor = fmt.id == formatId ? UITheme.Accent : UITheme.Text,
                    ActionLabel = fmt.id == formatId ? "SELECTED" : "SELECT", ActionEnabled = fmt.id != formatId,
                    Action = () => { formatId = fmt.id; roster.Clear(); Rebuild(); }
                };
            }
            var format = Format();
            if (format == null || clan == null) yield break;
            yield return EconomyUi.Notice("Choose " + format.teamSize + " players", roster.Count + " selected. Only members who have been in the clan long enough can play.", UITheme.Cyan);
            foreach (var m in clan.members)
            {
                var member = m;
                bool picked = roster.Contains(member.publicId);
                yield return new RowData
                {
                    Title = member.username, Subtitle = ClanRoles.Label(member.role), TitleColor = picked ? UITheme.Accent : UITheme.Text,
                    ActionLabel = picked ? "REMOVE" : "ADD", ActionEnabled = war.canManage && (picked || roster.Count < format.teamSize),
                    Action = () => { if (!roster.Add(member.publicId)) roster.Remove(member.publicId); Rebuild(); }
                };
            }
            if (war.queue != null)
                yield return new RowData { Title = "Leave queue", ActionLabel = "LEAVE", ActionEnabled = war.canManage && !Busy, Action = () => { Busy = true; Svc.LeaveWarQueue(r => Finish(r, "Left queue", null)); } };
            else
                yield return new RowData
                {
                    Title = "Join the queue", Subtitle = roster.Count == format.teamSize ? "Roster ready." : "Select exactly " + format.teamSize + " players.", TitleColor = UITheme.Accent,
                    ActionLabel = "QUEUE", ActionEnabled = war.canManage && !Busy && roster.Count == format.teamSize,
                    Action = () => { Busy = true; Svc.QueueForWar(format.id, new List<string>(roster), r => Finish(r, "Queued", "You'll be matched with a similar-rated clan.")); }
                };
        }

        private IEnumerable<RowData> HistoryRows()
        {
            if (war.history.Count == 0) { yield return EconomyUi.Notice("No clan wars yet", "Verified results from the game server appear here.", UITheme.TextDim); yield break; }
            foreach (var h in war.history)
            {
                var m = h;
                string outcome = m.status == "void" ? "VOID (no penalty)" : m.status == "disputed" ? "DISPUTED" : m.result == null ? m.status : (m.result.winnerClanId == war.clanId ? "WIN" : (m.result.winnerClanId == 0 ? "DRAW" : "LOSS"));
                string score = m.result != null ? m.result.roundsA + " - " + m.result.roundsB + "  -  " : "";
                yield return new RowData
                {
                    Title = m.clanA.name + " vs " + m.clanB.name + "   " + outcome, Subtitle = score + m.map + "  -  " + When(m.createdAt),
                    TitleColor = outcome == "WIN" ? UITheme.Success : outcome == "LOSS" ? UITheme.Danger : UITheme.Text,
                    ActionLabel = m.status == "completed" && war.canManage ? "DISPUTE" : null, ActionEnabled = !Busy,
                    Action = () =>
                    {
                        if (In1.Length < 10) { AppServices.Notifications.Post("Add a reason", "Type at least 10 characters in the box first.", NotificationSeverity.Warning); return; }
                        string reason = In1;
                        Ctx.Confirm("Dispute result", "A moderator will review this result. False disputes may limit your account.", "SUBMIT", () => { Busy = true; Svc.DisputeWar(m.id, reason, r => Finish(r, "Dispute submitted", null)); }, true);
                    }
                };
            }
        }
    }

    // -------------------------------------------------------------- companions
    public sealed class CompanionsScreen : CommunityScreen
    {
        public override LobbyScreenId Id => LobbyScreenId.Companions;
        public override string Title => "Companions";
        protected override string Banner => "Companions are cosmetic only. They never block movement, reveal enemies or affect hit detection. Prices come from the server. Placeholder models are used until final art is added.";
        protected override string[] TabNames => new[] { "ALL", "OWNED" };

        private CompanionsDto data;

        protected override TabInput InputFor(int tab) => new TabInput();
        protected override void OnGo() { }
        protected override void LoadTab() => Svc.LoadCompanions(r => Load(r, Apply));

        private void Apply(CompanionsDto v)
        {
            data = v;
            var eq = v.equipped;
            CompanionFollower.Show(Ctx.Preview != null ? Ctx.Preview.transform : null, eq != null && !string.IsNullOrEmpty(eq.companionId) ? eq.companionId : null, eq != null ? eq.variantId : "default");
        }

        protected override void OnHidden() { }

        protected override IEnumerable<RowData> GetRows()
        {
            if (GuardOffline(out var offline)) { yield return offline; yield break; }
            if (data == null) { yield return Status(); yield break; }
            string equippedId = data.equipped != null ? data.equipped.companionId : "";
            string equippedVar = data.equipped != null ? data.equipped.variantId : "";
            int shown = 0;
            foreach (var c in data.companions)
            {
                if (Tab == 1 && !c.owned) continue;
                shown++;
                var comp = c;
                bool isEquipped = c.owned && equippedId == c.id && equippedVar == "default";
                yield return new RowData
                {
                    Title = c.name + "  -  " + Cap(c.rarity) + " " + c.kind,
                    Subtitle = c.owned ? "Companion level " + c.level + "  -  " + c.description : c.description + "  -  " + CostText(c.priceCoins, c.priceDiamonds, c.minLevel),
                    Progress = c.owned && c.xpPerLevel > 0 ? Mathf.Clamp01((float)c.xp / c.xpPerLevel) : -1f,
                    ActionLabel = c.owned ? (isEquipped ? "UNEQUIP" : "EQUIP") : (c.canUnlock ? "UNLOCK" : "LOCKED"),
                    ActionEnabled = !Busy && (c.owned || c.canUnlock),
                    Action = () =>
                    {
                        Busy = true;
                        if (comp.owned) Svc.EquipCompanion(isEquipped ? null : comp.id, "default", r => EquipDone(r, isEquipped ? "Companion unequipped" : comp.name + " equipped"));
                        else Ctx.Confirm("Unlock " + comp.name, CostText(comp.priceCoins, comp.priceDiamonds, 0), "UNLOCK", () => Unlock(comp, "default"));
                        if (!comp.owned) Busy = false;
                    }
                };
                if (!c.owned) continue;
                foreach (var v in c.variants)
                {
                    var variant = v;
                    bool varEquipped = equippedId == c.id && equippedVar == v.id;
                    yield return new RowData
                    {
                        Title = "    " + v.name, Subtitle = v.owned ? "Variant" : CostText(v.priceCoins, v.priceDiamonds, 0),
                        ActionLabel = v.owned ? (varEquipped ? "EQUIPPED" : "EQUIP") : "UNLOCK", ActionEnabled = !Busy && !varEquipped,
                        Action = () =>
                        {
                            if (variant.owned) { Busy = true; Svc.EquipCompanion(comp.id, variant.id, r => EquipDone(r, variant.name + " equipped")); }
                            else Ctx.Confirm("Unlock " + variant.name, CostText(variant.priceCoins, variant.priceDiamonds, 0), "UNLOCK", () => Unlock(comp, variant.id));
                        }
                    };
                }
            }
            if (shown == 0) yield return EconomyUi.Notice("No companions here yet", Tab == 1 ? "Unlock one from the ALL tab." : "The catalog is empty.", UITheme.TextDim);
        }

        private void Unlock(CompanionDto c, string variantId)
        {
            Busy = true;
            string currency = c.priceCoins > 0 ? "coins" : "diamonds";
            if (variantId != "default") { var v = c.variants.Find(x => x.id == variantId); if (v != null) currency = v.priceCoins > 0 ? "coins" : "diamonds"; }
            Svc.UnlockCompanion(c.id, variantId, currency, r =>
            {
                if (r.Ok) Apply(r.Value);
                Finish(r, "Unlocked", c.name, () => { });
            });
        }

        private void EquipDone(ApiResult<CompanionsDto> r, string message)
        {
            if (r.Ok) Apply(r.Value);
            Finish(r, "Done", message, () => { });
        }

        private static string Cap(string s) => string.IsNullOrEmpty(s) ? s : char.ToUpperInvariant(s[0]) + s.Substring(1);

        private static string CostText(int coins, int diamonds, int minLevel)
        {
            string cost = coins > 0 ? coins.ToString("N0") + " coins" : diamonds > 0 ? diamonds.ToString("N0") + " diamonds" : "Free";
            return cost + (minLevel > 1 ? "  -  needs level " + minLevel : "");
        }
    }

    // -------------------------------------------------------------- reputation
    public sealed class ReputationScreen : CommunityScreen
    {
        public override LobbyScreenId Id => LobbyScreenId.Reputation;
        public override string Title => "Reputation & Reports";
        protected override string Banner => CommunityText.ReportNotice;
        protected override string[] TabNames => new[] { "STANDING", "REPORT", "APPEALS" };

        private FairPlayDto fair;
        private FairRulesDto rules;
        private string category = "aimbot";

        protected override TabInput InputFor(int tab)
        {
            if (tab == 1) return new TabInput { Placeholder = "Player ID", Placeholder2 = "What happened?", GoLabel = "SEND", MaxLength = 200 };
            if (tab == 2) return new TabInput { Placeholder = "Appeal message (10+ characters)", GoLabel = "-", MaxLength = 300 };
            return new TabInput();
        }

        protected override void LoadTab()
        {
            Svc.LoadFairPlay(r => Load(r, v => fair = v));
            if (rules == null) Svc.LoadFairPlayRules(r => { if (r.Ok) { rules = r.Value; Rebuild(); } });
        }

        protected override void OnGo()
        {
            if (Tab != 1) return;
            if (In1.Length < 8 || In2.Length < 5) { AppServices.Notifications.Post("Missing details", "Enter the player's ID and describe what happened.", NotificationSeverity.Warning); return; }
            string id = In1, reason = In2, cat = category;
            Ctx.Confirm("Send report", "Report " + id + " for " + CommunityText.ReportCategoryLabel(cat) + "? False reports can limit your reporting.", "SEND", () =>
            {
                Busy = true;
                Svc.ReportPlayer(id, cat, reason, null, null, r => Finish(r, "Report sent", "Thanks. A moderator will review it. Nobody is punished automatically."));
            });
        }

        protected override IEnumerable<RowData> GetRows()
        {
            if (GuardOffline(out var offline)) { yield return offline; yield break; }
            if (fair == null) { yield return Status(); yield break; }
            switch (Tab)
            {
                case 0:
                    yield return new RowData
                    {
                        Title = CommunityText.ReputationLabel(fair.level) + "  -  score " + fair.score, TitleColor = fair.competitiveAllowed ? UITheme.Success : UITheme.Danger,
                        Subtitle = "Competitive play: " + (fair.competitiveAllowed ? "allowed" : "restricted") + "  -  Chat: " + (fair.chatAllowed ? "allowed" : "muted")
                    };
                    foreach (var reason in fair.reasons) yield return EconomyUi.Notice("Why", reason, UITheme.Warning);
                    yield return EconomyUi.Notice("Indicators", fair.cleanMatches + " clean matches  -  " + fair.abandonsInWindow + " abandons recently  -  " + fair.confirmedActions + " confirmed moderator actions", UITheme.Text);
                    if (rules != null)
                    {
                        yield return EconomyUi.Notice("Transparent rules", "Start at " + rules.startScore + ". Clean matches add +" + rules.cleanMatchBonus + ". Reports alone never reduce your score; only a moderator's confirmed action does. Appeals open for " + rules.appealWindowDays + " days.", UITheme.TextDim);
                        foreach (var l in rules.levels) yield return EconomyUi.Notice(CommunityText.ReputationLabel(l.id) + " (from " + l.min + ")", l.label, UITheme.TextDim);
                    }
                    break;
                case 1:
                    foreach (var c in CommunityText.ReportCategories)
                    {
                        string cat = c;
                        yield return new RowData
                        {
                            Title = CommunityText.ReportCategoryLabel(c), TitleColor = c == category ? UITheme.Accent : UITheme.Text,
                            ActionLabel = c == category ? "SELECTED" : "CHOOSE", ActionEnabled = c != category, Action = () => { category = cat; Rebuild(); }
                        };
                    }
                    foreach (var r in fair.myReports)
                        yield return new RowData { Title = "Your report: " + CommunityText.ReportCategoryLabel(r.category), Subtitle = r.targetUsername + "  -  " + r.status, RightText = When(r.createdAt), TitleColor = UITheme.TextDim };
                    break;
                default:
                    if (fair.actions.Count == 0) { yield return EconomyUi.Notice("No moderator actions", "Nothing on your account needs an appeal.", UITheme.Success); break; }
                    foreach (var a in fair.actions)
                    {
                        var act = a;
                        yield return new RowData
                        {
                            Title = Cap(a.action) + (a.revoked ? " (lifted)" : ""), Subtitle = (string.IsNullOrEmpty(a.reason) ? "" : a.reason + "  -  ") + When(a.createdAt) + (string.IsNullOrEmpty(a.appealStatus) ? "" : "  -  appeal " + a.appealStatus),
                            ActionLabel = a.canAppeal ? "APPEAL" : null, ActionEnabled = !Busy,
                            Action = () =>
                            {
                                if (In1.Length < 10) { AppServices.Notifications.Post("Explain your appeal", "Type at least 10 characters in the box first.", NotificationSeverity.Warning); return; }
                                Busy = true; Svc.Appeal(act.id, In1, r => Finish(r, "Appeal submitted", "A moderator will review it."));
                            }
                        };
                    }
                    break;
            }
        }

        private static string Cap(string s) => string.IsNullOrEmpty(s) ? s : char.ToUpperInvariant(s[0]) + s.Substring(1);
    }

    // ------------------------------------------------------------ server list
    public sealed class ServersScreen : CommunityScreen
    {
        public override LobbyScreenId Id => LobbyScreenId.Servers;
        public override string Title => "Servers";
        protected override string Banner => "Only servers the backend has verified as online (a heartbeat within the last 30 seconds) are listed. Joining a server is not connected yet, so this list is informational.";
        protected override string[] TabNames => new[] { "ALL", "BATTLE ROYALE", "LONE WOLF" };

        private ServerListDto data;
        private static readonly string[] Modes = { null, "battle_royale", "lone_wolf" };

        protected override TabInput InputFor(int tab) => new TabInput();
        protected override void OnGo() { }
        protected override void LoadTab() { data = null; Svc.LoadServers(Modes[Tab], r => Load(r, v => data = v)); }

        protected override IEnumerable<RowData> GetRows()
        {
            if (GuardOffline(out var offline)) { yield return offline; yield break; }
            if (data == null) { yield return Status(); yield break; }
            if (data.servers.Count == 0)
            {
                yield return EconomyUi.Notice("No servers online", "No dedicated server has reported healthy recently. Nothing is shown unless a server is actually running and sending heartbeats.", UITheme.TextDim);
                yield break;
            }
            foreach (var s in data.servers)
            {
                bool full = s.players >= s.capacity;
                yield return new RowData
                {
                    Title = s.name + "  -  " + s.region,
                    Subtitle = s.mode.Replace('_', ' ') + "  -  " + s.host + ":" + s.port + "  -  seen " + s.lastSeenSeconds + "s ago",
                    RightText = s.players + " / " + s.capacity,
                    RightColor = full ? UITheme.Warning : UITheme.Success
                };
            }
        }
    }
}