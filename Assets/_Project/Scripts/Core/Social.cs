using System;
using System.Collections.Generic;
using UnityEngine;

namespace Noxy.Core
{
    // ---- DTOs mirroring the backend JSON (field names are the wire format; keep them camelCase) ----

    [Serializable] public sealed class LoadoutDto
    {
        public string outfit = "", shoes = "", mask = "", headgear = "", backpack = "", weaponskin = "", emote = "";
    }

    [Serializable] public sealed class StatsDto
    {
        public int matches, wins, top3, kills, deaths, headshots;
        public float hoursPlayed;
    }

    [Serializable] public sealed class PrivacyDto
    {
        public string friendRequests = "everyone", partyInvites = "friends", onlineStatus = "friends";
    }

    [Serializable] public sealed class MeDto
    {
        public string publicId = "", username = "", rank = "Unranked";
        public int level = 1, xp, xpToNext = 500, credits, cores;
        public StatsDto stats = new StatsDto();
        public LoadoutDto loadout = new LoadoutDto();
        public PrivacyDto privacy = new PrivacyDto();
    }

    [Serializable] public sealed class AuthDto
    {
        public string token = "";
        public MeDto player = new MeDto();
    }

    [Serializable] public sealed class PlayerBriefDto
    {
        public string publicId = "", username = "", rank = "", status = "offline", relationship = "none";
        public int level = 1;
    }

    [Serializable] public sealed class PlayerProfileDto
    {
        public string publicId = "", username = "", rank = "", status = "offline", relationship = "none";
        public int level = 1;
        public StatsDto stats = new StatsDto();
        public LoadoutDto loadout = new LoadoutDto();
    }

    [Serializable] public sealed class SearchDto { public List<PlayerBriefDto> results = new List<PlayerBriefDto>(); }

    [Serializable] public sealed class FriendRequestDto
    {
        public int requestId;
        public PlayerBriefDto player = new PlayerBriefDto();
    }

    [Serializable] public sealed class FriendsDto
    {
        public List<PlayerBriefDto> friends = new List<PlayerBriefDto>();
        public List<FriendRequestDto> incoming = new List<FriendRequestDto>();
        public List<FriendRequestDto> outgoing = new List<FriendRequestDto>();
    }

    [Serializable] public sealed class PartyMemberDto
    {
        public string publicId = "", username = "", status = "offline";
        public int level = 1;
        public bool ready, isLeader;
    }

    [Serializable] public sealed class PartyDto
    {
        public int id;
        public string modeId = "", leaderPublicId = "";
        public int maxSize = 1;
        public List<PartyMemberDto> members = new List<PartyMemberDto>();
    }

    [Serializable] public sealed class InvitationDto
    {
        public int id, partyId;
        public string fromPublicId = "", fromUsername = "", modeId = "";
        public long expiresAt;
    }

    [Serializable] public sealed class PartyStateDto
    {
        public PartyDto party;
        public List<InvitationDto> invitations = new List<InvitationDto>();
    }

    [Serializable] public sealed class PartyResultDto { public PartyDto party; }

    [Serializable] public sealed class NotificationDto
    {
        public int id;
        public string kind = "", title = "", body = "";
        public long createdAt;
        public bool read;
    }

    [Serializable] public sealed class NotificationsDto { public List<NotificationDto> notifications = new List<NotificationDto>(); }

    [Serializable] public sealed class InventoryDto
    {
        public List<string> owned = new List<string>();
        public LoadoutDto loadout = new LoadoutDto();
    }

    [Serializable] public sealed class BlockedDto { public string publicId = "", username = ""; }
    [Serializable] public sealed class BlocksDto { public List<BlockedDto> blocked = new List<BlockedDto>(); }
    [Serializable] public sealed class OkDto { public bool ok; }

    [Serializable] public sealed class ErrorBodyDto { public string code = "", message = ""; public int retryAfter; }
    [Serializable] public sealed class ErrorEnvelopeDto { public ErrorBodyDto error = new ErrorBodyDto(); }

    // ---- transport + client ----

    public sealed class HttpResponse
    {
        public int Status;       // 0 = could not reach the server
        public string Body = "";
        public string NetworkError = "";
    }

