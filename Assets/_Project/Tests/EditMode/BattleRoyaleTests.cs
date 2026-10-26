using System;
using System.Collections.Generic;
using NUnit.Framework;
using Noxy.Core;
using UnityEngine;

namespace Noxy.Tests
{
    public class BattleRoyaleTests
    {
        // ------------------------------------------------------------ session / server clock
        private static BrStateDto State(string status, long serverNow, long cabinStart = 0, string you = "aircraft")
        {
            var s = new BrStateDto { status = status, serverNow = serverNow };
            s.you.state = you;
            s.times = new BrTimesDto { cabinStartsAt = cabinStart, cabinEndsAt = cabinStart + 15000, flightStartsAt = cabinStart + 15000, jumpOpensAt = cabinStart + 20000, flightEndsAt = cabinStart + 75000, combatStartsAt = cabinStart + 75000 };
            s.waiting = new BrWaitingDto { players = 3, minPlayers = 10, maxPlayers = 20, fillDeadlineAt = serverNow + 30000 };
            return s;
        }

        [Test]
        public void CountdownsUseTheServerClockSoEveryDeviceAgrees()
        {
            var a = new BattleRoyaleSession(); var b = new BattleRoyaleSession();
            // same server instant, wildly different device clocks
            a.Apply(State("cabin", 1_000_000, cabinStart: 995_000), localNowMs: 5);
            b.Apply(State("cabin", 1_000_000, cabinStart: 995_000), localNowMs: 900_000_000);
            Assert.AreEqual(10_000, a.CountdownMs(5));
            Assert.AreEqual(10_000, b.CountdownMs(900_000_000));
            Assert.AreEqual("Match starts in 10", a.CountdownLabel(5));
            Assert.AreEqual(a.CountdownMs(5 + 4000), b.CountdownMs(900_000_000 + 4000));
            Assert.AreEqual(0, a.CountdownMs(5 + 60_000), "never negative");
        }

        [Test]
        public void JumpButtonAppearsOnlyAfterTheServersQuietPeriodAndOnlyWhileAboard()
        {
            var s = new BattleRoyaleSession();
            long cabinStart = 100_000;
            s.Apply(State("cabin", cabinStart + 5000, cabinStart), 0);
            Assert.IsFalse(s.JumpButtonVisible(0), "cabin phase");
            s.Apply(State("flight", cabinStart + 16000, cabinStart), 0);
            Assert.IsFalse(s.JumpButtonVisible(0), "5 s quiet period after take-off");
            StringAssert.StartsWith("Jump opens in", s.CountdownLabel(0));
            s.Apply(State("flight", cabinStart + 20000, cabinStart), 0);
            Assert.IsTrue(s.JumpButtonVisible(0));
            StringAssert.StartsWith("Jump!", s.CountdownLabel(0));
            s.Apply(State("flight", cabinStart + 25000, cabinStart, you: "freefall"), 0);
            Assert.IsFalse(s.JumpButtonVisible(0), "already out");
            s.Apply(State("combat", cabinStart + 80000, cabinStart, you: "landed"), 0);
            Assert.IsTrue(s.CombatEnabled); Assert.IsFalse(s.JumpButtonVisible(0));
        }

        [Test]
        public void EventsAreDeliveredOnceAndPollingSlowsWhenNothingIsHappening()
        {
            var s = new BattleRoyaleSession();
            var st = State("combat", 1, 0);
            st.events.Add(new BrEventDto { seq = 5, type = "elimination" });
            st.events.Add(new BrEventDto { seq = 6, type = "jump" });
            s.Apply(st, 0);
            Assert.AreEqual(2, s.TakeEvents().Count);
            s.Apply(st, 0);                       // same snapshot again (a duplicate poll)
            Assert.AreEqual(0, s.TakeEvents().Count);
            Assert.AreEqual(6, s.LastEventSeq);
            Assert.Less(s.PollIntervalSeconds, 0.5f);
            s.Apply(State("waiting", 1), 0);
            Assert.AreEqual(1f, s.PollIntervalSeconds);
        }

