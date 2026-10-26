using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;

namespace Noxy.Core
{
    // ------------------------------------------------------------------------------------------
    // Supabase Auth (GoTrue) client + session state machine. Engine-free so it can be unit tested.
    // Only the project URL and the public anon key are used here. The service-role key must never
    // be placed in the client.
    // ------------------------------------------------------------------------------------------

    public sealed class SupabaseConfig
    {
        public string Url = "";
        public string AnonKey = "";
        /// <summary>Deep link registered for Android/iOS and (via loopback) PC, e.g. noxy://auth-callback.</summary>
        public string RedirectUrl = "noxy://auth-callback";

        public bool IsConfigured =>
            Url.StartsWith("https://", StringComparison.OrdinalIgnoreCase) &&
            AnonKey.Length > 20 && !AnonKey.Contains("YOUR_");

        public string Base => Url.TrimEnd('/');

        public static SupabaseConfig FromJson(string json)
        {
            var c = new SupabaseConfig();
            if (string.IsNullOrWhiteSpace(json) || !MiniJson.TryParse(json, out var o)) return c;
            var d = MiniJson.AsObject(o);
            if (d == null) return c;
            c.Url = MiniJson.Str(d, "url").Trim();
            c.AnonKey = MiniJson.Str(d, "anonKey").Trim();
            var r = MiniJson.Str(d, "redirectUrl").Trim();
            if (r.Length > 0) c.RedirectUrl = r;
            return c;
        }
    }

    public static class Pkce
    {
        public static string NewVerifier(Func<int, byte[]> random = null)
        {
            var bytes = random != null ? random(48) : RandomBytes(48);
            return Base64Url(bytes);
        }

        public static string Challenge(string verifier)
        {
            using (var sha = SHA256.Create())
                return Base64Url(sha.ComputeHash(Encoding.ASCII.GetBytes(verifier)));
        }

        public static string Base64Url(byte[] data) =>
            Convert.ToBase64String(data).TrimEnd('=').Replace('+', '-').Replace('/', '_');

        public static byte[] FromBase64Url(string s)
        {
            s = s.Replace('-', '+').Replace('_', '/');
            switch (s.Length % 4) { case 2: s += "=="; break; case 3: s += "="; break; }
            return Convert.FromBase64String(s);
        }

        private static byte[] RandomBytes(int n)
        {
            var b = new byte[n];
            using (var rng = RandomNumberGenerator.Create()) rng.GetBytes(b);
            return b;
        }
    }

    public enum AuthErrorKind
    {
        None, Network, InvalidCredentials, AccountExists, WeakPassword, RateLimited, EmailNotConfirmed,
        Cancelled, ProviderDisabled, LinkConflict, SessionExpired, NotConfigured, InvalidCallback, Unknown
    }

    public sealed class AuthResult
    {
        public bool Ok;
        public AuthErrorKind Kind;
        public string Message = "";
        public AuthSession Session;
        public string Url = "";
        /// <summary>True when sign-up succeeded but the user must confirm their e-mail before a session exists.</summary>
        public bool NeedsEmailConfirmation;

        public static AuthResult Success(AuthSession s = null) => new AuthResult { Ok = true, Session = s };
        public static AuthResult Fail(AuthErrorKind kind, string message) => new AuthResult { Kind = kind, Message = message };
    }

    public sealed class AuthSession
    {
        public string AccessToken = "";
        public string RefreshToken = "";
        public long ExpiresAtUnix;
        public string UserId = "";
        public string Email = "";
        public bool IsAnonymous;
        public List<string> Providers = new List<string>();

        public bool IsExpired(long nowUnix, int skewSeconds = 0) => nowUnix + skewSeconds >= ExpiresAtUnix;

        public string ToJson() => MiniJson.Write(new Dictionary<string, object>
        {
            ["access"] = AccessToken, ["refresh"] = RefreshToken, ["exp"] = (double)ExpiresAtUnix,
            ["uid"] = UserId, ["email"] = Email, ["anon"] = IsAnonymous,
            ["providers"] = Providers.ConvertAll(p => (object)p)
        });

