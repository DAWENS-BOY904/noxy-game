using System;
using System.Collections.Generic;
using UnityEngine;

namespace Noxy.Core
{
    public enum AccountState { SignedOut, Working, SignedIn }

    /// <summary>Persists the session token and server address. Unity implementation uses PlayerPrefs (dev only; use the platform keystore in production).</summary>
    public interface ITokenStore
    {
        string LoadToken();
        void SaveToken(string token);
        string LoadServerUrl();
        void SaveServerUrl(string url);
    }

    public sealed class MemoryTokenStore : ITokenStore
    {
        private string token = "";
        private string url = "";
        public string LoadToken() => token;
        public void SaveToken(string value) => token = value ?? "";
        public string LoadServerUrl() => url;
        public void SaveServerUrl(string value) => url = value ?? "";
    }

    /// <summary>
    /// Owns the signed-in session: authentication, mirroring server data into the profile, syncing settings,
    /// loadout and HUD layouts, presence, and polling for friend/party/notification updates. Nothing here is
    /// authoritative: the server validates every request and the client only mirrors the result.
    /// </summary>
    public sealed class AccountService
    {
        public const float HeartbeatSeconds = 10f;
        public const float PollSeconds = 4f;
        public const float RetrySeconds = 15f;

        private readonly ApiClient api;
        private readonly IProfileService profile;
        private readonly LocalConfigStore config;
        private readonly NotificationService notifications;
        private readonly RemoteSocialService social;
        private readonly ITokenStore tokens;

        private float heartbeatTimer, pollTimer, retryTimer;
        private int lastNotificationId;
        private bool firstNotificationPull = true;
        private Loadout pendingLoadout;
        private bool pendingRestore;
        private bool polling;

        public AccountService(ApiClient api, IProfileService profile, LocalConfigStore config, NotificationService notifications,
            RemoteSocialService social, ITokenStore tokens)
        {
            this.api = api;
            this.profile = profile;
            this.config = config;
            this.notifications = notifications;
            this.social = social;
            this.tokens = tokens;
            social.UnavailableDetail = () => UnavailableReason;

            string savedUrl = tokens.LoadServerUrl();
            if (!string.IsNullOrEmpty(savedUrl)) api.BaseUrl = savedUrl;
            profile.LoadoutEquipped += OnLoadoutEquipped;
        }

        public AccountState State { get; private set; } = AccountState.SignedOut;
        public string LastError { get; private set; } = "";
        public string PublicId { get; private set; } = "";
        public string Username { get; private set; } = "";
        public bool ServerReachable { get; private set; } = true;

        /// <summary>Returns "lobby" or "in_match". Set by the app so presence reflects the current scene.</summary>
        public Func<string> PresenceProvider = () => "lobby";

        /// <summary>
        /// True while this session is bound to a verified Supabase account. The game server then accepts the live Supabase
        /// access token, nothing is stored in the legacy token store, and Supabase (not this class) owns session lifetime.
        /// </summary>
        public bool SupabaseBound { get; private set; }
        /// <summary>Raised when the game server rejects the Supabase token, so the auth layer can re-verify the session.</summary>
        public Action OnUnauthorized;
        private bool pendingSupabaseRetry;
        private Func<string> supabaseToken;
        private float unauthorizedCooldown;

        /// <summary>Plain-language reason online features are unavailable right now (never claims success it doesn't have).</summary>
        public string UnavailableReason
        {
            get
            {
                if (State == AccountState.SignedIn) return "";
                if (SupabaseBound)
                    return State == AccountState.Working
                        ? "Connecting to the game server..."
                        : "Your account is verified, but the game server is unavailable: " + (string.IsNullOrEmpty(LastError) ? "not reachable." : LastError);
                return CommunityText.OfflineReason;
            }
        }

        public event Action Changed;