        [Test]
        public void ServerSnapshotsParseIncludingMissingFields()
        {
            const string json = "{\"matchId\":\"br-1\",\"mode\":\"battle_royale\",\"status\":\"combat\",\"serverNow\":1700000000123,\"eventSeq\":9," +
                "\"server\":{\"id\":\"eu-1\",\"name\":\"EU\",\"region\":\"eu\",\"host\":\"gs.example.test\",\"port\":7777}," +
                "\"players\":[{\"publicId\":\"1234567890\",\"username\":\"A\",\"slot\":0,\"state\":\"landed\",\"alive\":true,\"visible\":true,\"x\":1.5,\"y\":2.5,\"z\":3.5}," +
                "{\"publicId\":\"2234567890\",\"username\":\"B\",\"slot\":1,\"state\":\"landed\",\"alive\":true,\"visible\":false}]," +
                "\"you\":{\"publicId\":\"1234567890\",\"state\":\"landed\",\"health\":87,\"selected\":\"primary\",\"weapons\":{\"primary\":{\"id\":\"rifle_ar\",\"mag\":30},\"secondary\":null,\"melee\":{\"id\":\"melee_knife\",\"mag\":0}},\"ammo\":{\"pistol\":0,\"rifle\":60}}," +
                "\"times\":{\"combatStartsAt\":1700000000000},\"combat\":{\"enabled\":true,\"startsAt\":1700000000000},\"events\":[{\"seq\":9,\"type\":\"elimination\",\"victim\":\"2234567890\",\"killer\":\"1234567890\",\"placement\":2}]}";
            var s = JsonUtility.FromJson<BrStateDto>(json);
            Assert.AreEqual("combat", s.status);
            Assert.AreEqual(2, s.players.Count);
            Assert.IsTrue(s.players[0].visible); Assert.IsFalse(s.players[1].visible, "hidden players carry no usable position");
            Assert.AreEqual(87, s.you.health); Assert.AreEqual("rifle_ar", s.you.weapons.primary.id);
            Assert.IsTrue(BrWeaponDto.IsNullOrEmpty(s.you.weapons.secondary), "a null weapon slot is just empty");
            Assert.IsFalse(BrWeaponDto.IsNullOrEmpty(s.you.weapons.melee));
            Assert.AreEqual(60, s.you.ammo.rifle);
            Assert.AreEqual("elimination", s.events[0].type);
            Assert.AreEqual(BrPhase.Combat, BattleRoyaleSession.ParsePhase(s.status));
        }

        // ------------------------------------------------------------ honest loading progress
        [Test]
        public void LoadingCompletesOnlyWhenEveryStageActuallyFinished()
        {
            Assert.AreEqual(1f, LoadingProgress.Weights[0] + LoadingProgress.Weights[1] + LoadingProgress.Weights[2] + LoadingProgress.Weights[3] + LoadingProgress.Weights[4], 1e-5f);
            var p = new LoadingProgress(); p.Begin(0f);
            Assert.AreEqual(0, p.Percent); Assert.IsFalse(p.IsComplete);
            p.Complete(LoadStage.ConnectingServer, 1f); p.Complete(LoadStage.FindingPlayers, 2f);
            p.Report(LoadStage.LoadingMap, 0.5f, 3f);
            Assert.AreEqual(55, p.Percent, "10 + 25 + half of 40");
            Assert.AreEqual(LoadStage.LoadingMap, p.Current);
            StringAssert.Contains("Bermuda", p.Message);
            Assert.IsFalse(p.IsComplete, "the map is half loaded: never report ready");
            p.Report(LoadStage.LoadingMap, 0.3f, 4f);
            Assert.AreEqual(0.5f, p.StageProgress(LoadStage.LoadingMap), "progress never moves backwards");
            p.Complete(LoadStage.LoadingMap, 5f); p.Complete(LoadStage.PreparingCharacters, 6f);
            Assert.IsFalse(p.IsComplete, "match start has not been confirmed yet");
            Assert.AreEqual(95, p.Percent);
            p.Complete(LoadStage.StartingMatch, 7f);
            Assert.IsTrue(p.IsComplete); Assert.AreEqual(100, p.Percent); Assert.AreEqual("Ready", p.Message);
        }