    /// <summary>
    /// Sends one HTTP request and invokes <paramref name="done"/> exactly once (on the main thread in Unity).
    /// The Unity implementation uses UnityWebRequest; tests use HttpClient.
    /// </summary>
    public interface ITransport
    {
        void Send(string method, string url, string jsonBody, string bearerToken, Action<HttpResponse> done);
    }

    public sealed class ApiResult<T> where T : class
    {
        public bool Ok;
        public T Value;
        public int Status;
        public string Code = "";
        public string Message = "";
        public bool NetworkError;
        public int RetryAfter;

        public string Friendly => Ok ? "" : (string.IsNullOrEmpty(Message) ? "Request failed." : Message);
    }

    /// <summary>Thin typed JSON client. Holds the server URL and bearer token; contains no secrets of its own.</summary>
    public sealed class ApiClient
    {
        private readonly ITransport transport;

        public ApiClient(ITransport transport, string baseUrl)
        {
            this.transport = transport;
            BaseUrl = baseUrl;
        }

        public string BaseUrl { get; set; }
        public string Token { get; set; }

        public void Request<T>(string method, string path, Dictionary<string, object> body, Action<ApiResult<T>> done) where T : class, new()
        {
            string json = body == null ? null : MiniJson.Write(body);
            string url = (BaseUrl ?? "").TrimEnd('/') + path;
            transport.Send(method, url, json, Token, response =>
            {
                var result = new ApiResult<T> { Status = response.Status };
                if (response.Status == 0)
                {
                    result.NetworkError = true;
                    result.Code = "network";
                    result.Message = "Cannot reach the server. Check your connection and the server address.";
                }
                else if (response.Status >= 200 && response.Status < 300)
                {
                    try
                    {
                        result.Value = string.IsNullOrEmpty(response.Body) ? new T() : JsonUtility.FromJson<T>(response.Body) ?? new T();
                        result.Ok = true;
                    }
                    catch (Exception)
                    {
                        result.Code = "bad_response";
                        result.Message = "The server sent an unexpected response.";
                    }
                }
                else
                {
                    result.Code = "http_" + response.Status;
                    result.Message = "Request failed (" + response.Status + ").";
                    try
                    {
                        var env = JsonUtility.FromJson<ErrorEnvelopeDto>(response.Body);
                        if (env?.error != null && !string.IsNullOrEmpty(env.error.code))
                        {
                            result.Code = env.error.code;
                            result.Message = env.error.message;
                            result.RetryAfter = env.error.retryAfter;
                        }
                    }
                    catch (Exception) { /* keep generic message */ }
                }
                done(result);
            });
        }

        /// <summary>For endpoints whose payload has dynamic keys (settings data, HUD layouts).</summary>
        public void RequestRaw(string method, string path, Dictionary<string, object> body, Action<HttpResponse> done)
        {
            transport.Send(method, (BaseUrl ?? "").TrimEnd('/') + path, body == null ? null : MiniJson.Write(body), Token, done);
        }
    }

    // ---- social ----

    public sealed class SocialSnapshot
    {
        public List<PlayerBriefDto> Friends = new List<PlayerBriefDto>();
        public List<FriendRequestDto> Incoming = new List<FriendRequestDto>();
        public List<FriendRequestDto> Outgoing = new List<FriendRequestDto>();
        public List<BlockedDto> Blocked = new List<BlockedDto>();
        public PartyDto Party;                                     // null when not in a party
        public List<InvitationDto> Invitations = new List<InvitationDto>();

        public bool InParty => Party != null && Party.members != null && Party.members.Count > 0;
    }

    /// <summary>
    /// Friends, profiles search, blocking and party invitations. The only implementation that talks to a server is
    /// RemoteSocialService; OfflineSocialService says plainly that these features need an account.
    /// </summary>
    public interface ISocialService
    {
        bool IsAvailable { get; }
        string UnavailableReason { get; }
        SocialSnapshot Snapshot { get; }
        event Action Changed;

