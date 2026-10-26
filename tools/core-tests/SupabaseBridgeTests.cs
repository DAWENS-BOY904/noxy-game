using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using NUnit.Framework;
using Noxy.Core;

namespace Noxy.IntegrationTests
{
    /// <summary>Sends requests for the fake project host to the local fake instead (the client insists on https://).</summary>
    public sealed class RewritingTransport : IHeaderTransport
    {
        private readonly IHeaderTransport inner = new HttpClientTransport();
        private readonly string from, to;
        public RewritingTransport(string from, string to) { this.from = from; this.to = to; }
        public void Send(string method, string url, string jsonBody, string bearerToken, Action<HttpResponse> done) =>
            inner.Send(method, url.Replace(from, to), jsonBody, bearerToken, done);
        public void SendWithHeaders(string method, string url, string jsonBody, string bearerToken, IDictionary<string, string> headers, Action<HttpResponse> done) =>
            inner.SendWithHeaders(method, url.Replace(from, to), jsonBody, bearerToken, headers, done);
    }

    /// <summary>
    /// End to end: real AuthService + SupabaseAuthClient -> (fake GoTrue/PostgREST) and real AccountService -> real Node backend
    /// running in Supabase mode, which verifies the same token against the same fake. Proves our logic; it is NOT a test
    /// against a real Supabase project (see tools/supabase-live-check for that).
    /// </summary>
    [TestFixture]
    public class SupabaseBridgeIntegrationTests
    {
        private const string FakeHost = "https://fake-project.supabase.test";
        private const string AnonKey = "test-anon-key-0123456789abcdef";
        private const string ServerKey = "it-server-key-12345";

        private Process fake, node;
        private string fakeUrl, nodeUrl, dbFile;

        private static int FreePort()
        {
            var l = new TcpListener(IPAddress.Loopback, 0); l.Start();
            int p = ((IPEndPoint)l.LocalEndpoint).Port; l.Stop(); return p;
        }

        private static string RepoRoot() => Path.GetFullPath(Path.Combine(TestContext.CurrentContext.TestDirectory, "..", "..", "..", "..", ".."));