        /// <summary>Optional wallet/store client; kept in sync with the signed-in state.</summary>
        private RemoteEconomyService economyService;
        private RemoteCommunityService communityService;
        // Setting a service also tells it why it is unavailable, so every screen explains the real reason.
        public RemoteEconomyService Economy { get => economyService; set { economyService = value; if (value != null) value.UnavailableDetail = () => UnavailableReason; } }
        public RemoteCommunityService Community { get => communityService; set { communityService = value; if (value != null) value.UnavailableDetail = () => UnavailableReason; } }
        private RemoteBattleRoyaleService battleRoyaleService;
        public RemoteBattleRoyaleService BattleRoyale { get => battleRoyaleService; set { battleRoyaleService = value; if (value != null) value.UnavailableDetail = () => UnavailableReason; } }

        public string ServerUrl
        {
            get => api.BaseUrl;
            set
            {
                api.BaseUrl = (value ?? "").Trim();
                tokens.SaveServerUrl(api.BaseUrl);
                Changed?.Invoke();
            }
        }

        public static bool IsValidServerUrl(string url, out string problem)
        {
            problem = null;
            if (string.IsNullOrWhiteSpace(url) || !Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri) || (uri.Scheme != "http" && uri.Scheme != "https"))
            {
                problem = "Enter a full address like https://game.example.com or http://192.168.1.20:8787";
                return false;
            }
            // The account session token is sent to this server, so clear-text http is only allowed on this machine or a private network.
            if (uri.Scheme == "http" && !IsPrivateHost(uri.Host))
            {
                problem = "This address must use https:// (plain http is only allowed for localhost and private networks).";
                return false;
            }
            return true;
        }

        /// <summary>Loopback, RFC 1918 / link-local IPv4, and .local / single-label names. Everything else needs https.</summary>
        public static bool IsPrivateHost(string host)
        {
            if (string.IsNullOrEmpty(host)) return false;
            host = host.Trim('[', ']').ToLowerInvariant();
            if (host == "localhost" || host == "::1" || host.EndsWith(".local")) return true;
            if (System.Net.IPAddress.TryParse(host, out var ip))
            {
                if (System.Net.IPAddress.IsLoopback(ip)) return true;
                var b = ip.GetAddressBytes();
                if (b.Length != 4) return false;
                return b[0] == 10 || (b[0] == 172 && b[1] >= 16 && b[1] <= 31) || (b[0] == 192 && b[1] == 168) || (b[0] == 169 && b[1] == 254);
            }
            return false;
        }

        // ---------- sign in / out ----------

        public void Register(string username, string password, Action<ServiceResult> done) => Authenticate("/api/auth/register", username, password, done);
        public void SignIn(string username, string password, Action<ServiceResult> done) => Authenticate("/api/auth/login", username, password, done);

        private void Authenticate(string path, string username, string password, Action<ServiceResult> done)
        {
            if (!IsValidServerUrl(api.BaseUrl, out string problem)) { Finish(done, ServiceResult.Fail(problem)); return; }
            if (State == AccountState.Working) { Finish(done, ServiceResult.Fail("Please wait...")); return; }
            SetState(AccountState.Working);
            api.Token = null;
            api.Request<AuthDto>("POST", path, new Dictionary<string, object> { ["username"] = username, ["password"] = password }, r =>
            {
                if (!r.Ok) { SetState(AccountState.SignedOut); Finish(done, ServiceResult.Fail(r.Friendly)); return; }
                BeginSession(r.Value.token, r.Value.player, done);
            });
        }

        /// <summary>
        /// Starts a game-server session from a verified Supabase login. The server verifies the token with Supabase itself and
        /// adopts the Player ID stored in the Supabase profile. Failure leaves online features OFF with a visible reason; it
        /// never falls back to a fake or local identity.
        /// </summary>
        public void SignInWithSupabase(Func<string> accessToken, Action<ServiceResult> done = null)
        {
            if (State == AccountState.Working) { done?.Invoke(ServiceResult.Fail("Please wait...")); return; }
            SupabaseBound = true;
            supabaseToken = accessToken;
            pendingSupabaseRetry = false;
            if (!IsValidServerUrl(api.BaseUrl, out string problem)) { SetState(AccountState.SignedOut); Finish(done, ServiceResult.Fail(problem)); return; }

            SetState(AccountState.Working);
            api.Token = null;
            api.TokenProvider = accessToken;
            api.Request<MeDto>("GET", "/api/me", null, r =>
            {
                if (!r.Ok)
                {
                    SetState(AccountState.SignedOut);
                    // Network trouble and 5xx are worth retrying; a 4xx means the server refused this account and retrying won't help.
                    if (r.NetworkError || r.Status >= 500) { pendingSupabaseRetry = true; retryTimer = RetrySeconds; }
                    Finish(done, ServiceResult.Fail(r.Friendly));
                    return;
                }
                BeginSession(null, r.Value, done);
            });
        }