        public static AuthSession FromJson(string json)
        {
            if (string.IsNullOrEmpty(json) || !MiniJson.TryParse(json, out var o)) return null;
            var d = MiniJson.AsObject(o);
            if (d == null) return null;
            var s = new AuthSession
            {
                AccessToken = MiniJson.Str(d, "access"), RefreshToken = MiniJson.Str(d, "refresh"),
                ExpiresAtUnix = (long)MiniJson.Number(d, "exp"), UserId = MiniJson.Str(d, "uid"),
                Email = MiniJson.Str(d, "email"), IsAnonymous = MiniJson.Bool(d, "anon")
            };
            if (d.TryGetValue("providers", out var p) && p is List<object> list)
                foreach (var x in list) if (x is string str) s.Providers.Add(str);
            return s.AccessToken.Length == 0 || s.RefreshToken.Length == 0 ? null : s;
        }
    }

    public static class Jwt
    {
        /// <summary>Reads the unverified payload. Used only for expiry/UI; the server verifies the signature.</summary>
        public static Dictionary<string, object> Payload(string token)
        {
            if (string.IsNullOrEmpty(token)) return null;
            var parts = token.Split('.');
            if (parts.Length != 3) return null;
            try
            {
                var json = Encoding.UTF8.GetString(Pkce.FromBase64Url(parts[1]));
                return MiniJson.TryParse(json, out var o) ? MiniJson.AsObject(o) : null;
            }
            catch (FormatException) { return null; }
        }

        public static long ExpiresAt(string token)
        {
            var p = Payload(token);
            return p == null ? 0 : (long)MiniJson.Number(p, "exp");
        }
    }

    public static class AuthErrors
    {
        public static AuthResult FromResponse(HttpResponse r)
        {
            if (r.Status == 0)
                return AuthResult.Fail(AuthErrorKind.Network, "Cannot reach the account server. Check your connection and try again.");

            string code = "", msg = "";
            if (MiniJson.TryParse(r.Body, out var o) && MiniJson.AsObject(o) is Dictionary<string, object> d)
            {
                code = MiniJson.Str(d, "error_code");
                if (code.Length == 0) code = MiniJson.Str(d, "code");
                if (code.Length == 0) code = MiniJson.Str(d, "error");
                msg = MiniJson.Str(d, "msg");
                if (msg.Length == 0) msg = MiniJson.Str(d, "message");
                if (msg.Length == 0) msg = MiniJson.Str(d, "error_description");
            }
            return Map(r.Status, code, msg);
        }

        public static AuthResult Map(int status, string code, string msg)
        {
            switch (code)
            {
                case "invalid_credentials": case "invalid_grant":
                    return AuthResult.Fail(AuthErrorKind.InvalidCredentials, "Incorrect e-mail or password.");
                case "email_exists": case "user_already_exists":
                    return AuthResult.Fail(AuthErrorKind.AccountExists, "An account with this e-mail already exists. Sign in instead, or use the provider you registered with.");
                case "weak_password":
                    return AuthResult.Fail(AuthErrorKind.WeakPassword, "Password is too weak. Use at least 8 characters with letters and numbers.");
                case "over_request_rate_limit": case "over_email_send_rate_limit": case "over_sms_send_rate_limit":
                    return AuthResult.Fail(AuthErrorKind.RateLimited, "Too many attempts. Please wait a minute and try again.");
                case "email_not_confirmed":
                    return AuthResult.Fail(AuthErrorKind.EmailNotConfirmed, "Please confirm your e-mail address first (check your inbox).");
                case "provider_disabled": case "validation_failed" when msg.IndexOf("provider", StringComparison.OrdinalIgnoreCase) >= 0:
                    return AuthResult.Fail(AuthErrorKind.ProviderDisabled, "This sign-in method is not enabled for the game yet.");
                case "identity_already_exists":
                    return AuthResult.Fail(AuthErrorKind.LinkConflict,
                        "That account is already linked to another Noxy account. Sign out, sign in with it directly, and keep using that account. Your current progress is not changed.");
                case "manual_linking_disabled":
                    return AuthResult.Fail(AuthErrorKind.LinkConflict, "Account linking is not enabled on the server yet.");
                case "refresh_token_not_found": case "refresh_token_already_used": case "session_not_found": case "session_expired": case "bad_jwt":
                    return AuthResult.Fail(AuthErrorKind.SessionExpired, "Your session has expired. Please sign in again.");
                case "anonymous_provider_disabled":
                    return AuthResult.Fail(AuthErrorKind.ProviderDisabled, "Guest sign-in is not enabled on the server.");
            }
            if (status == 429) return AuthResult.Fail(AuthErrorKind.RateLimited, "Too many attempts. Please wait a minute and try again.");
            if (status == 401 || status == 403) return AuthResult.Fail(AuthErrorKind.SessionExpired, "Your session has expired. Please sign in again.");
            if (status >= 500) return AuthResult.Fail(AuthErrorKind.Network, "The account server is having problems. Try again shortly.");
            return AuthResult.Fail(AuthErrorKind.Unknown, string.IsNullOrEmpty(msg) ? "Sign-in failed." : msg);
        }
    }

