using System;
using System.Collections.Generic;
using System.Text;
using NUnit.Framework;
using Noxy.Core;

namespace Noxy.Tests
{
    /// <summary>
    /// In-memory stand-in for GoTrue + PostgREST. It models the behaviours the client depends on
    /// (one profile per user, PKCE verification, identity linking conflicts, token expiry) but it is NOT Supabase.
    /// </summary>
    public sealed class FakeSupabase : IHeaderTransport
    {
        public sealed class User
        {
            public string Id, Email, Password; public bool Anonymous;
            public List<string> Providers = new List<string>();
        }

        public long Now = 1_700_000_000;
        public int AccessLifetime = 3600;
        public bool Offline;
        public bool FailEnsurePlayer;
        public bool LinkConflict;
        public string LinkReturnsUser = "";          // simulate the provider returning a different account
        public readonly Dictionary<string, User> Users = new Dictionary<string, User>();
        public readonly Dictionary<string, string> PlayerIds = new Dictionary<string, string>();   // user id -> player id
        public readonly Dictionary<string, string> AccessTokens = new Dictionary<string, string>(); // token -> user id
        public readonly Dictionary<string, string> RefreshTokens = new Dictionary<string, string>();
        public readonly Dictionary<string, (string user, string challenge)> Codes = new Dictionary<string, (string, string)>();
        public readonly Dictionary<string, string> OAuthUsers = new Dictionary<string, string>(); // provider subject -> user id
        public int EnsureCalls;
        public string LastSignUpUsername = "";
        private int counter;
        private readonly Random rng = new Random(7);

        public void Send(string method, string url, string body, string bearer, Action<HttpResponse> done) =>
            SendWithHeaders(method, url, body, bearer, null, done);

        public void SendWithHeaders(string method, string url, string body, string bearer, IDictionary<string, string> headers, Action<HttpResponse> done)
        {
            if (Offline) { done(new HttpResponse { Status = 0, NetworkError = "offline" }); return; }
            if (headers == null || !headers.ContainsKey("apikey")) { done(Err(401, "no_api_key", "No API key")); return; }
            var path = new Uri(url).PathAndQuery;
            var d = string.IsNullOrEmpty(body) ? new Dictionary<string, object>() : MiniJson.AsObject(MiniJson.Parse(body));
            done(Route(method, path, d, bearer));
        }

        private static HttpResponse Ok(object o) => new HttpResponse { Status = 200, Body = MiniJson.Write(o) };
        private static HttpResponse Err(int status, string code, string msg) =>
            new HttpResponse { Status = status, Body = MiniJson.Write(new Dictionary<string, object> { ["error_code"] = code, ["msg"] = msg }) };