        [OneTimeSetUp]
        public void Start()
        {
            string root = RepoRoot();
            string serverDir = Path.Combine(root, "server");
            string fakeScript = Path.Combine(root, "tools", "core-tests", "fake-supabase.js");
            if (!File.Exists(Path.Combine(serverDir, "src", "index.js")) || !File.Exists(fakeScript)) Assert.Ignore("server/ or fake-supabase.js not found");

            int fakePort = FreePort();
            var fpsi = new ProcessStartInfo("node", "\"" + fakeScript + "\"") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
            fpsi.Environment["PORT"] = fakePort.ToString();
            fpsi.Environment["FAKE_ANON_KEY"] = AnonKey;
            fake = Process.Start(fpsi);
            fakeUrl = "http://127.0.0.1:" + fakePort;
            Assert.IsTrue(fake.StandardOutput.ReadLine()?.StartsWith("READY") == true, "fake supabase did not start");

            int port = FreePort();
            dbFile = Path.Combine(Path.GetTempPath(), "noxy-sb-" + Guid.NewGuid().ToString("N") + ".sqlite");
            var psi = new ProcessStartInfo("node", "--no-warnings src/index.js") { WorkingDirectory = serverDir, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
            psi.Environment["PORT"] = port.ToString();
            psi.Environment["DB_PATH"] = dbFile;
            psi.Environment["SUPABASE_URL"] = fakeUrl;
            psi.Environment["SUPABASE_ANON_KEY"] = AnonKey;
            psi.Environment["NOXY_SUPABASE_CACHE_SECONDS"] = "1";
            psi.Environment["NOXY_MATCH_SERVER_KEY"] = ServerKey;
            psi.Environment["NOXY_TEST_AUTH_LIMIT"] = "100000";
            node = Process.Start(psi);
            nodeUrl = "http://127.0.0.1:" + port;

            var transport = new HttpClientTransport();
            var until = DateTime.UtcNow.AddSeconds(15);
            while (DateTime.UtcNow < until)
            {
                int status = 0; string body = "";
                transport.Send("GET", nodeUrl + "/api/health", null, null, r => { status = r.Status; body = r.Body; });
                if (status == 200) { StringAssert.Contains("\"authMode\":\"supabase\"", body); return; }
                Thread.Sleep(200);
            }
            Assert.Fail("Backend did not start in supabase mode.");
        }

        [OneTimeTearDown]
        public void Stop()
        {
            foreach (var p in new[] { node, fake }) try { if (p != null && !p.HasExited) p.Kill(true); } catch (Exception) { }
            foreach (var ext in new[] { "", "-wal", "-shm" }) try { File.Delete(dbFile + ext); } catch (Exception) { }
        }

        // ---- helpers ----

        private sealed class Device
        {
            public ApiClient Api; public LocalProfileService Profile; public AccountService Account; public AuthService Auth;
            public RemoteEconomyService Economy; public RemoteCommunityService Community; public MemorySessionStore Store;
            public int UnauthorizedCalls;

            public Device(string nodeUrl, string fakeUrl, MemorySessionStore store = null, bool autoSync = true)
            {
                Store = store ?? new MemorySessionStore();
                Api = new ApiClient(new HttpClientTransport(), nodeUrl);
                Profile = new LocalProfileService(new DefaultCosmeticCatalog(), null);
                var notes = new NotificationService();
                var social = new RemoteSocialService(Api);
                Account = new AccountService(Api, Profile, new LocalConfigStore(null, false), notes, social, new MemoryTokenStore());
                Economy = new RemoteEconomyService(Api); Account.Economy = Economy;
                Community = new RemoteCommunityService(Api); Account.Community = Community;
                var cfg = new SupabaseConfig { Url = FakeHost, AnonKey = AnonKey };
                Auth = new AuthService(new SupabaseAuthClient(new RewritingTransport(FakeHost, fakeUrl), cfg), Store, cfg);
                Account.OnUnauthorized = () => { UnauthorizedCalls++; Auth.Revalidate(); };
                if (autoSync) Auth.Changed += () => Account.SyncWithAuth(Auth);
            }
        }

        private Device NewDevice(MemorySessionStore store = null, bool autoSync = true) => new Device(nodeUrl, fakeUrl, store, autoSync);
        private static string Email() => "it_" + Guid.NewGuid().ToString("N").Substring(0, 10) + "@example.test";
        private static string Name() => "Rn_" + Guid.NewGuid().ToString("N").Substring(0, 8);

        private HttpResponse Raw(string method, string baseUrl, string path, string body = null, string bearer = null, Dictionary<string, string> headers = null)
        {
            HttpResponse res = null;
            new HttpClientTransport().SendWithHeaders(method, baseUrl + path, body, bearer, headers, r => res = r);
            return res;
        }
        private void Fake(string path, string body) => Raw("POST", fakeUrl, path, body);

        // ---- tests ----

        [Test]
        public void VerifiedSupabaseLoginOpensAGameServerSessionWithTheSamePlayerId()
        {
            var d = NewDevice();
            d.Auth.SignUpEmail(Email(), "password123");
            Assert.AreEqual(AuthState.SignedIn, d.Auth.State, d.Auth.LastError);
            Assert.AreEqual(AccountState.SignedIn, d.Account.State, d.Account.LastError);
            Assert.IsTrue(d.Account.SupabaseBound);
            Assert.AreEqual(10, d.Auth.Player.PlayerId.Length);
            Assert.AreEqual(d.Auth.Player.PlayerId, d.Account.PublicId, "game server uses the Player ID from the Supabase profile");
            Assert.AreEqual(d.Auth.Player.PlayerId, d.Profile.Profile.publicId);

            ServiceResult wallet = default; d.Economy.RefreshWallet(r => wallet = r);
            Assert.IsTrue(wallet.Success, wallet.Error);

            // there is no second identity system: password accounts are off and an opaque token means nothing
            Assert.AreEqual(403, Raw("POST", nodeUrl, "/api/auth/register", "{\"username\":\"someone\",\"password\":\"password123\"}").Status);
            Assert.AreEqual(401, Raw("GET", nodeUrl, "/api/me", null, "abcdefghijklmnopqrstuvwxyz0123456789").Status);
        }

        [Test]
        public void SecondDeviceGetsTheSameIdAndRenamesArePersistedInSupabase()
        {
            string email = Email(); string name = Name();
            var a = NewDevice();
            a.Auth.SignUpEmail(email, "password123");
            string id = a.Auth.Player.PlayerId;

            var b = NewDevice();
            b.Auth.SignInEmail(email, "password123");
            Assert.AreEqual(AccountState.SignedIn, b.Account.State, b.Account.LastError);
            Assert.AreEqual(id, b.Account.PublicId);
            Assert.AreEqual(id, b.Auth.Player.PlayerId);

            ServiceResult rename = default; a.Account.ChangeUsername(name, r => rename = r);
            Assert.IsTrue(rename.Success, rename.Error);
            var stored = Raw("POST", fakeUrl, "/__test/profile", "{\"userId\":\"" + a.Auth.Session.UserId + "\"}");
            StringAssert.Contains(name, stored.Body, "the new username is in the Supabase profile, not just in the game server");

            Thread.Sleep(1300); // device B's verification cache window on the game server
            ApiResult<MeDto> seen = null; b.Api.Request<MeDto>("GET", "/api/me", null, r => seen = r);
            Assert.IsTrue(seen.Ok, seen.Friendly);
            Assert.AreEqual(name, seen.Value.username, "other device sees the change");
            Assert.AreEqual(id, seen.Value.publicId);

            // sign out everywhere, then sign back in on a fresh install
            a.Auth.SignOut();
            Assert.AreEqual(AccountState.SignedOut, a.Account.State);
            Assert.IsFalse(a.Account.SupabaseBound);
            var c = NewDevice();
            c.Auth.SignInEmail(email, "password123");
            Assert.AreEqual(name, c.Account.Username);
            Assert.AreEqual(id, c.Account.PublicId);
        }

        [Test]
        public void FailedLoginNeverOpensAGameServerSession()
        {
            string email = Email();
            NewDevice().Auth.SignUpEmail(email, "password123");

            var d = NewDevice();
            d.Auth.SignInEmail(email, "wrong-password");
            Assert.AreEqual(AuthState.SignedOut, d.Auth.State);
            Assert.AreEqual(AuthErrorKind.InvalidCredentials, d.Auth.LastErrorKind);
            Assert.AreEqual(AccountState.SignedOut, d.Account.State);
            Assert.IsFalse(d.Account.SupabaseBound);
            Assert.IsFalse(d.Community.IsAvailable);

            // a game-server session without a valid Supabase token is refused, with an honest reason
            ServiceResult r = default;
            d.Account.SignInWithSupabase(() => "", x => r = x);
            Assert.IsFalse(r.Success);
            Assert.AreEqual(AccountState.SignedOut, d.Account.State);
            StringAssert.Contains("game server", d.Community.UnavailableReason);
        }

        [Test]
        public void SupabaseOutageMeansNoSessionAndOnlineFeaturesRecoverByThemselves()
        {
            var d = NewDevice(autoSync: false);
            d.Auth.SignUpEmail(Email(), "password123");
            Assert.AreEqual(AuthState.SignedIn, d.Auth.State);

            Fake("/__test/down", "{\"down\":true}");
            try
            {
                ServiceResult r = default;
                d.Account.SignInWithSupabase(d.Auth.ValidAccessToken, x => r = x);
                Assert.IsFalse(r.Success, "the game server could not verify the session, so it must not claim success");
                Assert.AreEqual(AccountState.SignedOut, d.Account.State);
                StringAssert.Contains("unavailable", d.Account.UnavailableReason);
                Assert.IsFalse(d.Economy.IsAvailable);
            }
            finally { Fake("/__test/down", "{\"down\":false}"); }

            d.Account.Tick(16f); // retry timer
            Assert.AreEqual(AccountState.SignedIn, d.Account.State, d.Account.LastError);
            Assert.IsTrue(d.Economy.IsAvailable);
        }

        [Test]
        public void RevokedSessionIsDetectedAndEverythingDropsToSignedOut()
        {
            var d = NewDevice();
            d.Auth.SignUpEmail(Email(), "password123");
            Assert.AreEqual(AccountState.SignedIn, d.Account.State);

            Fake("/__test/revoke", "{\"userId\":\"" + d.Auth.Session.UserId + "\"}"); // e.g. signed out from another device
            Thread.Sleep(1300);
            d.Account.Tick(11f); // heartbeat -> 401 -> asks the auth layer, which tries to refresh and is refused

            Assert.GreaterOrEqual(d.UnauthorizedCalls, 1);
            Assert.AreEqual(AuthState.SignedOut, d.Auth.State);
            Assert.AreEqual(AccountState.SignedOut, d.Account.State);
            Assert.IsFalse(d.Economy.IsAvailable);
            Assert.AreEqual("", d.Store.Value, "the dead session is not kept for restore");
        }

        [Test]
        public void SessionRestoreAfterRestartUsesTheSavedSupabaseSession()
        {
            string email = Email();
            var first = NewDevice();
            first.Auth.SignUpEmail(email, "password123");
            string id = first.Account.PublicId;

            var restarted = NewDevice(first.Store); // same "device storage", new app instance
            restarted.Auth.Restore();
            Assert.AreEqual(AuthState.SignedIn, restarted.Auth.State, restarted.Auth.LastError);
            Assert.AreEqual(AccountState.SignedIn, restarted.Account.State, restarted.Account.LastError);
            Assert.AreEqual(id, restarted.Account.PublicId);
        }

        [Test]
        public void ServerListShowsOnlyVerifiedLiveServers_AndClientsCannotRegisterServers()
        {
            var d = NewDevice();
            d.Auth.SignUpEmail(Email(), "password123");
            string sid = "it-" + Guid.NewGuid().ToString("N").Substring(0, 8);
            var key = new Dictionary<string, string> { ["x-noxy-server-key"] = ServerKey };
            string spec = "{\"serverId\":\"" + sid + "\",\"name\":\"IT Server\",\"region\":\"eu-west\",\"mode\":\"battle_royale\",\"host\":\"gs.example.test\",\"port\":7777,\"capacity\":60}";

            var reg = Raw("POST", nodeUrl, "/api/internal/servers/register", spec, null, key);
            Assert.AreEqual(201, reg.Status, reg.Body);
            string token = MiniJson.Str(MiniJson.AsObject(ParseJson(reg.Body)), "instanceToken");

            ApiResult<ServerListDto> before = null; d.Community.LoadServers(null, r => before = r);
            Assert.IsTrue(before.Ok, before.Friendly);
            Assert.IsFalse(before.Value.servers.Exists(s => s.id == sid), "registered but never reported healthy");

            Assert.AreEqual(200, Raw("POST", nodeUrl, "/api/internal/servers/heartbeat", "{\"serverId\":\"" + sid + "\",\"instanceToken\":\"" + token + "\",\"players\":5}", null, key).Status);
            ApiResult<ServerListDto> after = null; d.Community.LoadServers(null, r => after = r);
            var found = after.Value.servers.Find(s => s.id == sid);
            Assert.IsNotNull(found);
            Assert.AreEqual(5, found.players); Assert.AreEqual(60, found.capacity); Assert.AreEqual("online", found.status);

            // a signed-in player is not a server
            var asPlayer = Raw("POST", nodeUrl, "/api/internal/servers/register", spec.Replace(sid, sid + "x"), d.Auth.ValidAccessToken());
            Assert.AreEqual(401, asPlayer.Status);
        }

        private static object ParseJson(string json) { Assert.IsTrue(MiniJson.TryParse(json, out var o)); return o; }
    }
}