    public sealed class PlayerRecord
    {
        public string UserId = "", PlayerId = "", Username = "", AvatarUrl = "";
        public int Level = 1;
        public long Xp;

        public static PlayerRecord FromDictionary(Dictionary<string, object> d) => d == null ? null : new PlayerRecord
        {
            UserId = MiniJson.Str(d, "user_id"), PlayerId = MiniJson.Str(d, "player_id"),
            Username = MiniJson.Str(d, "username"), AvatarUrl = MiniJson.Str(d, "avatar_url"),
            Level = (int)MiniJson.Number(d, "level", 1), Xp = (long)MiniJson.Number(d, "xp")
        };

        public bool IsValid
        {
            get
            {
                if (PlayerId.Length != 10 || UserId.Length == 0 || PlayerId[0] == '0') return false;
                foreach (var c in PlayerId) if (c < '0' || c > '9') return false;
                return true;
            }
        }
    }

    /// <summary>Thin GoTrue/PostgREST client. All callbacks run once, on whichever thread the transport uses.</summary>
    public sealed class SupabaseAuthClient
    {
        private readonly IHeaderTransport transport;
        private readonly SupabaseConfig config;
        private readonly Func<long> clock;

        public SupabaseAuthClient(IHeaderTransport transport, SupabaseConfig config, Func<long> clock = null)
        {
            this.transport = transport;
            this.config = config;
            this.clock = clock ?? (() => DateTimeOffset.UtcNow.ToUnixTimeSeconds());
        }

        private Dictionary<string, string> Headers() => new Dictionary<string, string> { ["apikey"] = config.AnonKey };

        private void Call(string method, string path, object body, string bearer, Action<HttpResponse> done)
        {
            if (!config.IsConfigured)
            {
                done(new HttpResponse { Status = 0, NetworkError = "not configured" });
                return;
            }
            transport.SendWithHeaders(method, config.Base + path, body == null ? null : MiniJson.Write(body), bearer, Headers(), done);
        }

        public void SignUpEmail(string email, string password, Action<AuthResult> done) =>
            Call("POST", "/auth/v1/signup", new Dictionary<string, object> { ["email"] = email, ["password"] = password }, null,
                r => done(ParseSessionResponse(r, true)));

        public void SignInEmail(string email, string password, Action<AuthResult> done) =>
            Call("POST", "/auth/v1/token?grant_type=password", new Dictionary<string, object> { ["email"] = email, ["password"] = password }, null,
                r => done(ParseSessionResponse(r, false)));

        public void SignInAnonymously(Action<AuthResult> done) =>
            Call("POST", "/auth/v1/signup", new Dictionary<string, object>(), null, r => done(ParseSessionResponse(r, false)));

        public void SignInWithIdToken(string provider, string idToken, string rawNonce, Action<AuthResult> done)
        {
            var body = new Dictionary<string, object> { ["provider"] = provider, ["id_token"] = idToken };
            if (!string.IsNullOrEmpty(rawNonce)) body["nonce"] = rawNonce;
            Call("POST", "/auth/v1/token?grant_type=id_token", body, null, r => done(ParseSessionResponse(r, false)));
        }

        public void Refresh(string refreshToken, Action<AuthResult> done) =>
            Call("POST", "/auth/v1/token?grant_type=refresh_token", new Dictionary<string, object> { ["refresh_token"] = refreshToken }, null,
                r => done(ParseSessionResponse(r, false)));