        private HttpResponse Route(string method, string path, Dictionary<string, object> b, string bearer)
        {
            if (path.StartsWith("/auth/v1/signup"))
            {
                var email = MiniJson.Str(b, "email");
                if (email.Length == 0) return Ok(SessionFor(NewUser(null, null, true)));
                foreach (var u in Users.Values) if (u.Email == email) return Err(422, "user_already_exists", "User already registered");
                if (MiniJson.Str(b, "password").Length < 8) return Err(422, "weak_password", "weak");
                if (b.TryGetValue("data", out var data)) LastSignUpUsername = MiniJson.Str(MiniJson.AsObject(data), "username");
                return Ok(SessionFor(NewUser(email, MiniJson.Str(b, "password"), false)));
            }
            if (path.StartsWith("/auth/v1/token?grant_type=password"))
            {
                foreach (var u in Users.Values)
                    if (u.Email == MiniJson.Str(b, "email") && u.Password == MiniJson.Str(b, "password")) return Ok(SessionFor(u));
                return Err(400, "invalid_credentials", "Invalid login credentials");
            }
            if (path.StartsWith("/auth/v1/token?grant_type=refresh_token"))
            {
                if (!RefreshTokens.TryGetValue(MiniJson.Str(b, "refresh_token"), out var uid)) return Err(400, "refresh_token_not_found", "Invalid Refresh Token");
                RefreshTokens.Remove(MiniJson.Str(b, "refresh_token"));
                return Ok(SessionFor(Users[uid]));
            }
            if (path.StartsWith("/auth/v1/token?grant_type=pkce"))
            {
                var code = MiniJson.Str(b, "auth_code");
                if (!Codes.TryGetValue(code, out var entry)) return Err(400, "bad_code_verifier", "invalid code");
                if (Pkce.Challenge(MiniJson.Str(b, "code_verifier")) != entry.challenge) return Err(400, "bad_code_verifier", "verifier mismatch");
                Codes.Remove(code);
                return Ok(SessionFor(Users[entry.user]));
            }
            if (path.StartsWith("/auth/v1/token?grant_type=id_token"))
            {
                var token = MiniJson.Str(b, "id_token");
                if (!token.StartsWith("valid:")) return Err(400, "invalid_credentials", "bad token");
                return Ok(SessionFor(UserForSubject(MiniJson.Str(b, "provider"), token.Substring(6))));
            }
            if (path.StartsWith("/auth/v1/logout")) { AccessTokens.Remove(bearer ?? ""); return new HttpResponse { Status = 204 }; }

            // everything below needs a valid access token
            if (!AccessTokens.TryGetValue(bearer ?? "", out var me) || Jwt.ExpiresAt(bearer) <= Now)
                return Err(401, "bad_jwt", "invalid JWT");

            if (path.StartsWith("/auth/v1/user/identities/authorize"))
            {
                if (LinkConflict) return Err(422, "identity_already_exists", "Identity is already linked to another user");
                var q = AuthService.ParseCallback(path);
                var target = LinkReturnsUser.Length > 0 ? LinkReturnsUser : me;
                var code = "code" + (++counter);
                Codes[code] = (target, q["code_challenge"]);
                if (target == me && !Users[me].Providers.Contains(q["provider"])) Users[me].Providers.Add(q["provider"]);
                return Ok(new Dictionary<string, object> { ["url"] = "https://idp.example/auth?code=" + code });
            }
            if (path.StartsWith("/auth/v1/user"))
            {
                var u = Users[me];
                if (method == "PUT") { u.Email = MiniJson.Str(b, "email"); u.Password = MiniJson.Str(b, "password"); u.Anonymous = false; if (!u.Providers.Contains("email")) u.Providers.Add("email"); return Ok(UserJson(u)); }
                return Ok(UserJson(u));
            }
            if (path.StartsWith("/rest/v1/rpc/ensure_player"))
            {
                EnsureCalls++;
                if (FailEnsurePlayer) return Err(500, "unexpected_failure", "db down");
                if (!PlayerIds.TryGetValue(me, out var pid))
                {
                    do pid = rng.Next(100000, 999999).ToString() + rng.Next(1000, 9999).ToString(); while (PlayerIds.ContainsValue(pid));
                    PlayerIds[me] = pid;
                }
                return Ok(new Dictionary<string, object> { ["user_id"] = me, ["player_id"] = pid, ["username"] = "Operator_" + pid, ["level"] = 1.0, ["xp"] = 0.0 });
            }
            return Err(404, "not_found", "no route " + path);
        }

        public User NewUser(string email, string password, bool anon)
        {
            var u = new User { Id = Guid.NewGuid().ToString(), Email = email ?? "", Password = password, Anonymous = anon };
            if (email != null) u.Providers.Add("email");
            Users[u.Id] = u;
            return u;
        }

        public User UserForSubject(string provider, string subject)
        {
            var key = provider + ":" + subject;
            if (!OAuthUsers.TryGetValue(key, out var id)) { var u = NewUser(subject + "@example.com", null, false); u.Providers.Clear(); u.Providers.Add(provider); OAuthUsers[key] = u.Id; id = u.Id; }
            return Users[id];
        }

        public string IssueCodeFor(User u, string challenge) { var c = "code" + (++counter); Codes[c] = (u.Id, challenge); return c; }

        private Dictionary<string, object> UserJson(User u)
        {
            var ids = new List<object>();
            foreach (var p in u.Providers) ids.Add(new Dictionary<string, object> { ["provider"] = p });
            return new Dictionary<string, object> { ["id"] = u.Id, ["email"] = u.Email, ["is_anonymous"] = u.Anonymous, ["identities"] = ids };
        }

