using System;
using System.Collections.Generic;

namespace Noxy.Core
{
    // Clans, clan wars, companions, map events and fair play. The client only displays what the server reports and
    // sends player intents; every permission check, price, reward and result is decided server-side.

    // ---------- clans ----------
    [Serializable] public sealed class ClanActivityDto { public int messages7d, matches7d, activeMembers7d; }
    [Serializable] public sealed class ClanMemberDto { public string publicId = "", username = "", role = ""; public long joinedAt; public int contributionXp; }
    [Serializable] public sealed class ClanApplicantDto { public int id, level; public string message = "", publicId = "", username = ""; public long createdAt; }
    [Serializable] public sealed class ClanPendingInviteDto { public int id; public string publicId = "", username = ""; public long createdAt; }
    [Serializable] public sealed class ClanDto
    {
        public int id, emblem, emblemColor, level, xp, xpIntoLevel, xpPerLevel, memberCount, memberLimit, wins, losses, draws, rating, rank;
        public string name = "", tag = "", description = "", joinPolicy = "request", myRole = "";
        public bool isMember;
        public ClanActivityDto activity = new ClanActivityDto();
        public List<ClanMemberDto> members = new List<ClanMemberDto>();
        public List<ClanApplicantDto> applications = new List<ClanApplicantDto>();
        public List<ClanPendingInviteDto> invitations = new List<ClanPendingInviteDto>();
    }
    [Serializable] public sealed class ClanInviteDto { public int id, clanId; public string clanName = "", clanTag = "", inviter = ""; public long createdAt; }
    [Serializable] public sealed class ClanApplicationDto { public int id, clanId; public string clanName = "", clanTag = ""; public long createdAt; }
    [Serializable] public sealed class MyClanDto
    {
        public ClanDto clan; // null when the player has no clan
        public List<ClanInviteDto> invitations = new List<ClanInviteDto>();
        public List<ClanApplicationDto> applications = new List<ClanApplicationDto>();
        public int createCostCoins;
    }
    [Serializable] public sealed class ClanListDto { public List<ClanDto> clans = new List<ClanDto>(); public string by = ""; }
    [Serializable] public sealed class ApplyResultDto { public bool joined; public int applicationId; }
    [Serializable] public sealed class ChatMessageDto { public int id; public string text = "", publicId = "", username = ""; public long createdAt; }
    [Serializable] public sealed class ChatDto { public List<ChatMessageDto> messages = new List<ChatMessageDto>(); }


    // ---------- clan wars ----------
    [Serializable] public sealed class WarFormatDto { public string id = ""; public int teamSize, roundLimit, durationSeconds; }
    [Serializable] public sealed class WarEventDto
    {
        public string id = "", title = "", map = "", status = "", matchId = "";
        public int teamSize, roundLimit, durationSeconds, registeredClans;
        public long startsAt, registrationClosesAt;
        public bool registered;
    }
    [Serializable] public sealed class WarQueueDto { public string formatId = ""; public long queuedAt; }
    [Serializable] public sealed class WarSideDto { public int id; public string name = ""; }
    [Serializable] public sealed class WarResultDto { public int winnerClanId, roundsA, roundsB, xpA, xpB, ratingDeltaA, ratingDeltaB; public bool forfeit, rated, reverted; }
    [Serializable] public sealed class WarPlayerDto
    {
        public string publicId = "", username = "", rewardStatus = "";
        public int clanId, kills, deaths, score, points, coins;
        public bool played, abandoned;
    }
    [Serializable] public sealed class WarMatchDto
    {
        public string id = "", status = "", map = "", mySide = "a";
        public int teamSize, roundLimit, durationSeconds;
        public WarSideDto clanA = new WarSideDto(), clanB = new WarSideDto();
        public long createdAt, finishedAt;
        public WarResultDto result; // null until the dedicated server reports a result
        public List<WarPlayerDto> players = new List<WarPlayerDto>();
    }
    [Serializable] public sealed class WarRewardsDto { public int winXp, lossXp, drawXp, winCoins, participationCoins, pairCapPer24h, minMemberAgeHours; }
    [Serializable] public sealed class EligibilityDto { public int score; public string level = ""; public bool competitiveAllowed = true, chatAllowed = true; public List<string> reasons = new List<string>(); }
    [Serializable] public sealed class WarOverviewDto
    {
        public int clanId;
        public string role = "";
        public bool canManage;
        public List<WarFormatDto> formats = new List<WarFormatDto>();
        public List<string> maps = new List<string>();
        public WarRewardsDto rewards = new WarRewardsDto();
        public List<WarEventDto> events = new List<WarEventDto>();
        public WarQueueDto queue; // null when not queued
        public WarMatchDto activeMatch; // null when none
        public List<WarMatchDto> history = new List<WarMatchDto>();
        public EligibilityDto eligibility = new EligibilityDto();
    }
    [Serializable] public sealed class QueueResultDto { public bool queued; public string matchId = ""; }
    [Serializable] public sealed class ContributionDto { public string publicId = "", username = ""; public int matches, kills, deaths, points; }
    [Serializable] public sealed class ContributionsDto { public List<ContributionDto> members = new List<ContributionDto>(); }