        /// <summary>
        /// Keeps the game-server session in step with the verified Supabase session. Safe to call on every auth change:
        /// it starts a server session only for a verified sign-in and ends it only once Supabase is really signed out.
        /// </summary>
        public void SyncWithAuth(AuthService auth)
        {
            if (auth == null) return;
            if (auth.State == AuthState.SignedIn && auth.Session != null)
            {
                if (!SupabaseBound && State != AccountState.Working) SignInWithSupabase(auth.ValidAccessToken);
            }
            else if ((auth.State == AuthState.SignedOut || auth.State == AuthState.NotConfigured) && SupabaseBound)
            {
                EndSupabaseSession();
            }
        }

        /// <summary>Called when the Supabase session ends (sign-out, expiry, revocation).</summary>
        public void EndSupabaseSession()
        {
            if (!SupabaseBound) return;
            EndSessionLocally();
        }

        /// <summary>Resume a saved session (called at startup). Silent when the server is unreachable.</summary>
        public void TryRestoreSession(Action<ServiceResult> done = null)
        {
            string token = tokens.LoadToken();
            if (string.IsNullOrEmpty(token) || !IsValidServerUrl(api.BaseUrl, out _)) { done?.Invoke(ServiceResult.Fail("No saved session.")); return; }

            SetState(AccountState.Working);
            api.Token = token;
            api.Request<MeDto>("GET", "/api/me", null, r =>
            {
                if (r.Ok) { BeginSession(token, r.Value, done); return; }
                api.Token = null;
                SetState(AccountState.SignedOut);
                if (r.NetworkError) { pendingRestore = true; retryTimer = 0f; }
                else tokens.SaveToken("");
                done?.Invoke(ServiceResult.Fail(r.Friendly));
            });
        }

        private void BeginSession(string token, MeDto me, Action<ServiceResult> done)
        {
            if (!SupabaseBound) api.Token = token;
            api.Request<InventoryDto>("GET", "/api/me/inventory", null, inv =>
            {
                if (!inv.Ok)
                {
                    if (!SupabaseBound) api.Token = null;
                    SetState(AccountState.SignedOut);
                    if (SupabaseBound && (inv.NetworkError || inv.Status >= 500)) { pendingSupabaseRetry = true; retryTimer = RetrySeconds; }
                    Finish(done, ServiceResult.Fail(inv.Friendly));
                    return;
                }

                if (!SupabaseBound) tokens.SaveToken(token); // a Supabase access token must never land in the legacy token store
                pendingRestore = false;
                PublicId = me.publicId;
                Username = me.username;
                profile.ApplyServerSnapshot(AccountMapper.ToProfile(me, inv.Value));
                social.SignedIn = true;
                if (Economy != null) { Economy.SignedIn = true; Economy.RefreshWallet(); }
                if (Community != null) Community.SignedIn = true;
                if (BattleRoyale != null) BattleRoyale.SignedIn = true;
                lastNotificationId = 0;
                firstNotificationPull = true;
                heartbeatTimer = 0f;
                pollTimer = PollSeconds;
                SetState(AccountState.SignedIn);

                PullSettings(() => PullHud(() => social.Refresh(_ =>
                {
                    notifications.Post("Signed in", $"{me.username}  |  ID {me.publicId}", NotificationSeverity.Success);
                    Finish(done, ServiceResult.Ok);
                })));
            });
        }

