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
    }
}