        private Dictionary<string, object> SessionFor(User u)
        {
            var access = MakeJwt(u);
            var refresh = "r" + (++counter);
            AccessTokens[access] = u.Id; RefreshTokens[refresh] = u.Id;
            return new Dictionary<string, object> { ["access_token"] = access, ["refresh_token"] = refresh, ["expires_in"] = (double)AccessLifetime, ["user"] = UserJson(u) };
        }

        private string MakeJwt(User u)
        {
            string B(string s) => Pkce.Base64Url(Encoding.UTF8.GetBytes(s));
            var payload = MiniJson.Write(new Dictionary<string, object> { ["sub"] = u.Id, ["exp"] = (double)(Now + AccessLifetime), ["is_anonymous"] = u.Anonymous, ["n"] = (double)(++counter) });
            return B("{\"alg\":\"HS256\"}") + "." + B(payload) + ".sig";
        }

        public string Corrupt(string refreshToken) { RefreshTokens.Remove(refreshToken); return refreshToken; }
    }

    [TestFixture]
    public class AuthTests
    {
        private FakeSupabase fake;
        private MemorySessionStore store;
        private SupabaseConfig cfg;

        [SetUp]
        public void Setup()
        {
            fake = new FakeSupabase();
            store = new MemorySessionStore();
            cfg = new SupabaseConfig { Url = "https://example.supabase.co", AnonKey = "anon-public-key-for-tests-0123456789" };
        }

        private AuthService NewService(ISessionStore s = null) =>
            new AuthService(new SupabaseAuthClient(fake, cfg, () => fake.Now), s ?? store, cfg, () => fake.Now);

        // ---- primitives ----

        [Test]
        public void Pkce_MatchesRfc7636Vector() =>
            Assert.AreEqual("E9Melhoa2OwvFrEMTJguCHaoeK1t8URWbuGJSstw-cM", Pkce.Challenge("dBjftJeZ4CVP-mB92K27uhbUJU1p1r_wW1gFWFOEjXk"));

        [Test]
        public void Pkce_VerifiersAreUniqueAndUrlSafe()
        {
            var a = Pkce.NewVerifier(); var b = Pkce.NewVerifier();
            Assert.AreNotEqual(a, b);
            Assert.GreaterOrEqual(a.Length, 43);
            Assert.IsFalse(a.Contains("+") || a.Contains("/") || a.Contains("="));
        }

        [Test]
        public void Config_RequiresHttpsAndRealKey()
        {
            Assert.IsFalse(new SupabaseConfig().IsConfigured);
            Assert.IsFalse(new SupabaseConfig { Url = "http://x.supabase.co", AnonKey = "anon-public-key-for-tests-0123456789" }.IsConfigured);
            Assert.IsFalse(new SupabaseConfig { Url = "https://x.supabase.co", AnonKey = "YOUR_ANON_KEY_PLACEHOLDER_HERE" }.IsConfigured);
            Assert.IsTrue(cfg.IsConfigured);
            Assert.IsTrue(SupabaseConfig.FromJson("{\"url\":\"https://a.supabase.co\",\"anonKey\":\"anon-public-key-for-tests-0123456789\"}").IsConfigured);
            Assert.IsFalse(SupabaseConfig.FromJson("not json").IsConfigured);
        }

        [Test]
        public void Session_JsonRoundTrips()
        {
            var s = new AuthSession { AccessToken = "a", RefreshToken = "r", ExpiresAtUnix = 123, UserId = "u", Email = "e@x.io", IsAnonymous = true };
            s.Providers.Add("google");
            var back = AuthSession.FromJson(s.ToJson());
            Assert.AreEqual("u", back.UserId); Assert.IsTrue(back.IsAnonymous); Assert.AreEqual("google", back.Providers[0]); Assert.AreEqual(123, back.ExpiresAtUnix);
            Assert.IsNull(AuthSession.FromJson("{}"));
            Assert.IsNull(AuthSession.FromJson("garbage"));
        }