        public void SignOut(Action<ServiceResult> done = null)
        {
            if (State == AccountState.SignedOut && string.IsNullOrEmpty(api.Token)) { done?.Invoke(ServiceResult.Ok); return; }
            void Local()
            {
                EndSessionLocally();
                done?.Invoke(ServiceResult.Ok);
            }
            if (SupabaseBound) { Local(); return; } // the auth layer ends the Supabase session itself
            if (State == AccountState.SignedIn) api.Request<OkDto>("POST", "/api/auth/logout", null, _ => Local());
            else Local();
        }

        public void DeleteAccount(string password, Action<ServiceResult> done)
        {
            if (State != AccountState.SignedIn) { done?.Invoke(ServiceResult.Fail("Not signed in.")); return; }
            api.Request<OkDto>("DELETE", "/api/me", new Dictionary<string, object> { ["password"] = password }, r =>
            {
                if (!r.Ok) { done?.Invoke(ServiceResult.Fail(r.Friendly)); return; }
                EndSessionLocally();
                notifications.Post("Account deleted", "Your account was removed from the server. Local data on this device is kept.", NotificationSeverity.Info);
                done?.Invoke(ServiceResult.Ok);
            });
        }

        public void ChangeUsername(string username, Action<ServiceResult> done)
        {
            if (State != AccountState.SignedIn) { done?.Invoke(ServiceResult.Fail("Not signed in.")); return; }
            api.Request<MeDto>("PATCH", "/api/me", new Dictionary<string, object> { ["username"] = username }, r =>
            {
                if (!r.Ok) { done?.Invoke(ServiceResult.Fail(r.Friendly)); return; }
                Username = r.Value.username;
                profile.Profile.username = r.Value.username;
                done?.Invoke(ServiceResult.Ok);
                Changed?.Invoke();
            });
        }

        private void EndSessionLocally()
        {
            api.Token = null;
            api.TokenProvider = null;
            SupabaseBound = false;
            supabaseToken = null;
            pendingSupabaseRetry = false;
            tokens.SaveToken("");
            social.SignedIn = false;
            social.Clear();
            if (Economy != null) { Economy.SignedIn = false; Economy.Clear(); }
            if (Community != null) Community.SignedIn = false;
            if (BattleRoyale != null) BattleRoyale.SignedIn = false;
            profile.RevertToLocal();
            PublicId = "";
            Username = "";
            pendingLoadout = null;
            pendingRestore = false;
            SetState(AccountState.SignedOut);
        }

        private void SetState(AccountState state)
        {
            State = state;
            Changed?.Invoke();
        }

        private void Finish(Action<ServiceResult> done, ServiceResult result)
        {
            LastError = result.Success ? "" : result.Error;
            Changed?.Invoke();
            done?.Invoke(result);
        }

        // ---------- sync ----------

        /// <summary>Pushes privacy and gameplay preferences. Local settings always apply immediately; this only mirrors them.</summary>
        public void PushSettings(Action<ServiceResult> done = null)
        {
            if (State != AccountState.SignedIn) { done?.Invoke(ServiceResult.Fail("Not signed in.")); return; }
            var s = config.Settings;
            var body = new Dictionary<string, object>
            {
                ["friendRequests"] = s.friendRequests == (int)Visibility.Nobody ? "nobody" : "everyone",
                ["partyInvites"] = s.partyInvites == (int)Visibility.Nobody ? "nobody" : "friends",
                ["onlineStatus"] = s.onlineStatus == (int)Visibility.Nobody ? "nobody" : s.onlineStatus == (int)Visibility.Everyone ? "everyone" : "friends",
                ["data"] = s.ToSyncMap(),
            };
            api.Request<OkDto>("PUT", "/api/me/settings", body, r => done?.Invoke(r.Ok ? ServiceResult.Ok : ServiceResult.Fail(r.Friendly)));
        }