        void Refresh(Action<ServiceResult> done = null);
        void Search(string query, Action<ApiResult<SearchDto>> done);
        void GetProfile(string publicId, Action<ApiResult<PlayerProfileDto>> done);
        void SendFriendRequest(string publicId, Action<ServiceResult> done);
        void RespondToRequest(int requestId, bool accept, Action<ServiceResult> done);
        void CancelRequest(int requestId, Action<ServiceResult> done);
        void RemoveFriend(string publicId, Action<ServiceResult> done);
        void Block(string publicId, Action<ServiceResult> done);
        void Unblock(string publicId, Action<ServiceResult> done);
        void Report(string publicId, string reason, Action<ServiceResult> done);
        void Invite(string publicId, string modeId, Action<ServiceResult> done);
        void RespondToInvitation(int invitationId, bool accept, Action<ServiceResult> done);
        void LeaveParty(Action<ServiceResult> done);
        void SetReady(bool ready, Action<ServiceResult> done);
        void SetPartyMode(string modeId, Action<ServiceResult> done);
    }

    public sealed class OfflineSocialService : ISocialService
    {
        private static readonly ServiceResult Unavailable = ServiceResult.Fail("Sign in to an account to use online features.");

        public bool IsAvailable => false;
        public string UnavailableReason => "Sign in to an account (Settings > Account) to use friends, parties and invitations.";
        public SocialSnapshot Snapshot { get; } = new SocialSnapshot();
        public event Action Changed { add { } remove { } }

        private static void Fail<T>(Action<ApiResult<T>> done) where T : class =>
            done?.Invoke(new ApiResult<T> { Ok = false, Code = "offline", Message = Unavailable.Error });

        public void Refresh(Action<ServiceResult> done = null) => done?.Invoke(Unavailable);
        public void Search(string query, Action<ApiResult<SearchDto>> done) => Fail(done);
        public void GetProfile(string publicId, Action<ApiResult<PlayerProfileDto>> done) => Fail(done);
        public void SendFriendRequest(string publicId, Action<ServiceResult> done) => done?.Invoke(Unavailable);
        public void RespondToRequest(int requestId, bool accept, Action<ServiceResult> done) => done?.Invoke(Unavailable);
        public void CancelRequest(int requestId, Action<ServiceResult> done) => done?.Invoke(Unavailable);
        public void RemoveFriend(string publicId, Action<ServiceResult> done) => done?.Invoke(Unavailable);
        public void Block(string publicId, Action<ServiceResult> done) => done?.Invoke(Unavailable);
        public void Unblock(string publicId, Action<ServiceResult> done) => done?.Invoke(Unavailable);
        public void Report(string publicId, string reason, Action<ServiceResult> done) => done?.Invoke(Unavailable);
        public void Invite(string publicId, string modeId, Action<ServiceResult> done) => done?.Invoke(Unavailable);
        public void RespondToInvitation(int invitationId, bool accept, Action<ServiceResult> done) => done?.Invoke(Unavailable);
        public void LeaveParty(Action<ServiceResult> done) => done?.Invoke(Unavailable);
        public void SetReady(bool ready, Action<ServiceResult> done) => done?.Invoke(Unavailable);
        public void SetPartyMode(string modeId, Action<ServiceResult> done) => done?.Invoke(Unavailable);
    }

    public sealed class RemoteSocialService : ISocialService
    {
        private readonly ApiClient api;

        public RemoteSocialService(ApiClient api) { this.api = api; }

        /// <summary>Set by AccountService while a valid session exists.</summary>
        public bool SignedIn { get; set; }
        public bool IsAvailable => SignedIn;
        public string UnavailableReason => SignedIn ? "" : "Sign in to an account (Settings > Account) to use friends, parties and invitations.";
        public SocialSnapshot Snapshot { get; private set; } = new SocialSnapshot();
        public event Action Changed;

        public void Clear()
        {
            Snapshot = new SocialSnapshot();
            Changed?.Invoke();
        }

        private static ServiceResult ToResult<T>(ApiResult<T> r) where T : class => r.Ok ? ServiceResult.Ok : ServiceResult.Fail(r.Friendly);

        public void Refresh(Action<ServiceResult> done = null)
        {
            if (!SignedIn) { done?.Invoke(ServiceResult.Fail("Not signed in.")); return; }
            api.Request<FriendsDto>("GET", "/api/friends", null, friends =>
            {
                if (!friends.Ok) { done?.Invoke(ToResult(friends)); return; }
                api.Request<PartyStateDto>("GET", "/api/party", null, party =>
                {
                    if (!party.Ok) { done?.Invoke(ToResult(party)); return; }
                    api.Request<BlocksDto>("GET", "/api/blocks", null, blocks =>
                    {
                        var snap = new SocialSnapshot
                        {
                            Friends = friends.Value.friends ?? new List<PlayerBriefDto>(),
                            Incoming = friends.Value.incoming ?? new List<FriendRequestDto>(),
                            Outgoing = friends.Value.outgoing ?? new List<FriendRequestDto>(),
                            Invitations = party.Value.invitations ?? new List<InvitationDto>(),
                            Party = NormalizeParty(party.Value.party),
                            Blocked = blocks.Ok ? blocks.Value.blocked : Snapshot.Blocked,
                        };
                        Snapshot = snap;
                        Changed?.Invoke();
                        done?.Invoke(ServiceResult.Ok);
                    });
                });
            });
        }