        [Test]
        public void ParseCallback_ReadsQueryAndFragment()
        {
            var q = AuthService.ParseCallback("noxy://auth-callback?code=abc%20d&x=1");
            Assert.AreEqual("abc d", q["code"]);
            var f = AuthService.ParseCallback("noxy://auth-callback#error=access_denied&error_description=User+denied");
            Assert.AreEqual("access_denied", f["error"]); Assert.AreEqual("User denied", f["error_description"]);
        }

        // ---- e-mail ----

        [Test]
        public void EmailSignUp_CreatesProfileWithTenDigitId()
        {
            var svc = NewService();
            svc.SignUpEmail("new@example.com", "password123");
            Assert.AreEqual(AuthState.SignedIn, svc.State, svc.LastError);
            Assert.AreEqual(10, svc.Player.PlayerId.Length);
            StringAssert.IsMatch("^[1-9][0-9]{9}$", svc.Player.PlayerId);
            Assert.AreEqual(fake.Users.Count, fake.PlayerIds.Count);
        }

        [Test]
        public void EmailSignUp_SendsTheRequestedUsernameAsAuthMetadata()
        {
            var svc = NewService();
            svc.SignUpEmail("new@example.com", "password123", "Noxy_Ranger");
            Assert.AreEqual(AuthState.SignedIn, svc.State, svc.LastError);
            Assert.AreEqual("Noxy_Ranger", fake.LastSignUpUsername);
        }

        [Test]
        public void EmailSignUp_RejectsInvalidRequestedUsername()
        {
            var svc = NewService();
            svc.SignUpEmail("new@example.com", "password123", "invalid name");
            Assert.AreEqual(AuthState.SignedOut, svc.State);
            Assert.AreEqual(0, fake.Users.Count);
            StringAssert.Contains("3-20", svc.LastError);
        }

        [Test]
        public void RepeatedSignIns_KeepSamePlayerId_NoDuplicateProfiles()
        {
            var svc = NewService();
            svc.SignUpEmail("a@example.com", "password123");
            var id = svc.Player.PlayerId;
            for (int i = 0; i < 4; i++)
            {
                svc.SignOut();
                svc.SignInEmail("a@example.com", "password123");
                Assert.AreEqual(AuthState.SignedIn, svc.State);
                Assert.AreEqual(id, svc.Player.PlayerId);
            }
            Assert.AreEqual(1, fake.PlayerIds.Count);
        }

        [Test]
        public void SamePlayerId_OnAnotherDevice()
        {
            var pc = NewService(new MemorySessionStore());
            pc.SignUpEmail("x@example.com", "password123");
            var phone = NewService(new MemorySessionStore());
            phone.SignInEmail("x@example.com", "password123");
            Assert.AreEqual(pc.Player.PlayerId, phone.Player.PlayerId);
            Assert.AreEqual(pc.Player.UserId, phone.Player.UserId);
        }

        [Test]
        public void WrongPassword_DoesNotSignIn()
        {
            var svc = NewService();
            svc.SignUpEmail("a@example.com", "password123"); svc.SignOut();
            svc.SignInEmail("a@example.com", "wrong-password");
            Assert.AreEqual(AuthState.SignedOut, svc.State);
            Assert.AreEqual(AuthErrorKind.InvalidCredentials, svc.LastErrorKind);
            Assert.IsNull(svc.Player); Assert.IsNull(svc.Session);
        }

        [Test]
        public void DuplicateEmail_IsReportedAsExistingAccount()
        {
            NewService().SignUpEmail("a@example.com", "password123");
            var svc = NewService(new MemorySessionStore());
            svc.SignUpEmail("a@example.com", "password123");
            Assert.AreEqual(AuthErrorKind.AccountExists, svc.LastErrorKind);
            Assert.AreEqual(AuthState.SignedOut, svc.State);
        }

        [Test]
        public void LocalValidation_RejectsBadInputWithoutNetwork()
        {
            fake.Offline = true;
            var svc = NewService();
            svc.SignInEmail("not-an-email", "x");
            Assert.AreEqual(AuthErrorKind.InvalidCredentials, svc.LastErrorKind);
            svc.SignUpEmail("ok@example.com", "short");
            Assert.AreEqual(AuthErrorKind.WeakPassword, svc.LastErrorKind);
        }