        public void ExchangeCode(string authCode, string verifier, Action<AuthResult> done) =>
            Call("POST", "/auth/v1/token?grant_type=pkce",
                new Dictionary<string, object> { ["auth_code"] = authCode, ["code_verifier"] = verifier }, null,
                r => done(ParseSessionResponse(r, false)));

        public void SignOut(string accessToken, Action<AuthResult> done) =>
            Call("POST", "/auth/v1/logout?scope=local", null, accessToken, r =>
                done(r.Status == 0 ? AuthErrors.FromResponse(r) : AuthResult.Success()));

        /// <summary>Browser URL that starts the provider flow for a new/returning sign-in (PKCE).</summary>
        public string BuildAuthorizeUrl(string provider, string verifier)
        {
            return config.Base + "/auth/v1/authorize?provider=" + Uri.EscapeDataString(provider) +
                   "&redirect_to=" + Uri.EscapeDataString(config.RedirectUrl) +
                   "&code_challenge=" + Pkce.Challenge(verifier) + "&code_challenge_method=s256";
        }

        /// <summary>Asks the server for a URL that links <paramref name="provider"/> to the signed-in user.</summary>
        public void BeginLink(string accessToken, string provider, string verifier, Action<AuthResult> done)
        {
            var path = "/auth/v1/user/identities/authorize?provider=" + Uri.EscapeDataString(provider) +
                       "&redirect_to=" + Uri.EscapeDataString(config.RedirectUrl) +
                       "&code_challenge=" + Pkce.Challenge(verifier) + "&code_challenge_method=s256&skip_http_redirect=true";
            Call("GET", path, null, accessToken, r =>
            {
                if (r.Status < 200 || r.Status >= 300) { done(AuthErrors.FromResponse(r)); return; }
                var d = MiniJson.TryParse(r.Body, out var o) ? MiniJson.AsObject(o) : null;
                var url = d == null ? "" : MiniJson.Str(d, "url");
                done(url.Length == 0 ? AuthResult.Fail(AuthErrorKind.Unknown, "Could not start linking.") : new AuthResult { Ok = true, Url = url });
            });
        }

        /// <summary>Upgrades an anonymous (guest) user to an e-mail/password account, keeping the same user id.</summary>
        public void UpgradeGuestWithEmail(string accessToken, string email, string password, Action<AuthResult> done) =>
            Call("PUT", "/auth/v1/user", new Dictionary<string, object> { ["email"] = email, ["password"] = password }, accessToken,
                r => done(r.Status >= 200 && r.Status < 300 ? AuthResult.Success() : AuthErrors.FromResponse(r)));

        public void GetUser(string accessToken, Action<AuthResult> done) =>
            Call("GET", "/auth/v1/user", null, accessToken, r =>
            {
                if (r.Status < 200 || r.Status >= 300) { done(AuthErrors.FromResponse(r)); return; }
                var s = new AuthSession();
                ApplyUser(s, MiniJson.TryParse(r.Body, out var o) ? MiniJson.AsObject(o) : null);
                done(AuthResult.Success(s));
            });

        /// <summary>Calls the idempotent ensure_player() RPC. The player id is generated server-side.</summary>
        public void EnsurePlayer(string accessToken, Action<AuthResult, PlayerRecord> done) =>
            Call("POST", "/rest/v1/rpc/ensure_player", new Dictionary<string, object>(), accessToken, r =>
            {
                if (r.Status < 200 || r.Status >= 300)
                {
                    var err = AuthErrors.FromResponse(r);
                    if (r.Status == 429 || r.Body.Contains("rate limited")) err = AuthResult.Fail(AuthErrorKind.RateLimited, "Too many attempts. Please wait a minute and try again.");
                    done(err, null);
                    return;
                }
                var rec = MiniJson.TryParse(r.Body, out var o) ? PlayerRecord.FromDictionary(MiniJson.AsObject(o)) : null;
                if (rec == null || !rec.IsValid) { done(AuthResult.Fail(AuthErrorKind.Unknown, "The server returned an invalid player profile."), null); return; }
                done(AuthResult.Success(), rec);
            });