        [Test]
        public void LoadingFlagsSlowNetworksTimesOutAndSupportsCancel()
        {
            var p = new LoadingProgress(); p.Begin(0f);
            p.Report(LoadStage.ConnectingServer, 0.2f, 1f);
            p.Tick(5f); Assert.IsFalse(p.Slow);
            p.Tick(12f); Assert.IsTrue(p.Slow); StringAssert.Contains("slow connection", p.Message);
            p.Report(LoadStage.ConnectingServer, 0.6f, 13f); p.Tick(13.5f);
            Assert.IsFalse(p.Slow, "progress resumed");
            p.Tick(1f + LoadingProgress.StageTimeoutSeconds[0] + 1f);
            Assert.IsTrue(p.Failed); Assert.AreEqual(LoadStage.ConnectingServer, p.FailedStage);
            StringAssert.Contains("Timed out", p.FailureMessage);
            p.Complete(LoadStage.ConnectingServer, 100f);
            Assert.IsFalse(p.IsComplete, "a failed load can never become 'complete'");

            var c = new LoadingProgress(); c.Begin(0f); c.Report(LoadStage.ConnectingServer, 1f, 1f); c.Cancel();
            Assert.IsTrue(c.Cancelled); Assert.IsFalse(c.IsComplete);
            c.Report(LoadStage.FindingPlayers, 1f, 2f);
            Assert.AreEqual(0f, c.StageProgress(LoadStage.FindingPlayers), "nothing is recorded after cancelling");

            var f = new LoadingProgress(); f.Begin(0f); f.Fail("Server went away.");
            Assert.AreEqual("Server went away.", f.Message);
        }

        // ------------------------------------------------------------ jump kinematics versus the server's validation
        private static bool Respects(BrConfigDto c, AirState before, AirState after, float dt, bool chute, float sinceDeploy, out string why)
        {
            // the same arithmetic as validateMove() in server/src/br.js
            const float tol = 1.35f;
            dt = Mathf.Clamp(dt, 0.02f, 2f);
            float f = chute ? c.parachute.maxHorizontal : c.freefall.maxHorizontal;
            float maxH = f * dt * tol + 0.5f;
            float dropLimit = c.freefall.terminalSpeed;
            if (chute) dropLimit = c.parachute.maxDescent + (c.freefall.terminalSpeed - c.parachute.maxDescent) * Math.Max(0f, 1f - sinceDeploy / c.parachute.transitionSeconds);
            float maxDrop = dropLimit * dt * tol + 0.5f;
            float h = (float)Math.Sqrt((after.X - before.X) * (after.X - before.X) + (after.Z - before.Z) * (after.Z - before.Z));
            why = "";
            if (h > maxH) { why = "horizontal " + h + " > " + maxH; return false; }
            if (after.Y - before.Y > 0.5f) { why = "climbed"; return false; }
            if (before.Y - after.Y > maxDrop) { why = "dropped " + (before.Y - after.Y) + " > " + maxDrop; return false; }
            return true;
        }

        [TestCase(0.05f)]
        [TestCase(0.1f)]
        [TestCase(0.25f)]
        [TestCase(0.5f)]
        public void LocalFreefallAndParachuteAlwaysStayInsideTheServersLimits(float dt)
        {
            var c = new BrConfigDto();
            Func<float, float, float> ground = (x, z) => 12f;
            var s = new AirState { X = 0f, Y = c.planeAltitude - 2f, Z = 0f };
            float t = 0f, impact = 0f, deployedAt = -1f; bool landed = false; int steps = 0;
            while (!landed && steps++ < 20000)
            {
                float alt = s.Y - ground(s.X, s.Z);
                if (!s.Chute && alt <= c.freefall.autoDeployAltitude) { s.Chute = true; deployedAt = t; }   // the server auto-deploys here
                var before = s;
                float sx = (float)Math.Sin(t * 0.7f), sz = (float)Math.Cos(t * 0.5f);                     // keeps steering the whole way down
                s = JumpModel.Step(s, dt, sx, sz, c, ground, out landed);
                Assert.IsTrue(Respects(c, before, s, dt, before.Chute, deployedAt < 0f ? 0f : t - deployedAt, out var why), why + " at t=" + t);
                impact = -s.VelY; t += dt;
            }
            Assert.IsTrue(landed, "reaches the ground");
            Assert.Less(t, 60f, "a full drop takes under a minute");
            Assert.Less(impact, c.landing.safeSpeed, "the chute brings the landing speed under the safe limit");
        }

