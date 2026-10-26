using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using NUnit.Framework;
using Noxy.Core;

namespace Noxy.IntegrationTests
{
    /// <summary>Blocking HttpClient transport (completes the callback before returning), for tests only.</summary>
    public sealed class HttpClientTransport : IHeaderTransport
    {
        private static readonly HttpClient Client = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };

        public void Send(string method, string url, string jsonBody, string bearerToken, Action<HttpResponse> done)
            => SendWithHeaders(method, url, jsonBody, bearerToken, null, done);

        public void SendWithHeaders(string method, string url, string jsonBody, string bearerToken,
            IDictionary<string, string> headers, Action<HttpResponse> done)
        {
            var response = new HttpResponse();
            try
            {
                var req = new HttpRequestMessage(new HttpMethod(method), url);
                if (!string.IsNullOrEmpty(bearerToken)) req.Headers.TryAddWithoutValidation("Authorization", "Bearer " + bearerToken);
                if (headers != null)
                    foreach (var h in headers) req.Headers.TryAddWithoutValidation(h.Key, h.Value);
                if (jsonBody != null) req.Content = new StringContent(jsonBody, Encoding.UTF8, "application/json");
                using var res = Client.Send(req);
                using var reader = new StreamReader(res.Content.ReadAsStream());
                response.Status = (int)res.StatusCode;
                response.Body = reader.ReadToEnd();
            }
            catch (Exception e)
            {
                response.Status = 0;
                response.NetworkError = e.Message;
            }
            done(response);
        }
    }

    /// <summary>Everything a game client instance owns, wired like AppServices does it.</summary>
    public sealed class TestClient
    {
        public ApiClient Api;
        public LocalProfileService Profile;
        public LocalConfigStore Config;
        public NotificationService Notes;
        public RemoteSocialService Social;
        public AccountService Account;
        public MemoryTokenStore Tokens;

        public TestClient(string url, MemoryTokenStore tokens = null)
        {
            Tokens = tokens ?? new MemoryTokenStore();
            Api = new ApiClient(new HttpClientTransport(), url);
            Profile = new LocalProfileService(new DefaultCosmeticCatalog(), null);
            Config = new LocalConfigStore(null, false);
            Notes = new NotificationService();
            Social = new RemoteSocialService(Api);
            Account = new AccountService(Api, Profile, Config, Notes, Social, Tokens);
        }

        public ServiceResult Register(string user, string pass = "password123") { ServiceResult r = default; Account.Register(user, pass, x => r = x); return r; }
        public ServiceResult SignIn(string user, string pass = "password123") { ServiceResult r = default; Account.SignIn(user, pass, x => r = x); return r; }
        public ServiceResult SignOut() { ServiceResult r = default; Account.SignOut(x => r = x); return r; }
        public ServiceResult Refresh() { ServiceResult r = default; Social.Refresh(x => r = x); return r; }
        public ServiceResult Do(Action<Action<ServiceResult>> call) { ServiceResult r = default; call(x => r = x); return r; }
        public ApiResult<SearchDto> Search(string q) { ApiResult<SearchDto> r = null; Social.Search(q, x => r = x); return r; }
        public ApiResult<PlayerProfileDto> GetProfile(string id) { ApiResult<PlayerProfileDto> r = null; Social.GetProfile(id, x => r = x); return r; }
    }

    [TestFixture]
    public class ClientBackendIntegrationTests
    {
        private Process server;
        private string url;
        private string dbFile;

        [OneTimeSetUp]
        public void StartBackend()
        {
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            int port = ((IPEndPoint)listener.LocalEndpoint).Port;
            listener.Stop();

            string root = Path.GetFullPath(Path.Combine(TestContext.CurrentContext.TestDirectory, "..", "..", "..", "..", ".."));
            string serverDir = Path.Combine(root, "server");
            if (!File.Exists(Path.Combine(serverDir, "src", "index.js")))
                Assert.Ignore("server/ not found at " + serverDir);

            dbFile = Path.Combine(Path.GetTempPath(), "noxy-it-" + Guid.NewGuid().ToString("N") + ".sqlite");
            var psi = new ProcessStartInfo("node", "--no-warnings src/index.js")
            {
                WorkingDirectory = serverDir, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false,
            };
            psi.Environment["PORT"] = port.ToString();
            psi.Environment["DB_PATH"] = dbFile;
            psi.Environment["NOXY_TEST_AUTH_LIMIT"] = "100000";
            server = Process.Start(psi);

            url = "http://127.0.0.1:" + port;
            var ready = DateTime.UtcNow.AddSeconds(15);
            var transport = new HttpClientTransport();
            while (DateTime.UtcNow < ready)
            {
                int status = 0;
                transport.Send("GET", url + "/api/me", null, null, r => status = r.Status);
                if (status == 401) return;
                System.Threading.Thread.Sleep(200);
            }
            Assert.Fail("Backend did not start.");
        }

        [OneTimeTearDown]
        public void StopBackend()
        {
            try { if (server != null && !server.HasExited) server.Kill(true); } catch (Exception) { }
            foreach (var ext in new[] { "", "-wal", "-shm" })
                try { File.Delete(dbFile + ext); } catch (Exception) { }
        }

        [Test]
        public void RegisterGivesTenDigitIdAndMirrorsServerProfile()
        {
            var c = new TestClient(url);
            Assert.IsTrue(c.Register("it_player_1").Success);
            Assert.AreEqual(AccountState.SignedIn, c.Account.State);
            Assert.That(c.Profile.Profile.publicId, Does.Match("^[1-9][0-9]{9}$"));
            Assert.IsTrue(c.Profile.IsServerBound);
            Assert.AreEqual("it_player_1", c.Profile.Profile.username);
            Assert.IsTrue(c.Profile.Owns("outfit_urban"));
            Assert.IsFalse(c.Profile.Owns("outfit_aurora"));
        }

        [Test]
        public void SameIdAfterSignOutSignInAndOnASecondDevice()
        {
            var tokens = new MemoryTokenStore();
            var phone = new TestClient(url, tokens);
            phone.Register("it_player_2");
            string id = phone.Profile.Profile.publicId;

            Assert.IsTrue(phone.SignOut().Success);
            Assert.IsFalse(phone.Profile.IsServerBound);
            Assert.AreEqual("", phone.Tokens.LoadToken());
            Assert.IsFalse(phone.Social.IsAvailable);

            var pc = new TestClient(url);
            Assert.IsTrue(pc.SignIn("it_player_2").Success);
            Assert.AreEqual(id, pc.Profile.Profile.publicId);
        }

        [Test]
        public void SavedSessionRestoresWithoutPassword()
        {
            var tokens = new MemoryTokenStore();
            var first = new TestClient(url, tokens);
            first.Register("it_player_3");
            string id = first.Profile.Profile.publicId;

            var second = new TestClient(url, tokens);
            ServiceResult r = default;
            second.Account.TryRestoreSession(x => r = x);
            Assert.IsTrue(r.Success, r.Error);
            Assert.AreEqual(id, second.Profile.Profile.publicId);
        }

        [Test]
        public void WrongPasswordAndDuplicateNamesGiveFriendlyErrors()
        {
            new TestClient(url).Register("it_player_4");
            var c = new TestClient(url);
            var bad = c.SignIn("it_player_4", "totally-wrong");
            Assert.IsFalse(bad.Success);
            StringAssert.Contains("Incorrect", bad.Error);
            Assert.AreEqual(AccountState.SignedOut, c.Account.State);
            Assert.IsFalse(c.Profile.IsServerBound);
            StringAssert.Contains("taken", c.Register("it_player_4").Error);
        }

        [Test]
        public void SearchFriendRequestAcceptAndRemove()
        {
            var a = new TestClient(url); a.Register("it_friend_a");
            var b = new TestClient(url); b.Register("it_friend_b");
            string bId = b.Profile.Profile.publicId, aId = a.Profile.Profile.publicId;

            var byId = a.Search(bId);
            Assert.IsTrue(byId.Ok);
            Assert.AreEqual("it_friend_b", byId.Value.results[0].username);
            Assert.AreEqual(0, a.Search("9999999999").Value.results.Count);
            Assert.AreEqual("player_not_found", a.GetProfile("1000000000").Code);
            Assert.IsFalse(a.Search("x").Ok);

            Assert.IsTrue(a.Do(d => a.Social.SendFriendRequest(bId, d)).Success);
            Assert.IsFalse(a.Do(d => a.Social.SendFriendRequest(bId, d)).Success); // duplicate
            Assert.IsFalse(a.Do(d => a.Social.SendFriendRequest(aId, d)).Success);  // self
            Assert.AreEqual(1, a.Social.Snapshot.Outgoing.Count);

            b.Refresh();
            Assert.AreEqual(1, b.Social.Snapshot.Incoming.Count);
            int requestId = b.Social.Snapshot.Incoming[0].requestId;
            Assert.IsTrue(b.Do(d => b.Social.RespondToRequest(requestId, true, d)).Success);
            a.Refresh();
            Assert.AreEqual(1, a.Social.Snapshot.Friends.Count);
            Assert.AreEqual("friend", a.GetProfile(bId).Value.relationship);

            Assert.IsTrue(a.Do(d => a.Social.RemoveFriend(bId, d)).Success);
            Assert.AreEqual(0, a.Social.Snapshot.Friends.Count);
        }

        [Test]
        public void PartyInviteAcceptLeaveAndRules()
        {
            var a = new TestClient(url); a.Register("it_party_a");
            var b = new TestClient(url); b.Register("it_party_b");
            var stranger = new TestClient(url); stranger.Register("it_party_s");
            string bId = b.Profile.Profile.publicId;

            Assert.IsFalse(a.Do(d => a.Social.Invite(bId, "battle_royale", d)).Success); // not friends yet
            a.Do(d => a.Social.SendFriendRequest(bId, d));
            b.Refresh();
            b.Do(d => b.Social.RespondToRequest(b.Social.Snapshot.Incoming[0].requestId, true, d));

            var unsupported = a.Do(d => a.Social.Invite(bId, "training", d));
            Assert.IsFalse(unsupported.Success);
            Assert.IsTrue(a.Do(d => a.Social.Invite(bId, "battle_royale", d)).Success);
            Assert.IsFalse(a.Do(d => a.Social.Invite(bId, "battle_royale", d)).Success); // cooldown

            b.Refresh();
            Assert.AreEqual(1, b.Social.Snapshot.Invitations.Count);
            Assert.AreEqual("it_party_a", b.Social.Snapshot.Invitations[0].fromUsername);
            Assert.IsFalse(stranger.Do(d => stranger.Social.RespondToInvitation(b.Social.Snapshot.Invitations[0].id, true, d)).Success);

            Assert.IsTrue(b.Do(d => b.Social.RespondToInvitation(b.Social.Snapshot.Invitations[0].id, true, d)).Success);
            Assert.IsTrue(b.Social.Snapshot.InParty);
            Assert.AreEqual(2, b.Social.Snapshot.Party.members.Count);

            a.Refresh();
            Assert.AreEqual(2, a.Social.Snapshot.Party.members.Count);
            Assert.IsTrue(b.Do(d => b.Social.SetReady(true, d)).Success);
            a.Refresh();
            Assert.IsTrue(a.Social.Snapshot.Party.members.Exists(m => m.username == "it_party_b" && m.ready));

            Assert.IsTrue(b.Do(d => b.Social.LeaveParty(d)).Success);
            Assert.IsFalse(b.Social.Snapshot.InParty);
        }

        [Test]
        public void ServerRejectsUnownedEquipAndClientResyncs()
        {
            var c = new TestClient(url);
            c.Register("it_vault_1");

            // Simulate a tampered client that believes it owns a premium item.
            c.Profile.Profile.ownedCosmeticIds.Add("outfit_aurora");
            var loadout = c.Profile.Profile.loadout.Clone();
            loadout.outfit = "outfit_aurora";
            Assert.IsTrue(c.Profile.EquipLoadout(loadout).Success); // client-side check passes because of the tampering

            // The server refused; the client must have reverted to server truth.
            Assert.AreEqual("outfit_urban", c.Profile.Profile.loadout.outfit);
            Assert.IsFalse(c.Profile.Owns("outfit_aurora"));
            Assert.IsTrue(c.Notes.Messages.Count > 0 && c.Notes.Messages[0].Severity == NotificationSeverity.Error);

            var legit = c.Profile.Profile.loadout.Clone();
            legit.outfit = "outfit_nightops";
            legit.mask = "mask_visor";
            Assert.IsTrue(c.Profile.EquipLoadout(legit).Success);

            var other = new TestClient(url);
            other.SignIn("it_vault_1");
            Assert.AreEqual("outfit_nightops", other.Profile.Profile.loadout.outfit);
            Assert.AreEqual("mask_visor", other.Profile.Profile.loadout.mask);
        }

        [Test]
        public void SettingsAndHudSyncAcrossDevices()
        {
            var a = new TestClient(url);
            a.Register("it_sync_1");
            a.Config.Settings.sensGeneral = 88;
            a.Config.Settings.radarNorthUp = true;
            a.Config.Settings.partyInvites = (int)Visibility.Nobody;
            a.Config.SaveSettings();
            Assert.IsTrue(a.Do(d => a.Account.PushSettings(d)).Success);

            var layout = a.Config.LoadHud(false, new UnityEngine.Vector2(1600, 900));
            layout.Get(HudElementId.Fire).x = 0.7f;
            layout.Get(HudElementId.Fire).scale = 1.3f;
            Assert.IsTrue(a.Do(d => a.Account.PushHud(layout, d)).Success);

            var b = new TestClient(url);
            Assert.IsTrue(b.SignIn("it_sync_1").Success);
            Assert.AreEqual(88, b.Config.Settings.sensGeneral);
            Assert.IsTrue(b.Config.Settings.radarNorthUp);
            Assert.AreEqual((int)Visibility.Nobody, b.Config.Settings.partyInvites);
            var pulled = b.Config.LoadHud(false, new UnityEngine.Vector2(1600, 900));
            Assert.AreEqual(1.3f, pulled.Get(HudElementId.Fire).scale, 1e-3f);
        }

        [Test]
        public void PrivacySettingBlocksInvitesServerSide()
        {
            var a = new TestClient(url); a.Register("it_priv_a");
            var b = new TestClient(url); b.Register("it_priv_b");
            string bId = b.Profile.Profile.publicId;
            a.Do(d => a.Social.SendFriendRequest(bId, d));
            b.Refresh();
            b.Do(d => b.Social.RespondToRequest(b.Social.Snapshot.Incoming[0].requestId, true, d));

            b.Config.Settings.partyInvites = (int)Visibility.Nobody;
            b.Config.SaveSettings();
            b.Do(d => b.Account.PushSettings(d));
            var r = a.Do(d => a.Social.Invite(bId, "battle_royale", d));
            Assert.IsFalse(r.Success);
            StringAssert.Contains("not accepting", r.Error);
        }

        [Test]
        public void ProfileViewShowsOnlyPublicFields()
        {
            var a = new TestClient(url); a.Register("it_pub_a");
            var b = new TestClient(url); b.Register("it_pub_b");
            var p = a.GetProfile(b.Profile.Profile.publicId);
            Assert.IsTrue(p.Ok);
            Assert.AreEqual("it_pub_b", p.Value.username);
            Assert.AreEqual("outfit_urban", p.Value.loadout.outfit);
        }

        [Test]
        public void ChangeUsernameKeepsId()
        {
            var a = new TestClient(url); a.Register("it_rename_a");
            string id = a.Profile.Profile.publicId;
            ServiceResult r = default;
            a.Account.ChangeUsername("it_renamed_a", x => r = x);
            Assert.IsTrue(r.Success, r.Error);
            Assert.AreEqual("it_renamed_a", a.Profile.Profile.username);
            Assert.AreEqual(id, a.Profile.Profile.publicId);
        }

        [Test]
        public void DeleteAccountRequiresPasswordThenRemovesAccount()
        {
            var a = new TestClient(url); a.Register("it_delete_a");
            ServiceResult r = default;
            a.Account.DeleteAccount("wrong-password", x => r = x);
            Assert.IsFalse(r.Success);
            Assert.AreEqual(AccountState.SignedIn, a.Account.State);
            a.Account.DeleteAccount("password123", x => r = x);
            Assert.IsTrue(r.Success);
            Assert.AreEqual(AccountState.SignedOut, a.Account.State);
            Assert.IsFalse(new TestClient(url).SignIn("it_delete_a").Success);
        }

        [Test]
        public void OnlineProfileCannotGrantItselfCurrency()
        {
            var a = new TestClient(url); a.Register("it_reward_a");
            int credits = a.Profile.Profile.credits;
            Assert.IsFalse(a.Profile.CanClaimDailyReward);
            Assert.IsFalse(a.Profile.ClaimDailyReward().Success);
            Assert.AreEqual(credits, a.Profile.Profile.credits);
        }

        [Test]
        public void UnreachableServerFailsGracefullyAndOfflineProfileSurvives()
        {
            var c = new TestClient("http://127.0.0.1:1");
            var before = c.Profile.Profile.username;
            var r = c.SignIn("anyone");
            Assert.IsFalse(r.Success);
            StringAssert.Contains("Cannot reach", r.Error);
            Assert.AreEqual(AccountState.SignedOut, c.Account.State);
            Assert.AreEqual(before, c.Profile.Profile.username);
            Assert.IsFalse(c.Social.IsAvailable);
            c.Account.Tick(30f); // must not throw while signed out

            var offline = new OfflineSocialService();
            ServiceResult res = default;
            offline.Invite("1234567890", "battle_royale", x => res = x);
            Assert.IsFalse(res.Success);
        }

        [Test]
        public void InvalidServerUrlIsRejectedBeforeAnyRequest()
        {
            var c = new TestClient("not a url");
            var r = c.SignIn("x");
            Assert.IsFalse(r.Success);
            StringAssert.Contains("http", r.Error);
        }

        [Test]
        public void PollingPicksUpInvitationsAndNotifications()
        {
            var a = new TestClient(url); a.Register("it_poll_a");
            var b = new TestClient(url); b.Register("it_poll_b");
            string bId = b.Profile.Profile.publicId;
            a.Do(d => a.Social.SendFriendRequest(bId, d));

            b.Account.Tick(0f);
            b.Account.Tick(AccountService.PollSeconds + 0.1f);
            Assert.AreEqual(1, b.Social.Snapshot.Incoming.Count);
            Assert.IsTrue(System.Linq.Enumerable.Any(b.Notes.Messages, n => n.Body.Contains("it_poll_a")));
        }
    }

    [TestFixture]
    public class MiniJsonTests
    {
        [Test]
        public void RoundTripsNestedData()
        {
            var map = new Dictionary<string, object>
            {
                ["s"] = "he said \"hi\"\n", ["n"] = 1.5, ["i"] = 7, ["b"] = true, ["z"] = null,
                ["o"] = new Dictionary<string, object> { ["x"] = 0.25f }, ["a"] = new List<object> { 1, "two" }
            };
            var parsed = (Dictionary<string, object>)MiniJson.Parse(MiniJson.Write(map));
            Assert.AreEqual("he said \"hi\"\n", parsed["s"]);
            Assert.AreEqual(7.0, parsed["i"]);
            Assert.AreEqual(true, parsed["b"]);
            Assert.IsNull(parsed["z"]);
            Assert.AreEqual(0.25, ((Dictionary<string, object>)parsed["o"])["x"]);
            Assert.AreEqual(2, ((List<object>)parsed["a"]).Count);
        }

        [Test]
        public void RejectsMalformedJson()
        {
            Assert.IsFalse(MiniJson.TryParse("{\"a\":", out _));
            Assert.IsFalse(MiniJson.TryParse("{\"a\":1} trailing", out _));
            Assert.IsFalse(MiniJson.TryParse("[1,2", out _));
        }

        [Test]
        public void WriterRejectsNaN()
        {
            Assert.Throws<ArgumentException>(() => MiniJson.Write(new Dictionary<string, object> { ["x"] = float.NaN }));
        }
    }
}