        private void PullSettings(Action next)
        {
            api.RequestRaw("GET", "/api/me/settings", null, resp =>
            {
                if (resp.Status == 200 && MiniJson.TryParse(resp.Body, out var parsed) && parsed is Dictionary<string, object> root)
                {
                    var s = config.Settings;
                    s.friendRequests = MiniJson.Str(root, "friendRequests") == "nobody" ? (int)Visibility.Nobody : (int)Visibility.Everyone;
                    s.partyInvites = MiniJson.Str(root, "partyInvites") == "nobody" ? (int)Visibility.Nobody : (int)Visibility.Friends;
                    string status = MiniJson.Str(root, "onlineStatus");
                    s.onlineStatus = status == "nobody" ? (int)Visibility.Nobody : status == "everyone" ? (int)Visibility.Everyone : (int)Visibility.Friends;
                    if (root.TryGetValue("data", out var data) && data is Dictionary<string, object> map && map.Count > 0) s.ApplySyncMap(map);
                    config.SaveSettings();
                }
                next();
            });
        }

        public void PushHud(HudLayout layout, Action<ServiceResult> done = null)
        {
            if (State != AccountState.SignedIn) { done?.Invoke(ServiceResult.Fail("Not signed in.")); return; }
            api.Request<OkDto>("PUT", "/api/me/hud/" + layout.key, layout.ToApiBody(), r => done?.Invoke(r.Ok ? ServiceResult.Ok : ServiceResult.Fail(r.Friendly)));
        }

        private void PullHud(Action next)
        {
            PullHudKey("landscape", () => PullHudKey("portrait", next));
        }

        private void PullHudKey(string key, Action next)
        {
            api.RequestRaw("GET", "/api/me/hud/" + key, null, resp =>
            {
                if (resp.Status == 200 && MiniJson.TryParse(resp.Body, out var parsed) && parsed is Dictionary<string, object> root)
                {
                    var layout = HudLayout.CreateDefault(key == "portrait");
                    layout.ApplyApiBody(root);
                    config.SaveHud(layout);
                }
                next();
            });
        }

        private void OnLoadoutEquipped(Loadout loadout)
        {
            if (State != AccountState.SignedIn) return;
            pendingLoadout = loadout;
            PushLoadout();
        }

        private void PushLoadout()
        {
            if (pendingLoadout == null) return;
            var sent = pendingLoadout;
            var map = new Dictionary<string, object>();
            foreach (CosmeticSlot slot in Enum.GetValues(typeof(CosmeticSlot))) map[slot.ApiName()] = sent.Get(slot);

            api.Request<OkDto>("PUT", "/api/me/loadout", new Dictionary<string, object> { ["loadout"] = map }, r =>
            {
                if (r.Ok) { if (pendingLoadout == sent) pendingLoadout = null; return; }
                if (r.NetworkError) return; // keep pending, retried on the next poll

                // The server refused it (e.g. unowned item): it is authoritative, so re-sync from the server.
                pendingLoadout = null;
                notifications.Post("Loadout rejected", r.Friendly, NotificationSeverity.Error);
                ResyncInventory();
            });
        }

        public void ResyncInventory(Action<ServiceResult> done = null)
        {
            api.Request<MeDto>("GET", "/api/me", null, me =>
            {
                if (!me.Ok) { done?.Invoke(ServiceResult.Fail(me.Friendly)); return; }
                api.Request<InventoryDto>("GET", "/api/me/inventory", null, inv =>
                {
                    if (!inv.Ok) { done?.Invoke(ServiceResult.Fail(inv.Friendly)); return; }
                    profile.ApplyServerSnapshot(AccountMapper.ToProfile(me.Value, inv.Value));
                    done?.Invoke(ServiceResult.Ok);
                });
            });
        }

        // ---------- periodic work ----------