        [Test]
        public void ParachuteRulesFollowTheServerConfig()
        {
            var c = new BrConfigDto();
            Assert.IsFalse(JumpModel.CanDeploy(5f, c)); Assert.IsTrue(JumpModel.CanDeploy(c.parachute.minDeployAltitude, c));
            Assert.Less(JumpModel.FreefallTarget(c), c.freefall.terminalSpeed);
            Assert.Less(JumpModel.ChuteTarget(c), c.parachute.maxDescent);
            // steering input beyond 1 is clamped, so a keyboard diagonal is not faster than the stick
            var a = JumpModel.Step(new AirState { Y = 400f }, 1f, 1f, 1f, c, (x, z) => 0f, out _);
            var b = JumpModel.Step(new AirState { Y = 400f }, 1f, 1f, 0f, c, (x, z) => 0f, out _);
            Assert.AreEqual((float)Math.Sqrt(a.X * a.X + a.Z * a.Z), (float)Math.Sqrt(b.X * b.X + b.Z * b.Z), 1e-3f);
        }

        // ------------------------------------------------------------ matchmaking
        private sealed class FakeBr : IBattleRoyaleService
        {
            public bool Available = true;
            public ApiResult<BrStateDto> QueueResult, StateResult;
            public int Leaves, Polls;
            public bool IsAvailable => Available;
            public string UnavailableReason => "Sign in required.";
            public void Queue(string m, Action<ApiResult<BrStateDto>> d) => d(QueueResult);
            public void GetState(int s, Action<ApiResult<BrStateDto>> d) { Polls++; d(StateResult); }
            public void LeaveQueue(Action<ApiResult<OkDto>> d) { Leaves++; d(new ApiResult<OkDto> { Ok = true, Value = new OkDto() }); }
            public void LoadConfig(Action<ApiResult<BrConfigDto>> d) { }
            public void LoadMapInfo(Action<ApiResult<BrMapInfoDto>> d) { }
            public void Move(float x, float y, float z, float yaw, string a, Action<ApiResult<BrMoveResultDto>> d) { }
            public void Jump(Action<ApiResult<BrMoveResultDto>> d) { }
            public void DeployParachute(Action<ApiResult<OkDto>> d) { }
            public void Fire(float dx, float dy, float dz, Action<ApiResult<BrFireResultDto>> d) { }
            public void Reload(Action<ApiResult<OkDto>> d) { }
            public void SelectSlot(string s, Action<ApiResult<OkDto>> d) { }
            public void Heal(Action<ApiResult<OkDto>> d) { }
            public void PickupLoot(string id, Action<ApiResult<BrPickupDto>> d) { }
        }
        private static ApiResult<BrStateDto> Ok(string status, int players = 3)
        {
            var s = new BrStateDto { status = status };
            s.waiting = new BrWaitingDto { players = players, minPlayers = 10, maxPlayers = 20 };
            s.server = new BrServerDto { host = "gs.example.test", port = 7777 };
            return new ApiResult<BrStateDto> { Ok = true, Value = s };
        }
        private static GameModeDefinition BrMode() => DefaultGameModes.Create().Find(m => m.Id == GameModeIds.BattleRoyale);

        [Test]
        public void MatchmakingFailsHonestlyWithoutAnAccountOrServer()
        {
            var fake = new FakeBr { Available = false };
            var mm = new BattleRoyaleMatchmakingService(fake); MatchmakingStatus last = null; mm.StatusChanged += s => last = s;
            mm.Begin(BrMode(), new MatchmakingRequest());
            Assert.AreEqual(MatchmakingState.Failed, last.State); Assert.AreEqual(MatchmakingError.ServerUnavailable, last.Error);

            fake.Available = true;
            fake.QueueResult = new ApiResult<BrStateDto> { Ok = false, Code = "no_server_available", Message = "No game server is online for this mode right now. Nothing was queued." };
            mm.Begin(BrMode(), new MatchmakingRequest());
            Assert.AreEqual(MatchmakingState.Failed, last.State); StringAssert.Contains("No game server", last.Message);
        }