        [Test]
        public void NetworkFailure_NeverOpensLobby()
        {
            fake.Offline = true;
            var svc = NewService();
            svc.SignInEmail("a@example.com", "password123");
            Assert.AreEqual(AuthState.SignedOut, svc.State);
            Assert.AreEqual(AuthErrorKind.Network, svc.LastErrorKind);
            Assert.IsNull(svc.Player);
        }

        [Test]
        public void ProfileFailure_NeverOpensLobby_EvenWithValidSession()
        {
            fake.FailEnsurePlayer = true;
            var svc = NewService();
            svc.SignUpEmail("a@example.com", "password123");
            Assert.AreEqual(AuthState.SignedOut, svc.State);
            Assert.IsNull(svc.Player);
            Assert.AreEqual("", store.Value, "an unverified session must not be persisted");
        }

        // ---- OAuth (Google / Apple) ----

        private string Query(string url, string key) => AuthService.ParseCallback(url)[key];

        [TestCase("google")]
        [TestCase("apple")]
        public void OAuth_NewUserGetsProfile_ReturningUserKeepsId(string provider)
        {
            var svc = NewService();
            var url = svc.BeginOAuth(provider);
            StringAssert.Contains("provider=" + provider, url);
            StringAssert.Contains("code_challenge_method=s256", url);
            Assert.AreEqual(AuthState.Authenticating, svc.State);

            var user = fake.UserForSubject(provider, "subject-1");
            svc.HandleCallback("noxy://auth-callback?code=" + fake.IssueCodeFor(user, Query(url, "code_challenge")));
            Assert.AreEqual(AuthState.SignedIn, svc.State, svc.LastError);
            var id = svc.Player.PlayerId;
            StringAssert.IsMatch("^[1-9][0-9]{9}$", id);

            svc.SignOut();
            url = svc.BeginOAuth(provider);
            svc.HandleCallback("noxy://auth-callback?code=" + fake.IssueCodeFor(user, Query(url, "code_challenge")));
            Assert.AreEqual(id, svc.Player.PlayerId);
            Assert.AreEqual(1, fake.PlayerIds.Count);
        }

        [Test]
        public void OAuth_CodeWithWrongVerifierIsRejected()
        {
            var svc = NewService();
            svc.BeginOAuth("google");
            var user = fake.UserForSubject("google", "s");
            svc.HandleCallback("noxy://auth-callback?code=" + fake.IssueCodeFor(user, Pkce.Challenge("some-other-verifier")));
            Assert.AreEqual(AuthState.SignedOut, svc.State);
            Assert.IsNull(svc.Player);
        }

        [Test]
        public void OAuth_UserDenied_IsCancelledNotAnError()
        {
            var svc = NewService();
            svc.BeginOAuth("apple");
            svc.HandleCallback("noxy://auth-callback?error=access_denied&error_description=User+cancelled");
            Assert.AreEqual(AuthState.SignedOut, svc.State);
            Assert.AreEqual(AuthErrorKind.Cancelled, svc.LastErrorKind);
            Assert.IsNull(svc.Player);
        }

        [Test]
        public void OAuth_ProviderDisabled_GivesClearMessage()
        {
            var svc = NewService();
            svc.BeginOAuth("google");
            svc.HandleCallback("noxy://auth-callback?error=server_error&error_code=provider_disabled&error_description=x");
            Assert.AreEqual(AuthErrorKind.ProviderDisabled, svc.LastErrorKind);
        }

        [Test]
        public void OAuth_UnsolicitedCallbackIsIgnored()
        {
            var svc = NewService();
            var user = fake.UserForSubject("google", "attacker");
            svc.HandleCallback("noxy://auth-callback?code=" + fake.IssueCodeFor(user, "x"));
            Assert.AreEqual(AuthState.SignedOut, svc.State);
            Assert.IsNull(svc.Session);
        }