        public void Tick(float dt)
        {
            if (unauthorizedCooldown > 0f) unauthorizedCooldown -= dt;
            if (State == AccountState.SignedOut && SupabaseBound && pendingSupabaseRetry)
            {
                retryTimer -= dt;
                if (retryTimer <= 0f) { retryTimer = RetrySeconds; SignInWithSupabase(supabaseToken); }
                return;
            }
            if (State == AccountState.SignedOut && pendingRestore)
            {
                retryTimer -= dt;
                if (retryTimer <= 0f) { retryTimer = RetrySeconds; TryRestoreSession(); }
                return;
            }
            if (State != AccountState.SignedIn) return;

            heartbeatTimer -= dt;
            if (heartbeatTimer <= 0f)
            {
                heartbeatTimer = HeartbeatSeconds;
                api.Request<OkDto>("POST", "/api/presence", new Dictionary<string, object> { ["state"] = PresenceProvider() }, r => HandleHealth(r.NetworkError, r.Status));
            }

            pollTimer -= dt;
            if (pollTimer <= 0f && !polling)
            {
                pollTimer = PollSeconds;
                Poll();
            }
        }

        private void Poll()
        {
            polling = true;
            PushLoadout();
            social.Refresh(result =>
            {
                api.Request<NotificationsDto>("GET", "/api/notifications?after=" + lastNotificationId, null, n =>
                {
                    polling = false;
                    HandleHealth(n.NetworkError, n.Status);
                    if (!n.Ok) return;
                    foreach (var note in n.Value.notifications)
                    {
                        lastNotificationId = Math.Max(lastNotificationId, note.id);
                        if (note.kind == "reward" && !firstNotificationPull) ResyncInventory(); // server granted currency: refresh balances
                        notifications.Post(note.title, note.body, SeverityFor(note.kind), toast: !firstNotificationPull && config.Settings.notifications);
                    }
                    firstNotificationPull = false;
                });
            });
        }

        private void HandleHealth(bool networkError, int status)
        {
            if (status == 401 && SupabaseBound)
            {
                // Supabase owns the session: ask it whether the account is still valid instead of silently dropping online play.
                if (unauthorizedCooldown <= 0f) { unauthorizedCooldown = 30f; OnUnauthorized?.Invoke(); }
                return;
            }
            if (status == 401)
            {
                EndSessionLocally();
                notifications.Post("Signed out", "Your session expired. Sign in again to use online features.", NotificationSeverity.Warning);
                return;
            }
            if (ServerReachable == !networkError) return;
            ServerReachable = !networkError;
            notifications.Post(ServerReachable ? "Back online" : "Server unreachable",
                ServerReachable ? "Reconnected to the server." : "Offline features still work; online features resume when the server returns.",
                ServerReachable ? NotificationSeverity.Success : NotificationSeverity.Warning);
            Changed?.Invoke();
        }

        private static NotificationSeverity SeverityFor(string kind)
        {
            switch (kind)
            {
                case "friend_accepted":
                case "invite_accepted": return NotificationSeverity.Success;
                case "invite_declined":
                case "invite_expired": return NotificationSeverity.Warning;
                default: return NotificationSeverity.Info;
            }
        }
    }

    public static class AccountMapper
    {
        public static PlayerProfile ToProfile(MeDto me, InventoryDto inventory)
        {
            var p = new PlayerProfile
            {
                playerId = me.publicId,
                publicId = me.publicId,
                username = me.username,
                rank = me.rank,
                level = Math.Max(1, me.level),
                xp = me.xp,
                credits = me.credits,
                cores = me.cores,
                stats = new PlayerStats
                {
                    matchesPlayed = me.stats.matches, wins = me.stats.wins, topThree = me.stats.top3, kills = me.stats.kills,
                    deaths = me.stats.deaths, headshots = me.stats.headshots, hoursPlayed = me.stats.hoursPlayed
                },
                loadout = ToLoadout(inventory.loadout),
            };
            p.ownedCosmeticIds.AddRange(inventory.owned);
            return p;
        }

        public static Loadout ToLoadout(LoadoutDto d)
        {
            return new Loadout
            {
                outfit = d.outfit ?? "", shoes = d.shoes ?? "", mask = d.mask ?? "", headgear = d.headgear ?? "",
                backpack = d.backpack ?? "", weaponSkin = d.weaponskin ?? "", emote = d.emote ?? ""
            };
        }
    }
}