        [Test]
        public void MatchmakingFollowsTheServerRosterUntilTheServerStartsTheMatch()
        {
            var fake = new FakeBr { QueueResult = Ok("waiting", 3) };
            var mm = new BattleRoyaleMatchmakingService(fake); var seen = new List<MatchmakingStatus>(); mm.StatusChanged += seen.Add;
            mm.Begin(BrMode(), new MatchmakingRequest());
            Assert.AreEqual(MatchmakingState.Searching, seen[seen.Count - 1].State);
            StringAssert.Contains("3/10", seen[seen.Count - 1].Message);
            fake.StateResult = Ok("waiting", 7); mm.Tick(1.1f);
            StringAssert.Contains("7/10", seen[seen.Count - 1].Message);
            Assert.Greater(seen[seen.Count - 1].Progress, seen[0].Progress);
            mm.Tick(0.2f); Assert.AreEqual(1, fake.Polls, "polls at most once per second");
            fake.StateResult = Ok("cabin"); mm.Tick(1.1f);
            var done = seen[seen.Count - 1];
            Assert.AreEqual(MatchmakingState.Succeeded, done.State);
            Assert.AreEqual(BattleRoyaleMatchmakingService.SceneName, done.Assignment.SceneName);
            Assert.AreEqual("gs.example.test", done.Assignment.ServerAddress);
            Assert.AreEqual(7777, done.Assignment.ServerPort);
        }

        [Test]
        public void CancelledMatchesAndPlayerCancellationAreReported()
        {
            var fake = new FakeBr { QueueResult = Ok("waiting") };
            var mm = new BattleRoyaleMatchmakingService(fake); MatchmakingStatus last = null; mm.StatusChanged += s => last = s;
            mm.Begin(BrMode(), new MatchmakingRequest());
            fake.StateResult = Ok("cancelled"); mm.Tick(1.1f);
            Assert.AreEqual(MatchmakingState.Failed, last.State); Assert.AreEqual(MatchmakingError.NotEnoughPlayers, last.Error);

            mm.Begin(BrMode(), new MatchmakingRequest());
            mm.Cancel();
            Assert.AreEqual(MatchmakingState.Cancelled, last.State); Assert.AreEqual(1, fake.Leaves);
        }

        [Test]
        public void SlowNetworkIsRetriedAFewTimesThenReportedNotFaked()
        {
            var fake = new FakeBr { QueueResult = Ok("waiting") };
            var mm = new BattleRoyaleMatchmakingService(fake); MatchmakingStatus last = null; mm.StatusChanged += s => last = s;
            mm.Begin(BrMode(), new MatchmakingRequest());
            fake.StateResult = new ApiResult<BrStateDto> { Ok = false, NetworkError = true, Code = "network", Message = "Cannot reach the server." };
            for (int i = 0; i < 3; i++) mm.Tick(1.1f);
            Assert.AreEqual(MatchmakingState.Searching, last.State); StringAssert.Contains("slow", last.Message);
            for (int i = 0; i < 10; i++) mm.Tick(1.1f);
            Assert.AreEqual(MatchmakingState.Failed, last.State); Assert.AreEqual(MatchmakingError.ServerUnavailable, last.Error);
        }