        private static PartyDto NormalizeParty(PartyDto p) => p == null || p.members == null || p.members.Count == 0 ? null : p;

        private void Act<T>(string method, string path, Dictionary<string, object> body, Action<ServiceResult> done) where T : class, new()
        {
            if (!SignedIn) { done?.Invoke(ServiceResult.Fail("Not signed in.")); return; }
            api.Request<T>(method, path, body, r =>
            {
                if (!r.Ok) { done?.Invoke(ToResult(r)); return; }
                Refresh(refreshed => done?.Invoke(ServiceResult.Ok));
            });
        }

        public void Search(string query, Action<ApiResult<SearchDto>> done)
        {
            api.Request<SearchDto>("GET", "/api/players/search?q=" + Uri.EscapeDataString(query ?? ""), null, done);
        }

        public void GetProfile(string publicId, Action<ApiResult<PlayerProfileDto>> done)
        {
            api.Request<PlayerProfileDto>("GET", "/api/players/" + Uri.EscapeDataString(publicId ?? ""), null, done);
        }

        public void SendFriendRequest(string publicId, Action<ServiceResult> done) =>
            Act<OkDto>("POST", "/api/friends/requests", new Dictionary<string, object> { ["publicId"] = publicId }, done);

        public void RespondToRequest(int requestId, bool accept, Action<ServiceResult> done) =>
            Act<OkDto>("POST", $"/api/friends/requests/{requestId}/{(accept ? "accept" : "decline")}", null, done);

        public void CancelRequest(int requestId, Action<ServiceResult> done) =>
            Act<OkDto>("POST", $"/api/friends/requests/{requestId}/cancel", null, done);

        public void RemoveFriend(string publicId, Action<ServiceResult> done) =>
            Act<OkDto>("DELETE", "/api/friends/" + Uri.EscapeDataString(publicId), null, done);

        public void Block(string publicId, Action<ServiceResult> done) =>
            Act<OkDto>("POST", "/api/blocks", new Dictionary<string, object> { ["publicId"] = publicId }, done);

        public void Unblock(string publicId, Action<ServiceResult> done) =>
            Act<OkDto>("DELETE", "/api/blocks/" + Uri.EscapeDataString(publicId), null, done);

        public void Report(string publicId, string reason, Action<ServiceResult> done)
        {
            if (!SignedIn) { done?.Invoke(ServiceResult.Fail("Not signed in.")); return; }
            api.Request<OkDto>("POST", $"/api/players/{Uri.EscapeDataString(publicId)}/report", new Dictionary<string, object> { ["reason"] = reason }, r => done?.Invoke(ToResult(r)));
        }

        public void Invite(string publicId, string modeId, Action<ServiceResult> done)
        {
            var body = new Dictionary<string, object> { ["publicId"] = publicId };
            if (!string.IsNullOrEmpty(modeId)) body["modeId"] = modeId;
            Act<OkDto>("POST", "/api/party/invite", body, done);
        }

        public void RespondToInvitation(int invitationId, bool accept, Action<ServiceResult> done) =>
            Act<PartyResultDto>("POST", $"/api/party/invitations/{invitationId}/{(accept ? "accept" : "decline")}", null, done);

        public void LeaveParty(Action<ServiceResult> done) => Act<OkDto>("POST", "/api/party/leave", null, done);

        public void SetReady(bool ready, Action<ServiceResult> done) =>
            Act<PartyResultDto>("POST", "/api/party/ready", new Dictionary<string, object> { ["ready"] = ready }, done);

        public void SetPartyMode(string modeId, Action<ServiceResult> done) =>
            Act<PartyResultDto>("POST", "/api/party/mode", new Dictionary<string, object> { ["modeId"] = modeId }, done);
    }
}