    // ---------- companions ----------
    [Serializable] public sealed class CompanionVariantDto { public string id = "", name = ""; public int priceCoins, priceDiamonds; public bool owned; }
    [Serializable] public sealed class CompanionDto
    {
        public string id = "", name = "", kind = "", rarity = "", description = "", unlock = "";
        public int priceCoins, priceDiamonds, minLevel, level, xp, xpPerLevel;
        public bool owned, canUnlock;
        public List<CompanionVariantDto> variants = new List<CompanionVariantDto>();
    }
    [Serializable] public sealed class EquippedCompanionDto { public string companionId = "", variantId = "default"; }
    [Serializable] public sealed class CompanionsDto
    {
        public List<CompanionDto> companions = new List<CompanionDto>();
        public EquippedCompanionDto equipped; // null = none
        public WalletDto wallet; // present after an unlock
    }

    // ---------- map events ----------
    [Serializable] public sealed class MapMarkerDto { public string locationId = ""; public float x, y, z, radius; }
    [Serializable] public sealed class MapRewardDto { public int coins; }
    [Serializable] public sealed class MapProgressDto { public int team, progress; }
    [Serializable] public sealed class MapEventDto
    {
        public string id = "", matchId = "", type = "", title = "", description = "", phase = "", effect = "";
        public long announceAt, startAt, endsAt, serverNow, countdownMs;
        public MapMarkerDto marker = new MapMarkerDto();
        public MapRewardDto reward = new MapRewardDto();
        public int capacity, claimed, remaining, objectiveTarget, winnerTeam;
        public List<MapProgressDto> progress = new List<MapProgressDto>();
    }
    [Serializable] public sealed class MapEventsDto { public long serverNow; public List<MapEventDto> events = new List<MapEventDto>(); }

    // ---------- fair play ----------
    [Serializable] public sealed class FairActionDto { public int id; public string action = "", reason = "", appealStatus = ""; public long createdAt, expiresAt; public bool revoked, canAppeal; }
    [Serializable] public sealed class MyReportDto { public int id; public string category = "", status = "", targetUsername = ""; public long createdAt; }
    [Serializable] public sealed class FairPlayDto
    {
        public int score, abandonsInWindow, confirmedActions, cleanMatches;
        public string level = "";
        public bool competitiveAllowed = true, chatAllowed = true;
        public List<string> reasons = new List<string>();
        public List<FairActionDto> actions = new List<FairActionDto>();
        public List<MyReportDto> myReports = new List<MyReportDto>();
    }
    [Serializable] public sealed class ReputationLevelDto { public string id = "", label = ""; public int min; }
    [Serializable] public sealed class FairRulesDto { public int startScore, cleanMatchBonus, appealWindowDays; public bool reportsNeverAutoPunish; public List<ReputationLevelDto> levels = new List<ReputationLevelDto>(); }
    [Serializable] public sealed class AppealResultDto { public int appealId; public string status = ""; }

    // ---------- dedicated server list ----------
    [Serializable] public sealed class GameServerDto
    {
        public string id = "", name = "", region = "", mode = "", host = "", version = "", status = "";
        public int port, capacity, players, lastSeenSeconds;
    }
    [Serializable] public sealed class ServerListDto { public List<GameServerDto> servers = new List<GameServerDto>(); public long serverNow; }