        // ------------------------------------------------------------ mode availability
        private sealed class FakeCommunity : ICommunityService
        {
            public bool Available = true; public ApiResult<ServerListDto> Servers;
            public bool IsAvailable => Available; public string UnavailableReason => "x";
            public void LoadServers(string m, Action<ApiResult<ServerListDto>> d) => d(Servers);
            public void LoadMyClan(Action<ApiResult<MyClanDto>> d) { } public void SearchClans(string t, Action<ApiResult<ClanListDto>> d) { }
            public void LoadClanLeaderboard(string b, Action<ApiResult<ClanListDto>> d) { } public void CreateClan(string n, string t, string de, string j, Action<ApiResult<ClanDto>> d) { }
            public void UpdateClan(int c, string de, string j, Action<ApiResult<ClanDto>> d) { } public void DisbandClan(int c, Action<ApiResult<OkDto>> d) { }
            public void InvitePlayer(int c, string p, Action<ApiResult<OkDto>> d) { } public void RespondInvitation(int i, string a, Action<ApiResult<OkDto>> d) { }
            public void ApplyToClan(int c, string m, Action<ApiResult<ApplyResultDto>> d) { } public void RespondApplication(int i, string a, Action<ApiResult<OkDto>> d) { }
            public void MemberAction(int c, string p, string a, Action<ApiResult<OkDto>> d) { } public void TransferLeadership(int c, string p, Action<ApiResult<OkDto>> d) { }
            public void LeaveClan(int c, Action<ApiResult<OkDto>> d) { } public void LoadChat(int c, int a, Action<ApiResult<ChatDto>> d) { }
            public void SendChat(int c, string t, Action<ApiResult<OkDto>> d) { } public void ReportClan(int c, string ca, string r, Action<ApiResult<OkDto>> d) { }
            public void LoadWarOverview(Action<ApiResult<WarOverviewDto>> d) { } public void QueueForWar(string f, List<string> r, Action<ApiResult<QueueResultDto>> d) { }
            public void LeaveWarQueue(Action<ApiResult<OkDto>> d) { } public void RegisterForEvent(string e, List<string> r, Action<ApiResult<OkDto>> d) { }
            public void UnregisterFromEvent(string e, Action<ApiResult<OkDto>> d) { } public void LoadWarContributions(Action<ApiResult<ContributionsDto>> d) { }
            public void DisputeWar(string m, string r, Action<ApiResult<OkDto>> d) { } public void LoadCompanions(Action<ApiResult<CompanionsDto>> d) { }
            public void UnlockCompanion(string i, string v, string c, Action<ApiResult<CompanionsDto>> d) { } public void EquipCompanion(string i, string v, Action<ApiResult<CompanionsDto>> d) { }
            public void LoadMapEvents(string m, Action<ApiResult<MapEventsDto>> d) { } public void LoadFairPlay(Action<ApiResult<FairPlayDto>> d) { }
            public void LoadFairPlayRules(Action<ApiResult<FairRulesDto>> d) { } public void ReportPlayer(string p, string c, string r, string e, string m, Action<ApiResult<OkDto>> d) { }
            public void Appeal(int a, string m, Action<ApiResult<AppealResultDto>> d) { }
        }

        [Test]
        public void BattleRoyaleIsPlayableOnlyWhileAVerifiedServerIsOnlineAndTheSceneExists()
        {
            var mode = BrMode();
            Assert.IsFalse(mode.IsPlayable, "off by default");
            StringAssert.Contains("no game server is online", mode.StatusNote);
            var com = new FakeCommunity { Servers = new ApiResult<ServerListDto> { Ok = true, Value = new ServerListDto() } };
            bool scene = true;
            var av = new BattleRoyaleAvailability(com, mode, () => scene);
            av.Tick(BattleRoyaleAvailability.PollSeconds);
            Assert.IsFalse(mode.IsPlayable); StringAssert.Contains("No Battle Royale game server", mode.StatusNote);
            com.Servers.Value.servers.Add(new GameServerDto { id = "eu-1", status = "online" });
            av.Tick(BattleRoyaleAvailability.PollSeconds);
            Assert.IsTrue(mode.IsPlayable); StringAssert.Contains("1 server online", mode.StatusNote);
            scene = false; av.Tick(BattleRoyaleAvailability.PollSeconds);
            Assert.IsFalse(mode.IsPlayable); StringAssert.Contains("scene is missing", mode.StatusNote);
            scene = true; com.Available = false; av.Tick(BattleRoyaleAvailability.PollSeconds);
            Assert.IsFalse(mode.IsPlayable); StringAssert.Contains("Sign in", mode.StatusNote);
            com.Available = true; com.Servers = new ApiResult<ServerListDto> { Ok = false, Message = "Cannot reach the server." };
            av.Tick(BattleRoyaleAvailability.PollSeconds);
            Assert.IsFalse(mode.IsPlayable);
        }
    }
}