        [Test]
        public void OAuth_MissingCodeIsInvalidCallback()
        {
            var svc = NewService();
            svc.BeginOAuth("google");
            svc.HandleCallback("noxy://auth-callback");
            Assert.AreEqual(AuthErrorKind.InvalidCallback, svc.LastErrorKind);
        }

        [Test]
        public void OAuth_UserClosesBrowser_CancelReturnsToSignIn()
        {
            var svc = NewService();
            svc.BeginOAuth("google");
            svc.CancelOAuth();
            Assert.AreEqual(AuthState.SignedOut, svc.State);
            Assert.IsFalse(svc.HasPendingOAuth);
            Assert.AreEqual(AuthErrorKind.Cancelled, svc.LastErrorKind);
        }

        [Test]
        public void OAuth_PendingFlowExpires()
        {
            var svc = NewService();
            svc.BeginOAuth("google");
            Assert.IsFalse(svc.IsPendingExpired);
            fake.Now += AuthService.MaxPendingSeconds + 1;
            Assert.IsTrue(svc.IsPendingExpired);
        }

        [Test]
        public void NativeToken_ValidAndInvalid()
        {
            var svc = NewService();
            svc.SignInWithNativeToken("apple", "valid:apple-sub", "nonce");
            Assert.AreEqual(AuthState.SignedIn, svc.State);
            var id = svc.Player.PlayerId;
            svc.SignOut();
            svc.SignInWithNativeToken("apple", "valid:apple-sub", "nonce");
            Assert.AreEqual(id, svc.Player.PlayerId);
            svc.SignOut();
            svc.SignInWithNativeToken("apple", "forged", "nonce");
            Assert.AreEqual(AuthState.SignedOut, svc.State);
            svc.SignInWithNativeToken("apple", "", "nonce");
            Assert.AreEqual(AuthErrorKind.Cancelled, svc.LastErrorKind);
        }

        // ---- session restore / refresh / logout ----

        [Test]
        public void Restore_WithValidSession_SignsInWithoutPrompt()
        {
            var a = NewService();
            a.SignUpEmail("a@example.com", "password123");
            var b = NewService();
            b.Restore();
            Assert.AreEqual(AuthState.SignedIn, b.State);
            Assert.AreEqual(a.Player.PlayerId, b.Player.PlayerId);
        }

        [Test]
        public void Restore_ExpiredAccessToken_UsesRefreshToken()
        {
            var a = NewService();
            a.SignUpEmail("a@example.com", "password123");
            fake.Now += fake.AccessLifetime + 10;
            var b = NewService();
            b.Restore();
            Assert.AreEqual(AuthState.SignedIn, b.State, b.LastError);
            Assert.Greater(b.Session.ExpiresAtUnix, fake.Now);
        }

        [Test]
        public void Restore_RevokedRefreshToken_ClearsSession()
        {
            var a = NewService();
            a.SignUpEmail("a@example.com", "password123");
            fake.Corrupt(a.Session.RefreshToken);
            fake.Now += fake.AccessLifetime + 10;
            var b = NewService();
            b.Restore();
            Assert.AreEqual(AuthState.SignedOut, b.State);
            Assert.AreEqual("", store.Value);
        }

        [Test]
        public void Restore_Offline_KeepsStoredSessionForNextLaunch()
        {
            NewService().SignUpEmail("a@example.com", "password123");
            fake.Now += fake.AccessLifetime + 10;
            fake.Offline = true;
            var b = NewService();
            b.Restore();
            Assert.AreEqual(AuthState.SignedOut, b.State);
            Assert.AreNotEqual("", store.Value);
            fake.Offline = false;
            var c = NewService();
            c.Restore();
            Assert.AreEqual(AuthState.SignedIn, c.State);
        }

        [Test]
        public void Restore_NoSession_GoesToSignedOut()
        {
            var svc = NewService();
            svc.Restore();
            Assert.AreEqual(AuthState.SignedOut, svc.State);
        }

        [Test]
        public void Restore_CorruptStoredJson_IsDiscarded()
        {
            store.Value = "{\"access\":\"x\"}";
            var svc = NewService();
            svc.Restore();
            Assert.AreEqual(AuthState.SignedOut, svc.State);
            Assert.AreEqual("", store.Value);
        }