        private AuthResult ParseSessionResponse(HttpResponse r, bool signUp)
        {
            if (r.Status < 200 || r.Status >= 300) return AuthErrors.FromResponse(r);
            if (!MiniJson.TryParse(r.Body, out var o) || !(MiniJson.AsObject(o) is Dictionary<string, object> d))
                return AuthResult.Fail(AuthErrorKind.Unknown, "Unexpected response from the account server.");

            var access = MiniJson.Str(d, "access_token");
            if (access.Length == 0)
            {
                // Sign-up with e-mail confirmation enabled returns the user but no session.
                if (signUp && d.ContainsKey("id")) return new AuthResult { Ok = true, NeedsEmailConfirmation = true };
                return AuthResult.Fail(AuthErrorKind.Unknown, "Unexpected response from the account server.");
            }

            var s = new AuthSession { AccessToken = access, RefreshToken = MiniJson.Str(d, "refresh_token") };
            long exp = Jwt.ExpiresAt(access);
            if (exp == 0) exp = clock() + (long)MiniJson.Number(d, "expires_in", 3600);
            s.ExpiresAtUnix = exp;
            if (d.TryGetValue("user", out var u)) ApplyUser(s, MiniJson.AsObject(u));
            var claims = Jwt.Payload(access);
            if (s.UserId.Length == 0 && claims != null) s.UserId = MiniJson.Str(claims, "sub");
            if (claims != null && MiniJson.Bool(claims, "is_anonymous")) s.IsAnonymous = true;
            if (s.UserId.Length == 0 || s.RefreshToken.Length == 0)
                return AuthResult.Fail(AuthErrorKind.Unknown, "Unexpected response from the account server.");
            return AuthResult.Success(s);
        }

        private static void ApplyUser(AuthSession s, Dictionary<string, object> user)
        {
            if (user == null) return;
            var id = MiniJson.Str(user, "id");
            if (id.Length > 0) s.UserId = id;
            s.Email = MiniJson.Str(user, "email");
            s.IsAnonymous = MiniJson.Bool(user, "is_anonymous");
            s.Providers.Clear();
            if (user.TryGetValue("identities", out var ids) && ids is List<object> list)
                foreach (var x in list)
                {
                    var p = MiniJson.Str(MiniJson.AsObject(x) ?? new Dictionary<string, object>(), "provider");
                    if (p.Length > 0 && !s.Providers.Contains(p)) s.Providers.Add(p);
                }
        }
    }

    // ------------------------------------------------------------------------------------------
    // Session storage + state machine
    // ------------------------------------------------------------------------------------------

    public interface ISessionStore
    {
        string Load();
        void Save(string json);
        void Clear();
    }

    public sealed class MemorySessionStore : ISessionStore
    {
        public string Value = "";
        public string Load() => Value;
        public void Save(string json) => Value = json ?? "";
        public void Clear() => Value = "";
    }

    public enum AuthState { NotConfigured, Restoring, SignedOut, Authenticating, SignedIn }

    /// <summary>
    /// Owns the verified account session. The lobby opens only when State == SignedIn, which requires both a valid
    /// Supabase session AND a profile returned by ensure_player(). Failures and cancellations return to SignedOut.
    /// </summary>
    public sealed class AuthService
    {
        public const int RefreshSkewSeconds = 90;
        public const int MaxPendingSeconds = 300;

        private sealed class Pending
        {
            public string Verifier = "";
            public bool IsLink;
            public string LinkUserId = "";
            public string Provider = "";
            public long StartedAt;
        }

        private readonly SupabaseAuthClient client;
        private readonly ISessionStore store;
        private readonly SupabaseConfig config;
        private readonly Func<long> clock;
        private Pending pending;
        private bool refreshing;

        public AuthState State { get; private set; }
        public AuthSession Session { get; private set; }
        public PlayerRecord Player { get; private set; }
        public string LastError { get; private set; } = "";
        public AuthErrorKind LastErrorKind { get; private set; }
        public string Notice { get; private set; } = "";
        public bool IsBusy => State == AuthState.Restoring || State == AuthState.Authenticating;
        public bool HasPendingOAuth => pending != null;
        public event Action Changed;