    public static class ClanRoles
    {
        public static int Rank(string role)
        {
            switch (role)
            {
                case "leader": return 3;
                case "co_leader": return 2;
                case "officer": return 1;
                default: return 0;
            }
        }
        public static string Label(string role)
        {
            switch (role)
            {
                case "leader": return "Leader";
                case "co_leader": return "Co-leader";
                case "officer": return "Officer";
                default: return "Member";
            }
        }
        // These only decide which buttons to show. The server re-checks every action.
        public static bool CanInvite(string role) => Rank(role) >= 1;
        public static bool CanReviewApplications(string role) => Rank(role) >= 1;
        public static bool CanEdit(string role) => Rank(role) >= 2;
        public static bool CanManageWars(string role) => Rank(role) >= 2;
        public static bool CanDisband(string role) => Rank(role) >= 3;
        public static bool CanPromote(string actor, string target) => Rank(actor) >= 2 && Rank(target) < Rank(actor) - (Rank(actor) == 3 ? 0 : 1) && Rank(target) < 2;
        public static bool CanKick(string actor, string target) => Rank(actor) >= 1 && Rank(target) < Rank(actor);
        public static bool CanDemote(string actor, string target) => Rank(actor) >= 2 && Rank(target) > 0 && Rank(target) < Rank(actor);
    }

    public static class CommunityText
    {
        public const string OfflineReason = "Sign in to an account (Settings > Account) to use clans, clan wars, companions and fair play.";
        public const string ReportNotice = "Reports are reviewed by people. Sending many reports about someone does not punish them automatically, and false reports can limit your own reporting.";
        public const string WarNotice = "Clan war results come only from the dedicated game server. Until a game server is connected, matches can be queued but not played.";

        public static string Friendly(string code, string fallback)
        {
            switch (code)
            {
                case "offline": return string.IsNullOrEmpty(fallback) ? OfflineReason : fallback;
                case "name_taken": return "That clan name is already taken.";
                case "tag_taken": return "That clan tag is already taken.";
                case "already_in_clan": return "You are already in a clan.";
                case "not_a_member": return "You are not in that clan.";
                case "insufficient_role": return "Your clan role does not allow that.";
                case "invite_only": return "This clan is invite-only.";
                case "member_too_new": return fallback;
                case "player_restricted": return fallback;
                case "report_limit": return "You have reached today's report limit. Please try again tomorrow.";
                case "muted": return "You are muted from clan chat right now.";
                case "competitive_restricted": return "Competitive play is restricted on your account. See Reputation for details.";
                case "level_too_low": return fallback;
                case "insufficient_funds": return "You do not have enough currency for this.";
                case "already_owned": return "You already own this companion.";
                case "not_owned": return "You do not own that companion yet.";
                case "rate_limited": return "Too many requests. Please wait a moment.";
                case "network": return "No connection. Try again when you are back online.";
                default: return string.IsNullOrEmpty(fallback) ? "Something went wrong." : fallback;
            }
        }

        public static string ReputationLabel(string level)
        {
            switch (level)
            {
                case "trusted": return "Trusted";
                case "good": return "Good standing";
                case "caution": return "Caution";
                case "restricted": return "Restricted";
                default: return level ?? "";
            }
        }

        public static string ReportCategoryLabel(string id)
        {
            switch (id)
            {
                case "abnormal_headshots": return "Abnormal headshots";
                case "aimbot": return "Aimbot";
                case "wallhack": return "Wallhack";
                case "speed_hack": return "Speed hack";
                case "shoots_through_walls": return "Shooting through walls";
                case "walks_through_walls": return "Walking through walls";
                case "abnormal_movement": return "Abnormal movement";
                case "harassment": return "Harassment";
                case "abusive_chat": return "Abusive chat";
                case "offensive_name": return "Offensive username";
                case "afk": return "AFK / inactive";
                case "disruptive": return "Disruptive behaviour";
                default: return "Other";
            }
        }
        public static readonly string[] ReportCategories = { "aimbot", "wallhack", "speed_hack", "abnormal_headshots", "shoots_through_walls", "walks_through_walls", "abnormal_movement", "harassment", "abusive_chat", "offensive_name", "afk", "disruptive", "other" };

