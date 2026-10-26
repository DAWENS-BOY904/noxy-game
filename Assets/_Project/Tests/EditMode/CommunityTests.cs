using NUnit.Framework;
using Noxy.Core;

namespace Noxy.Tests
{
    public class CommunityTests
    {
        [Test]
        public void RoleChecksOnlyDecideWhichButtonsShow()
        {
            Assert.IsTrue(ClanRoles.CanKick("leader", "officer"));
            Assert.IsFalse(ClanRoles.CanKick("officer", "officer"));
            Assert.IsFalse(ClanRoles.CanKick("member", "member"));
            Assert.IsTrue(ClanRoles.CanDemote("leader", "co_leader"));
            Assert.IsFalse(ClanRoles.CanDemote("co_leader", "co_leader"));
            Assert.IsTrue(ClanRoles.CanDisband("leader"));
            Assert.IsFalse(ClanRoles.CanDisband("co_leader"));
            Assert.IsTrue(ClanRoles.CanManageWars("co_leader"));
            Assert.IsFalse(ClanRoles.CanManageWars("officer"));
        }

        [Test]
        public void MapEventClockUsesServerTimeNotLocalTime()
        {
            var e = new MapEventDto { announceAt = 1000, startAt = 2000, endsAt = 5000, phase = "scheduled" };
            // Two clients with wildly different local clocks agree because both sync to the server's "now".
            var a = new MapEventClock(); a.Sync(1500, 10);
            var b = new MapEventClock(); b.Sync(1500, 900000);
            Assert.AreEqual("announced", a.PhaseAt(e, 10));
            Assert.AreEqual("announced", b.PhaseAt(e, 900000));
            Assert.AreEqual(500, a.RemainingMs(e, 10));
            Assert.AreEqual(500, b.RemainingMs(e, 900000));
            Assert.AreEqual("active", a.PhaseAt(e, 10 + 600));
            Assert.AreEqual(2900, a.RemainingMs(e, 10 + 600));
            Assert.AreEqual("expired", a.PhaseAt(e, 10 + 4000));
        }

        [Test]
        public void MapEventCompletedStaysCompletedRegardlessOfClock()
        {
            var e = new MapEventDto { announceAt = 0, startAt = 1, endsAt = 10, phase = "completed" };
            var c = new MapEventClock(); c.Sync(5, 0);
            Assert.AreEqual("completed", c.PhaseAt(e, 0));
        }

        [Test]
        public void CountdownFormatsAndClampsAtZero()
        {
            Assert.AreEqual("1:05", CommunityText.Countdown(65000));
            Assert.AreEqual("0:00", CommunityText.Countdown(-5));
        }

        [Test]
        public void OfflineCommunityServiceNeverPretendsToSucceed()
        {
            var svc = new OfflineCommunityService();
            Assert.IsFalse(svc.IsAvailable);
            ApiResult<WarOverviewDto> r = null;
            svc.LoadWarOverview(x => r = x);
            Assert.IsFalse(r.Ok);
            Assert.AreEqual("offline", r.Code);
            ApiResult<OkDto> report = null;
            svc.ReportPlayer("1234567890", "aimbot", "reason text", null, null, x => report = x);
            Assert.IsFalse(report.Ok);
        }

        [Test]
        public void FriendlyErrorsFallBackToServerMessage()
        {
            Assert.AreEqual("That clan name is already taken.", CommunityText.Friendly("name_taken", "x"));
            Assert.AreEqual("server said this", CommunityText.Friendly("something_new", "server said this"));
        }

        [Test]
        public void GameServerUrlsOverClearTextHttpAreLimitedToPrivateNetworks()
        {
            foreach (var ok in new[] { "https://game.example.com", "http://127.0.0.1:8787", "http://localhost:8787", "http://192.168.1.20:8787", "http://10.0.0.5", "http://172.16.4.1:80", "http://my-pc.local:8787" })
                Assert.IsTrue(AccountService.IsValidServerUrl(ok, out var p), ok + " " + p);
            foreach (var bad in new[] { "http://game.example.com", "http://8.8.8.8:8787", "http://172.32.0.1", "http://192.169.1.1", "ftp://x.example.com", "game.example.com", "", "  " })
                Assert.IsFalse(AccountService.IsValidServerUrl(bad, out _), bad);
        }

        [Test]
        public void SupabaseConfigExplainsExactlyWhatIsWrong()
        {
            string Jwt(string role) => "e30." + System.Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes("{\"role\":\"" + role + "\"}")).TrimEnd('=').Replace('+', '-').Replace('/', '_') + ".c2ln";
            Assert.IsFalse(new SupabaseConfig().IsConfigured);
            StringAssert.Contains("missing or empty", new SupabaseConfig().Problem);
            // the shipped example must never count as configured
            var example = SupabaseConfig.FromJson("{\"url\":\"https://YOUR-PROJECT-REF.supabase.co\",\"anonKey\":\"YOUR-PUBLIC-ANON-KEY (never the service_role key)\"}");
            Assert.IsFalse(example.IsConfigured);
            StringAssert.Contains("placeholder", example.Problem);
            StringAssert.Contains("https://", new SupabaseConfig { Url = "http://abc.supabase.co", AnonKey = Jwt("anon") }.Problem);
            StringAssert.Contains("anonKey", new SupabaseConfig { Url = "https://abc.supabase.co", AnonKey = "" }.Problem);
            var privileged = new SupabaseConfig { Url = "https://abc.supabase.co", AnonKey = Jwt("service_role") };
            Assert.IsFalse(privileged.IsConfigured);
            StringAssert.Contains("SERVICE-ROLE", privileged.Problem);
            Assert.IsFalse(new SupabaseConfig { Url = "https://abc.supabase.co", AnonKey = "sb_secret_abcdefghijklmnopqrstuvwxyz" }.IsConfigured);
            Assert.IsTrue(new SupabaseConfig { Url = "https://abc.supabase.co", AnonKey = Jwt("anon") + "xxxxxxxxxxxxxxxxxxxxxxxx" }.IsConfigured);
            Assert.IsTrue(new SupabaseConfig { Url = "https://abc.supabase.co", AnonKey = "sb_publishable_abcdefghijklmnopqrstuvwxyz" }.IsConfigured);
        }
    }
}