        public AuthService(SupabaseAuthClient client, ISessionStore store, SupabaseConfig config, Func<long> clock = null)
        {
            this.client = client; this.store = store; this.config = config;
            this.clock = clock ?? (() => DateTimeOffset.UtcNow.ToUnixTimeSeconds());
            State = config.IsConfigured ? AuthState.SignedOut : AuthState.NotConfigured;
        }

        private void Set(AuthState s) { State = s; Changed?.Invoke(); }

        private bool Blocked()
        {
            if (State == AuthState.NotConfigured)
            {
                LastError = "Account sign-in is not configured for this build.";
                LastErrorKind = AuthErrorKind.NotConfigured;
                return true;
            }
            return IsBusy;
        }

        private void Fail(AuthResult r)
        {
            LastError = r.Message; LastErrorKind = r.Kind;
            Session = null; Player = null; pending = null;
            Set(AuthState.SignedOut);
        }

        private void Begin()
        {
            LastError = ""; LastErrorKind = AuthErrorKind.None; Notice = "";
            Set(AuthState.Authenticating);
        }

        // ---- restore ----

        public void Restore()
        {
            if (State == AuthState.NotConfigured) return;
            var saved = AuthSession.FromJson(store.Load());
            if (saved == null) { store.Clear(); Set(AuthState.SignedOut); return; }
            Set(AuthState.Restoring);
            if (!saved.IsExpired(clock(), RefreshSkewSeconds)) { Finish(saved, true); return; }
            client.Refresh(saved.RefreshToken, r =>
            {
                if (r.Ok) { Finish(r.Session, true); return; }
                // A network failure keeps the stored session so the next launch can retry; anything else means it is dead.
                if (r.Kind != AuthErrorKind.Network) store.Clear();
                Fail(r);
            });
        }

        // ---- e-mail ----

        public void SignInEmail(string email, string password)
        {
            if (Blocked()) return;
            var err = ValidateEmailPassword(email, password, false);
            if (err != null) { Fail(err); return; }
            Begin();
            client.SignInEmail(email.Trim(), password, r => HandleSession(r));
        }

        public void SignUpEmail(string email, string password)
        {
            if (Blocked()) return;
            var err = ValidateEmailPassword(email, password, true);
            if (err != null) { Fail(err); return; }
            Begin();
            client.SignUpEmail(email.Trim(), password, r =>
            {
                if (r.Ok && r.NeedsEmailConfirmation)
                {
                    Notice = "Check your inbox and confirm your e-mail, then sign in.";
                    Set(AuthState.SignedOut);
                    return;
                }
                HandleSession(r);
            });
        }

        public static AuthResult ValidateEmailPassword(string email, string password, bool signUp)
        {
            email = (email ?? "").Trim();
            int at = email.IndexOf('@');
            if (email.Length < 5 || at < 1 || email.LastIndexOf('.') < at + 2 || email.Contains(" "))
                return AuthResult.Fail(AuthErrorKind.InvalidCredentials, "Enter a valid e-mail address.");
            if (string.IsNullOrEmpty(password))
                return AuthResult.Fail(AuthErrorKind.InvalidCredentials, "Enter your password.");
            if (signUp && password.Length < 8)
                return AuthResult.Fail(AuthErrorKind.WeakPassword, "Password must be at least 8 characters.");
            return null;
        }

        // ---- guest ----

        public void SignInGuest()
        {
            if (Blocked()) return;
            Begin();
            client.SignInAnonymously(r => HandleSession(r));
        }

        // ---- OAuth (Google / Apple, browser flow with PKCE) ----

        /// <summary>Returns the URL to open in the system browser, or null if sign-in could not start.</summary>
        public string BeginOAuth(string provider)
        {
            if (Blocked()) return null;
            Begin();
            pending = new Pending { Verifier = Pkce.NewVerifier(), Provider = provider, StartedAt = clock() };
            return client.BuildAuthorizeUrl(provider, pending.Verifier);
        }

        public void CancelOAuth()
        {
            if (pending == null) return;
            var wasLink = pending.IsLink;
            pending = null;
            LastError = ""; LastErrorKind = AuthErrorKind.Cancelled;
            Notice = "Sign-in cancelled.";
            // A cancelled link must not sign the user out; a cancelled sign-in returns to the sign-in screen.
            Set(wasLink && Session != null ? AuthState.SignedIn : AuthState.SignedOut);
        }