        public static string MapEventPhaseLabel(string phase)
        {
            switch (phase)
            {
                case "announced": return "Incoming";
                case "active": return "Active";
                case "completed": return "Completed";
                case "cancelled": return "Cancelled";
                case "expired": return "Ended";
                default: return "Scheduled";
            }
        }

        public static string Countdown(long ms)
        {
            if (ms < 0) ms = 0;
            long s = ms / 1000;
            return (s / 60).ToString("0") + ":" + (s % 60).ToString("00");
        }
    }

    /// <summary>
    /// Keeps map-event countdowns consistent across clients. The server sends absolute timestamps plus its own "now";
    /// the offset between server and local time is measured once per response, so every client renders the same
    /// remaining time regardless of its clock.
    /// </summary>
    public sealed class MapEventClock
    {
        private long offsetMs;
        private bool synced;
        public bool Synced => synced;

        public void Sync(long serverNowMs, long localNowMs) { offsetMs = serverNowMs - localNowMs; synced = true; }
        public long ServerNow(long localNowMs) => localNowMs + offsetMs;

        public string PhaseAt(MapEventDto e, long localNowMs)
        {
            if (e.phase == "completed" || e.phase == "cancelled") return e.phase;
            long now = ServerNow(localNowMs);
            if (now < e.announceAt) return "scheduled";
            if (now < e.startAt) return "announced";
            if (now < e.endsAt) return "active";
            return "expired";
        }

        public long RemainingMs(MapEventDto e, long localNowMs)
        {
            long now = ServerNow(localNowMs);
            switch (PhaseAt(e, localNowMs))
            {
                case "scheduled": return e.announceAt - now;
                case "announced": return e.startAt - now;
                case "active": return e.endsAt - now;
                default: return 0;
            }
        }
    }

    public interface ICommunityService
    {
        bool IsAvailable { get; }
        string UnavailableReason { get; }

        // clans
        void LoadMyClan(Action<ApiResult<MyClanDto>> done);
        void SearchClans(string term, Action<ApiResult<ClanListDto>> done);
        void LoadClanLeaderboard(string by, Action<ApiResult<ClanListDto>> done);
        void CreateClan(string name, string tag, string description, string joinPolicy, Action<ApiResult<ClanDto>> done);
        void UpdateClan(int clanId, string description, string joinPolicy, Action<ApiResult<ClanDto>> done);
        void DisbandClan(int clanId, Action<ApiResult<OkDto>> done);
        void InvitePlayer(int clanId, string publicId, Action<ApiResult<OkDto>> done);
        void RespondInvitation(int invitationId, string action, Action<ApiResult<OkDto>> done);
        void ApplyToClan(int clanId, string message, Action<ApiResult<ApplyResultDto>> done);
        void RespondApplication(int applicationId, string action, Action<ApiResult<OkDto>> done);
        void MemberAction(int clanId, string publicId, string action, Action<ApiResult<OkDto>> done);
        void TransferLeadership(int clanId, string publicId, Action<ApiResult<OkDto>> done);
        void LeaveClan(int clanId, Action<ApiResult<OkDto>> done);
        void LoadChat(int clanId, int afterId, Action<ApiResult<ChatDto>> done);
        void SendChat(int clanId, string text, Action<ApiResult<OkDto>> done);
        void ReportClan(int clanId, string category, string reason, Action<ApiResult<OkDto>> done);

        // clan wars
        void LoadWarOverview(Action<ApiResult<WarOverviewDto>> done);
        void QueueForWar(string formatId, List<string> roster, Action<ApiResult<QueueResultDto>> done);
        void LeaveWarQueue(Action<ApiResult<OkDto>> done);
        void RegisterForEvent(string eventId, List<string> roster, Action<ApiResult<OkDto>> done);
        void UnregisterFromEvent(string eventId, Action<ApiResult<OkDto>> done);
        void LoadWarContributions(Action<ApiResult<ContributionsDto>> done);
        void DisputeWar(string matchId, string reason, Action<ApiResult<OkDto>> done);

        // companions
        void LoadCompanions(Action<ApiResult<CompanionsDto>> done);
        void UnlockCompanion(string id, string variantId, string currency, Action<ApiResult<CompanionsDto>> done);
        void EquipCompanion(string idOrNull, string variantId, Action<ApiResult<CompanionsDto>> done);