        [Test]
        public void Tick_RefreshesBeforeExpiry()
        {
            var svc = NewService();
            svc.SignUpEmail("a@example.com", "password123");
            var old = svc.Session.AccessToken;
            svc.Tick();
            Assert.AreEqual(old, svc.Session.AccessToken, "no refresh while the token is fresh");
            fake.Now += fake.AccessLifetime - AuthService.RefreshSkewSeconds + 1;
            svc.Tick();
            Assert.AreNotEqual(old, svc.Session.AccessToken);
            Assert.AreEqual(AuthState.SignedIn, svc.State);
            Assert.AreNotEqual("", svc.ValidAccessToken());
        }

        [Test]
        public void Tick_RevokedSession_ReturnsToSignIn()
        {
            var svc = NewService();
            svc.SignUpEmail("a@example.com", "password123");
            fake.Corrupt(svc.Session.RefreshToken);
            fake.Now += fake.AccessLifetime;
            svc.Tick();
            Assert.AreEqual(AuthState.SignedOut, svc.State);
            Assert.AreEqual("", store.Value);
        }

        [Test]
        public void Tick_NetworkDrop_KeepsSessionAndRetries()
        {
            var svc = NewService();
            svc.SignUpEmail("a@example.com", "password123");
            fake.Now += fake.AccessLifetime;
            fake.Offline = true;
            svc.Tick();
            Assert.AreEqual(AuthState.SignedIn, svc.State);
            fake.Offline = false;
            svc.Tick();
            Assert.Greater(svc.Session.ExpiresAtUnix, fake.Now);
        }

        [Test]
        public void SignOut_ClearsEverything()
        {
            var svc = NewService();
            svc.SignUpEmail("a@example.com", "password123");
            var token = svc.Session.AccessToken;
            svc.SignOut();
            Assert.AreEqual(AuthState.SignedOut, svc.State);
            Assert.IsNull(svc.Session); Assert.IsNull(svc.Player);
            Assert.AreEqual("", store.Value);
            Assert.IsFalse(fake.AccessTokens.ContainsKey(token));
            Assert.AreEqual("", svc.ValidAccessToken());
        }

        // ---- guest + linking ----

        [Test]
        public void Guest_UpgradeKeepsPlayerId()
        {
            var svc = NewService();
            svc.SignInGuest();
            Assert.IsTrue(svc.Session.IsAnonymous);
            var id = svc.Player.PlayerId; var uid = svc.Player.UserId;
            AuthResult result = null;
            svc.UpgradeGuestWithEmail("me@example.com", "password123", r => result = r);
            Assert.IsTrue(result.Ok, result.Message);
            Assert.IsFalse(svc.Session.IsAnonymous);
            Assert.AreEqual(id, svc.Player.PlayerId);
            var phone = NewService(new MemorySessionStore());
            phone.SignInEmail("me@example.com", "password123");
            Assert.AreEqual(id, phone.Player.PlayerId);
            Assert.AreEqual(uid, phone.Player.UserId);
        }

        [Test]
        public void Guest_CannotUpgradeNonGuest()
        {
            var svc = NewService();
            svc.SignUpEmail("a@example.com", "password123");
            AuthResult result = null;
            svc.UpgradeGuestWithEmail("b@example.com", "password123", r => result = r);
            Assert.IsFalse(result.Ok);
        }

        [Test]
        public void Link_AddsProvider_KeepsSameAccountAndPlayerId()
        {
            var svc = NewService();
            svc.SignUpEmail("a@example.com", "password123");
            var id = svc.Player.PlayerId; var uid = svc.Player.UserId;
            AuthResult res = null;
            svc.BeginLink("google", r => res = r);
            Assert.IsTrue(res.Ok, res.Message);
            Assert.AreEqual(AuthState.SignedIn, svc.State);
            svc.HandleCallback(res.Url);
            Assert.AreEqual(AuthState.SignedIn, svc.State, svc.LastError);
            Assert.AreEqual(uid, svc.Player.UserId);
            Assert.AreEqual(id, svc.Player.PlayerId);
            CollectionAssert.Contains(svc.Session.Providers, "google");
            Assert.AreEqual(1, fake.PlayerIds.Count);
        }