        /// <summary>Handles the redirect (deep link / loopback). Ignored unless a flow we started is pending.</summary>
        public void HandleCallback(string url)
        {
            if (pending == null) return; // unsolicited callbacks are ignored
            var p = pending;
            var args = ParseCallback(url);

            if (args.TryGetValue("error", out var e) || args.TryGetValue("error_code", out e))
            {
                args.TryGetValue("error_description", out var desc);
                var kind = e == "access_denied" ? AuthErrorKind.Cancelled : AuthErrorKind.Unknown;
                var code = args.TryGetValue("error_code", out var ec) ? ec : "";
                var mapped = code.Length > 0 ? AuthErrors.Map(400, code, desc ?? "") : null;
                var res = mapped != null && mapped.Kind != AuthErrorKind.Unknown ? mapped
                    : AuthResult.Fail(kind, kind == AuthErrorKind.Cancelled ? "Sign-in cancelled." : (desc ?? "Provider sign-in failed."));
                FailPending(p, res);
                return;
            }
            if (!args.TryGetValue("code", out var codeValue) || codeValue.Length == 0)
            {
                FailPending(p, AuthResult.Fail(AuthErrorKind.InvalidCallback, "The sign-in response was not valid. Please try again."));
                return;
            }

            pending = null;
            Set(AuthState.Authenticating);
            client.ExchangeCode(codeValue, p.Verifier, r =>
            {
                if (r.Ok && p.IsLink && r.Session.UserId != p.LinkUserId)
                {
                    // Linking must never silently switch accounts.
                    Fail(AuthResult.Fail(AuthErrorKind.LinkConflict, "That account belongs to a different Noxy profile. Nothing was changed."));
                    return;
                }
                HandleSession(r, p.IsLink);
            });
        }

        private void FailPending(Pending p, AuthResult res)
        {
            pending = null;
            if (p.IsLink && Session != null)
            {
                LastError = res.Message; LastErrorKind = res.Kind;
                Set(AuthState.SignedIn); // stay signed in; the link attempt failed
                return;
            }
            Fail(res);
        }

        public bool IsPendingExpired => pending != null && clock() - pending.StartedAt > MaxPendingSeconds;

        public static Dictionary<string, string> ParseCallback(string url)
        {
            var result = new Dictionary<string, string>();
            if (string.IsNullOrEmpty(url)) return result;
            foreach (var marker in new[] { '?', '#' })
            {
                int i = url.IndexOf(marker);
                if (i < 0) continue;
                var part = url.Substring(i + 1);
                int other = part.IndexOfAny(new[] { '#', '?' });
                if (other >= 0) part = part.Substring(0, other);
                foreach (var kv in part.Split('&'))
                {
                    if (kv.Length == 0) continue;
                    int eq = kv.IndexOf('=');
                    var k = Uri.UnescapeDataString((eq < 0 ? kv : kv.Substring(0, eq)).Replace('+', ' '));
                    var v = eq < 0 ? "" : Uri.UnescapeDataString(kv.Substring(eq + 1).Replace('+', ' '));
                    if (!result.ContainsKey(k)) result[k] = v;
                }
            }
            return result;
        }

        // ---- native token sign-in (Apple on iOS, Google on Android) ----

        public void SignInWithNativeToken(string provider, string idToken, string rawNonce)
        {
            if (Blocked()) return;
            if (string.IsNullOrEmpty(idToken)) { Fail(AuthResult.Fail(AuthErrorKind.Cancelled, "Sign-in cancelled.")); return; }
            Begin();
            client.SignInWithIdToken(provider, idToken, rawNonce, r => HandleSession(r));
        }

        // ---- linking ----

        /// <summary>Starts linking a provider to the CURRENT account. Returns the browser URL via callback.</summary>
        public void BeginLink(string provider, Action<AuthResult> urlReady)
        {
            if (State != AuthState.SignedIn || Session == null || IsBusy)
            {
                urlReady(AuthResult.Fail(AuthErrorKind.SessionExpired, "Sign in first, then link another provider."));
                return;
            }
            if (Session.Providers.Contains(provider))
            {
                urlReady(AuthResult.Fail(AuthErrorKind.LinkConflict, "This provider is already linked to your account."));
                return;
            }
            var p = new Pending { Verifier = Pkce.NewVerifier(), IsLink = true, LinkUserId = Session.UserId, Provider = provider, StartedAt = clock() };
            client.BeginLink(Session.AccessToken, provider, p.Verifier, r =>
            {
                if (r.Ok) pending = p;
                else { LastError = r.Message; LastErrorKind = r.Kind; Changed?.Invoke(); }
                urlReady(r);
            });
        }