        // dedicated servers: only servers the backend has verified as online (fresh heartbeat) are returned
        void LoadServers(string mode, Action<ApiResult<ServerListDto>> done);

        // map events and fair play
        void LoadMapEvents(string matchId, Action<ApiResult<MapEventsDto>> done);
        void LoadFairPlay(Action<ApiResult<FairPlayDto>> done);
        void LoadFairPlayRules(Action<ApiResult<FairRulesDto>> done);
        void ReportPlayer(string publicId, string category, string reason, string evidence, string matchId, Action<ApiResult<OkDto>> done);
        void Appeal(int actionId, string message, Action<ApiResult<AppealResultDto>> done);
    }

    public sealed class OfflineCommunityService : ICommunityService
    {
        public bool IsAvailable => false;
        public string UnavailableReason => CommunityText.OfflineReason;
        private static void Fail<T>(Action<ApiResult<T>> done) where T : class => done?.Invoke(new ApiResult<T> { Ok = false, Code = "offline", Message = CommunityText.OfflineReason });

        public void LoadMyClan(Action<ApiResult<MyClanDto>> done) => Fail(done);
        public void SearchClans(string term, Action<ApiResult<ClanListDto>> done) => Fail(done);
        public void LoadClanLeaderboard(string by, Action<ApiResult<ClanListDto>> done) => Fail(done);
        public void CreateClan(string name, string tag, string description, string joinPolicy, Action<ApiResult<ClanDto>> done) => Fail(done);
        public void UpdateClan(int clanId, string description, string joinPolicy, Action<ApiResult<ClanDto>> done) => Fail(done);
        public void DisbandClan(int clanId, Action<ApiResult<OkDto>> done) => Fail(done);
        public void InvitePlayer(int clanId, string publicId, Action<ApiResult<OkDto>> done) => Fail(done);
        public void RespondInvitation(int invitationId, string action, Action<ApiResult<OkDto>> done) => Fail(done);
        public void ApplyToClan(int clanId, string message, Action<ApiResult<ApplyResultDto>> done) => Fail(done);
        public void RespondApplication(int applicationId, string action, Action<ApiResult<OkDto>> done) => Fail(done);
        public void MemberAction(int clanId, string publicId, string action, Action<ApiResult<OkDto>> done) => Fail(done);
        public void TransferLeadership(int clanId, string publicId, Action<ApiResult<OkDto>> done) => Fail(done);
        public void LeaveClan(int clanId, Action<ApiResult<OkDto>> done) => Fail(done);
        public void LoadChat(int clanId, int afterId, Action<ApiResult<ChatDto>> done) => Fail(done);
        public void SendChat(int clanId, string text, Action<ApiResult<OkDto>> done) => Fail(done);
        public void ReportClan(int clanId, string category, string reason, Action<ApiResult<OkDto>> done) => Fail(done);
        public void LoadWarOverview(Action<ApiResult<WarOverviewDto>> done) => Fail(done);
        public void QueueForWar(string formatId, List<string> roster, Action<ApiResult<QueueResultDto>> done) => Fail(done);
        public void LeaveWarQueue(Action<ApiResult<OkDto>> done) => Fail(done);
        public void RegisterForEvent(string eventId, List<string> roster, Action<ApiResult<OkDto>> done) => Fail(done);
        public void UnregisterFromEvent(string eventId, Action<ApiResult<OkDto>> done) => Fail(done);
        public void LoadWarContributions(Action<ApiResult<ContributionsDto>> done) => Fail(done);
        public void DisputeWar(string matchId, string reason, Action<ApiResult<OkDto>> done) => Fail(done);
        public void LoadCompanions(Action<ApiResult<CompanionsDto>> done) => Fail(done);
        public void UnlockCompanion(string id, string variantId, string currency, Action<ApiResult<CompanionsDto>> done) => Fail(done);
        public void EquipCompanion(string idOrNull, string variantId, Action<ApiResult<CompanionsDto>> done) => Fail(done);
        public void LoadServers(string mode, Action<ApiResult<ServerListDto>> done) => Fail(done);
        public void LoadMapEvents(string matchId, Action<ApiResult<MapEventsDto>> done) => Fail(done);
        public void LoadFairPlay(Action<ApiResult<FairPlayDto>> done) => Fail(done);
        public void LoadFairPlayRules(Action<ApiResult<FairRulesDto>> done) => Fail(done);
        public void ReportPlayer(string publicId, string category, string reason, string evidence, string matchId, Action<ApiResult<OkDto>> done) => Fail(done);
        public void Appeal(int actionId, string message, Action<ApiResult<AppealResultDto>> done) => Fail(done);
    }