        [Test]
        public void Link_ProviderOwnedByAnotherAccount_ConflictKeepsCurrentAccount()
        {
            var svc = NewService();
            svc.SignUpEmail("a@example.com", "password123");
            var id = svc.Player.PlayerId;
            fake.LinkConflict = true;
            AuthResult res = null;
            svc.BeginLink("apple", r => res = r);
            Assert.IsFalse(res.Ok);
            Assert.AreEqual(AuthErrorKind.LinkConflict, res.Kind);
            Assert.AreEqual(AuthState.SignedIn, svc.State);
            Assert.AreEqual(id, svc.Player.PlayerId);
            StringAssert.Contains("sign in", res.Message.ToLowerInvariant());
        }

        [Test]
        public void Link_ReturningDifferentUser_IsRejectedWithoutSwitchingAccount()
        {
            var svc = NewService();
            svc.SignUpEmail("a@example.com", "password123");
            var other = fake.UserForSubject("google", "someone-else");
            fake.LinkReturnsUser = other.Id;
            AuthResult res = null;
            svc.BeginLink("google", r => res = r);
            svc.HandleCallback(res.Url);
            Assert.AreEqual(AuthState.SignedOut, svc.State);
            Assert.AreEqual(AuthErrorKind.LinkConflict, svc.LastErrorKind);
            Assert.IsNull(svc.Player);
        }

        [Test]
        public void Link_SameEmailDoesNotAutoMerge()
        {
            var email = NewService();
            email.SignUpEmail("same@example.com", "password123");
            var emailId = email.Player.PlayerId;
            // A Google identity that happens to share the e-mail address is a different account.
            var google = fake.UserForSubject("google", "same");
            var svc = NewService(new MemorySessionStore());
            svc.SignInWithNativeToken("google", "valid:same", null);
            Assert.AreNotEqual(emailId, svc.Player.PlayerId);
            Assert.AreEqual(2, fake.PlayerIds.Count);
        }

        [Test]
        public void Link_AlreadyLinkedOrSignedOut_IsRefused()
        {
            var svc = NewService();
            AuthResult res = null;
            svc.BeginLink("google", r => res = r);
            Assert.IsFalse(res.Ok);
            svc.SignUpEmail("a@example.com", "password123");
            svc.BeginLink("email", r => res = r);
            Assert.AreEqual(AuthErrorKind.LinkConflict, res.Kind);
        }

        [Test]
        public void Link_CancelledKeepsUserSignedIn()
        {
            var svc = NewService();
            svc.SignUpEmail("a@example.com", "password123");
            AuthResult res = null;
            svc.BeginLink("google", r => res = r);
            svc.HandleCallback("noxy://auth-callback?error=access_denied");
            Assert.AreEqual(AuthState.SignedIn, svc.State);
            Assert.IsNotNull(svc.Player);
        }

        [Test]
        public void NotConfigured_NeverSignsIn()
        {
            var empty = new SupabaseConfig();
            var svc = new AuthService(new SupabaseAuthClient(fake, empty), store, empty);
            Assert.AreEqual(AuthState.NotConfigured, svc.State);
            svc.Restore();
            svc.SignInEmail("a@example.com", "password123");
            Assert.AreEqual(AuthState.NotConfigured, svc.State);
            Assert.IsNull(svc.BeginOAuth("google"));
        }

        [Test]
        public void ErrorMapping_CoversCommonCodes()
        {
            Assert.AreEqual(AuthErrorKind.RateLimited, AuthErrors.Map(429, "over_request_rate_limit", "").Kind);
            Assert.AreEqual(AuthErrorKind.EmailNotConfirmed, AuthErrors.Map(400, "email_not_confirmed", "").Kind);
            Assert.AreEqual(AuthErrorKind.Network, AuthErrors.Map(503, "", "").Kind);
            Assert.AreEqual(AuthErrorKind.Network, AuthErrors.FromResponse(new HttpResponse { Status = 0 }).Kind);
        }
    }
}