        public void UpgradeGuestWithEmail(string email, string password, Action<AuthResult> done)
        {
            if (Session == null || !Session.IsAnonymous) { done(AuthResult.Fail(AuthErrorKind.Unknown, "Only guest accounts can be upgraded.")); return; }
            var err = ValidateEmailPassword(email, password, true);
            if (err != null) { done(err); return; }
            client.UpgradeGuestWithEmail(Session.AccessToken, email.Trim(), password, r =>
            {
                if (!r.Ok) { done(r); return; }
                RefreshIdentity(() => done(AuthResult.Success(Session)));
            });
        }

        private void RefreshIdentity(Action then)
        {
            if (Session == null) { then(); return; }
            client.GetUser(Session.AccessToken, r =>
            {
                if (r.Ok)
                {
                    Session.Email = r.Session.Email; Session.IsAnonymous = r.Session.IsAnonymous; Session.Providers = r.Session.Providers;
                    store.Save(Session.ToJson());
                    Changed?.Invoke();
                }
                then();
            });
        }

        // ---- sign out ----

        public void SignOut()
        {
            var token = Session?.AccessToken;
            Session = null; Player = null; pending = null;
            store.Clear();
            LastError = ""; Notice = "";
            Set(AuthState.SignedOut);
            // Best effort: local state is already cleared even if the server cannot be reached.
            if (!string.IsNullOrEmpty(token)) client.SignOut(token, _ => { });
        }

        // ---- token lifetime ----

        public void Tick()
        {
            if (State != AuthState.SignedIn || Session == null || refreshing) return;
            if (!Session.IsExpired(clock(), RefreshSkewSeconds)) return;
            refreshing = true;
            client.Refresh(Session.RefreshToken, r =>
            {
                refreshing = false;
                if (r.Ok)
                {
                    r.Session.Providers = Session.Providers.Count > 0 ? Session.Providers : r.Session.Providers;
                    if (r.Session.Email.Length == 0) r.Session.Email = Session.Email;
                    Session = r.Session;
                    store.Save(Session.ToJson());
                }
                else if (r.Kind != AuthErrorKind.Network)
                {
                    store.Clear();
                    Fail(r); // expired/revoked: back to the sign-in screen
                }
                // Network failure: stay signed in and retry on the next tick.
            });
        }

        /// <summary>Always returns a token that is valid right now, or empty if the session cannot be used.</summary>
        public string ValidAccessToken() =>
            Session != null && !Session.IsExpired(clock()) ? Session.AccessToken : "";

        // ---- shared completion ----

        private void HandleSession(AuthResult r, bool wasLink = false)
        {
            if (!r.Ok) { Fail(r); return; }
            Finish(r.Session, false, wasLink);
        }

        private void Finish(AuthSession s, bool restored, bool wasLink = false)
        {
            // Refresh identity details (providers, anonymous flag) then require a profile before opening the lobby.
            client.GetUser(s.AccessToken, ur =>
            {
                if (ur.Ok)
                {
                    s.Email = ur.Session.Email; s.Providers = ur.Session.Providers; s.IsAnonymous = ur.Session.IsAnonymous;
                }
                else if (ur.Kind == AuthErrorKind.SessionExpired)
                {
                    store.Clear(); Fail(ur); return;
                }
                else if (ur.Kind == AuthErrorKind.Network) { Fail(ur); return; }

                client.EnsurePlayer(s.AccessToken, (pr, rec) =>
                {
                    if (!pr.Ok) { if (pr.Kind == AuthErrorKind.SessionExpired) store.Clear(); Fail(pr); return; }
                    Session = s; Player = rec;
                    store.Save(s.ToJson());
                    Notice = wasLink ? "Account linked. Your progress and Player ID are unchanged." : "";
                    Set(AuthState.SignedIn);
                });
            });
        }
    }
}