    public sealed class RemoteCommunityService : ICommunityService
    {
        private readonly ApiClient api;
        public RemoteCommunityService(ApiClient api) { this.api = api; }

        /// <summary>Set by AccountService while a valid session exists.</summary>
        public bool SignedIn { get; set; }
        /// <summary>Raised after a wallet-changing call (companion unlock) so the lobby header can re-sync.</summary>
        public Action BalanceChanged;

        public bool IsAvailable => SignedIn;
        /// <summary>Why online features are off right now (set by the app from the account state). Falls back to a generic sign-in hint.</summary>
        public Func<string> UnavailableDetail;
        public string UnavailableReason => SignedIn ? "" : (UnavailableDetail?.Invoke() ?? CommunityText.OfflineReason);

        private void Call<T>(string method, string path, Dictionary<string, object> body, Action<ApiResult<T>> done, bool balance = false) where T : class, new()
        {
            if (!SignedIn) { done?.Invoke(new ApiResult<T> { Ok = false, Code = "offline", Message = UnavailableReason }); return; }
            api.Request<T>(method, path, body, r =>
            {
                if (r.Ok && balance) BalanceChanged?.Invoke();
                done?.Invoke(r);
            });
        }
        private static string E(string s) => Uri.EscapeDataString(s ?? "");
        private static Dictionary<string, object> Body(params object[] kv)
        {
            var d = new Dictionary<string, object>();
            for (int i = 0; i + 1 < kv.Length; i += 2) if (kv[i + 1] != null) d[(string)kv[i]] = kv[i + 1];
            return d;
        }

        public void LoadMyClan(Action<ApiResult<MyClanDto>> done) => Call("GET", "/api/clans/me", null, done);
        public void SearchClans(string term, Action<ApiResult<ClanListDto>> done) => Call("GET", "/api/clans/search?q=" + E(term), null, done);
        public void LoadClanLeaderboard(string by, Action<ApiResult<ClanListDto>> done) => Call("GET", "/api/clans/leaderboard?by=" + E(by), null, done);
        public void CreateClan(string name, string tag, string description, string joinPolicy, Action<ApiResult<ClanDto>> done) =>
            Call("POST", "/api/clans", Body("name", name, "tag", tag, "description", description, "joinPolicy", joinPolicy), done);
        public void UpdateClan(int clanId, string description, string joinPolicy, Action<ApiResult<ClanDto>> done) =>
            Call("PATCH", "/api/clans/" + clanId, Body("description", description, "joinPolicy", joinPolicy), done);
        public void DisbandClan(int clanId, Action<ApiResult<OkDto>> done) => Call("DELETE", "/api/clans/" + clanId, null, done);
        public void InvitePlayer(int clanId, string publicId, Action<ApiResult<OkDto>> done) => Call("POST", "/api/clans/" + clanId + "/invitations", Body("publicId", publicId), done);
        public void RespondInvitation(int invitationId, string action, Action<ApiResult<OkDto>> done) => Call("POST", "/api/clans/invitations/" + invitationId + "/" + action, null, done);
        public void ApplyToClan(int clanId, string message, Action<ApiResult<ApplyResultDto>> done) => Call("POST", "/api/clans/" + clanId + "/applications", Body("message", message), done);
        public void RespondApplication(int applicationId, string action, Action<ApiResult<OkDto>> done) => Call("POST", "/api/clans/applications/" + applicationId + "/" + action, null, done);
        public void MemberAction(int clanId, string publicId, string action, Action<ApiResult<OkDto>> done) => Call("POST", "/api/clans/" + clanId + "/members/" + E(publicId) + "/" + action, null, done);
        public void TransferLeadership(int clanId, string publicId, Action<ApiResult<OkDto>> done) => Call("POST", "/api/clans/" + clanId + "/transfer", Body("publicId", publicId), done);
        public void LeaveClan(int clanId, Action<ApiResult<OkDto>> done) => Call("POST", "/api/clans/" + clanId + "/leave", null, done);
        public void LoadChat(int clanId, int afterId, Action<ApiResult<ChatDto>> done) => Call("GET", "/api/clans/" + clanId + "/chat?after=" + afterId, null, done);
        public void SendChat(int clanId, string text, Action<ApiResult<OkDto>> done) => Call("POST", "/api/clans/" + clanId + "/chat", Body("text", text), done);
        public void ReportClan(int clanId, string category, string reason, Action<ApiResult<OkDto>> done) => Call("POST", "/api/clans/" + clanId + "/report", Body("category", category, "reason", reason), done);

        public void LoadWarOverview(Action<ApiResult<WarOverviewDto>> done) => Call("GET", "/api/clanwars/overview", null, done);
        public void QueueForWar(string formatId, List<string> roster, Action<ApiResult<QueueResultDto>> done) =>
            Call("POST", "/api/clanwars/queue", Body("formatId", formatId, "players", new List<object>(roster ?? new List<string>())), done);
        public void LeaveWarQueue(Action<ApiResult<OkDto>> done) => Call("DELETE", "/api/clanwars/queue", null, done);
        public void RegisterForEvent(string eventId, List<string> roster, Action<ApiResult<OkDto>> done) =>
            Call("POST", "/api/clanwars/events/" + E(eventId) + "/register", Body("players", new List<object>(roster ?? new List<string>())), done);
        public void UnregisterFromEvent(string eventId, Action<ApiResult<OkDto>> done) => Call("DELETE", "/api/clanwars/events/" + E(eventId) + "/register", null, done);
        public void LoadWarContributions(Action<ApiResult<ContributionsDto>> done) => Call("GET", "/api/clanwars/contributions", null, done);
        public void DisputeWar(string matchId, string reason, Action<ApiResult<OkDto>> done) => Call("POST", "/api/clanwars/matches/" + E(matchId) + "/dispute", Body("reason", reason), done);

        public void LoadCompanions(Action<ApiResult<CompanionsDto>> done) => Call("GET", "/api/companions", null, done);
        public void UnlockCompanion(string id, string variantId, string currency, Action<ApiResult<CompanionsDto>> done) =>
            Call("POST", "/api/companions/" + E(id) + "/unlock", Body("variantId", variantId, "currency", currency), done, true);
        public void EquipCompanion(string idOrNull, string variantId, Action<ApiResult<CompanionsDto>> done)
        {
            var body = new Dictionary<string, object>();
            if (string.IsNullOrEmpty(idOrNull)) body["companionId"] = null;
            else { body["companionId"] = idOrNull; body["variantId"] = string.IsNullOrEmpty(variantId) ? "default" : variantId; }
            Call("PUT", "/api/me/companion", body, done);
        }

        public void LoadServers(string mode, Action<ApiResult<ServerListDto>> done) =>
            Call("GET", "/api/servers" + (string.IsNullOrEmpty(mode) ? "" : "?mode=" + E(mode)), null, done);
        public void LoadMapEvents(string matchId, Action<ApiResult<MapEventsDto>> done) => Call("GET", "/api/matches/" + E(matchId) + "/events", null, done);
        public void LoadFairPlay(Action<ApiResult<FairPlayDto>> done) => Call("GET", "/api/fairplay/me", null, done);
        public void LoadFairPlayRules(Action<ApiResult<FairRulesDto>> done) => Call("GET", "/api/fairplay/rules", null, done);
        public void ReportPlayer(string publicId, string category, string reason, string evidence, string matchId, Action<ApiResult<OkDto>> done) =>
            Call("POST", "/api/players/" + E(publicId) + "/report", Body("category", category, "reason", reason, "evidence", string.IsNullOrEmpty(evidence) ? null : evidence, "matchId", string.IsNullOrEmpty(matchId) ? null : matchId), done);
        public void Appeal(int actionId, string message, Action<ApiResult<AppealResultDto>> done) => Call("POST", "/api/fairplay/appeals", Body("actionId", actionId, "message", message), done);
    }
}
