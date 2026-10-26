using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using NUnit.Framework;
using Noxy.Core;

namespace Noxy.IntegrationTests
{
    /// <summary>
    /// Real C# client code (RemoteBattleRoyaleService, DTO parsing, BattleRoyaleSession, JumpModel, the client's own island)
    /// against the real Node match authority over real HTTP, in real time with shortened phases. The two "players" are automated
    /// test accounts in one process: this proves client/server compatibility of the rules, not play on real devices.
    /// </summary>
    [TestFixture]
    public class BattleRoyaleIntegrationTests
    {
        private const string ServerKey = "br-it-server-key";
        private Process node;
        private string url, dbFile;
        private double poiX, poiZ;
        private IslandLayout island;

        private static int FreePort() { var l = new TcpListener(IPAddress.Loopback, 0); l.Start(); int p = ((IPEndPoint)l.LocalEndpoint).Port; l.Stop(); return p; }
        private static string Root() => Path.GetFullPath(Path.Combine(TestContext.CurrentContext.TestDirectory, "..", "..", "..", "..", ".."));

        [OneTimeSetUp]
        public void Start()
        {
            string root = Root(), serverDir = Path.Combine(root, "server");
            string mapFile = Path.Combine(serverDir, "data", "maps", "bermuda.json");
            if (!File.Exists(Path.Combine(serverDir, "src", "index.js")) || !File.Exists(mapFile)) Assert.Ignore("server or map data missing");

            island = IslandGenerator.Generate();
            var clock = island.FindPoi("clock");
            poiX = clock.X; poiZ = clock.Z;
            string route = "{\"ax\":" + F(poiX - 200) + ",\"az\":" + F(poiZ) + ",\"bx\":" + F(poiX + 200) + ",\"bz\":" + F(poiZ) + "}";
            string brConfig = "{\"modes\":{\"battle_royale\":{\"minPlayers\":2,\"maxPlayers\":2,\"fillTimeoutMs\":1000,\"maxWaitMs\":60000,\"belowMin\":\"cancel\",\"requireServer\":true}}," +
                              "\"cabinSeconds\":2,\"quietSeconds\":1,\"flightSeconds\":14,\"planeAltitude\":170,\"fixedRoute\":" + route + "}";

            int port = FreePort();
            dbFile = Path.Combine(Path.GetTempPath(), "noxy-br-" + Guid.NewGuid().ToString("N") + ".sqlite");
            var psi = new ProcessStartInfo("node", "--no-warnings src/index.js") { WorkingDirectory = serverDir, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
            psi.Environment["PORT"] = port.ToString();
            psi.Environment["DB_PATH"] = dbFile;
            psi.Environment["NOXY_MATCH_SERVER_KEY"] = ServerKey;
            psi.Environment["NOXY_TEST_AUTH_LIMIT"] = "100000";
            psi.Environment["NOXY_BR_CONFIG"] = brConfig;
            node = Process.Start(psi);
            url = "http://127.0.0.1:" + port;
            var transport = new HttpClientTransport();
            var until = DateTime.UtcNow.AddSeconds(15);
            while (DateTime.UtcNow < until)
            {
                int status = 0; transport.Send("GET", url + "/api/health", null, null, r => status = r.Status);
                if (status == 200) return;
                Thread.Sleep(200);
            }
            Assert.Fail("backend did not start");
        }

        [OneTimeTearDown]
        public void Stop()
        {
            try { if (node != null && !node.HasExited) node.Kill(true); } catch (Exception) { }
            foreach (var ext in new[] { "", "-wal", "-shm" }) try { File.Delete(dbFile + ext); } catch (Exception) { }
        }

        private static string F(double v) => v.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture);

        private static T Wait<T>(Action<Action<ApiResult<T>>> call) where T : class
        {
            ApiResult<T> r = null; call(x => r = x); return r.Value ?? Activator.CreateInstance<T>();
        }
        private static ApiResult<T> Call<T>(Action<Action<ApiResult<T>>> call) where T : class { ApiResult<T> r = null; call(x => r = x); return r; }

        private static long NowMs() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        [Test, Timeout(180000)]
        public void TwoClientsFlyAFullDropAgainstTheRealMatchAuthority()
        {
            // a verified game server must exist before anything can be queued
            var transport = new HttpClientTransport();
            var key = new Dictionary<string, string> { ["x-noxy-server-key"] = ServerKey };
            HttpResponse reg = null;
            transport.SendWithHeaders("POST", url + "/api/internal/servers/register", "{\"serverId\":\"br-it-1\",\"name\":\"IT\",\"region\":\"eu\",\"mode\":\"battle_royale\",\"host\":\"gs.example.test\",\"port\":7777,\"capacity\":50}", null, key, r => reg = r);
            Assert.AreEqual(201, reg.Status, reg.Body);

            var a = new TestClient(url); a.Register("br_it_a" + Guid.NewGuid().ToString("N").Substring(0, 6));
            var b = new TestClient(url); b.Register("br_it_b" + Guid.NewGuid().ToString("N").Substring(0, 6));
            var sa = new RemoteBattleRoyaleService(a.Api) { SignedIn = true };
            var sb = new RemoteBattleRoyaleService(b.Api) { SignedIn = true };

            // 1. no verified server yet: honest refusal
            var refused = Call<BrStateDto>(d => sa.Queue("battle_royale", d));
            Assert.IsFalse(refused.Ok); Assert.AreEqual("no_server_available", refused.Code);
            string token = MiniJson.Str(MiniJson.AsObject(Parse(reg.Body)), "instanceToken");
            transport.SendWithHeaders("POST", url + "/api/internal/servers/heartbeat", "{\"serverId\":\"br-it-1\",\"instanceToken\":\"" + token + "\",\"players\":0}", null, key, _ => { });

            // 2. queue: first player waits, second fills the roster and the server starts the cabin phase
            var cfg = Wait<BrConfigDto>(d => sa.LoadConfig(d));
            Assert.AreEqual(2, cfg.cabinSeconds); Assert.AreEqual(14, cfg.flightSeconds); Assert.AreEqual(170f, cfg.planeAltitude);
            var q1 = Call<BrStateDto>(d => sa.Queue("battle_royale", d));
            Assert.IsTrue(q1.Ok, q1.Message); Assert.AreEqual("waiting", q1.Value.status);
            var q2 = Call<BrStateDto>(d => sb.Queue("battle_royale", d));
            Assert.IsTrue(q2.Ok, q2.Message); Assert.AreEqual("cabin", q2.Value.status);

            var sessA = new BattleRoyaleSession(); var sessB = new BattleRoyaleSession();
            var stA = Wait<BrStateDto>(d => sa.GetState(sessA.LastEventSeq, d)); sessA.Apply(stA, NowMs());
            var stB = Wait<BrStateDto>(d => sb.GetState(sessB.LastEventSeq, d)); sessB.Apply(stB, NowMs());
            Assert.AreEqual(stA.matchId, stB.matchId);
            Assert.AreEqual(2, stA.players.Count);
            Assert.AreEqual(stA.times.cabinEndsAt, stB.times.cabinEndsAt, "one schedule for both clients");
            Assert.AreEqual(2000, stA.times.cabinEndsAt - stA.times.cabinStartsAt);
            Assert.AreEqual(BrPhase.Cabin, sessA.Phase);
            long cd = sessA.CountdownMs(NowMs());
            Assert.That(cd, Is.InRange(1L, 2000L), "countdown comes from the server clock");
            Assert.AreEqual(stA.players[0].slot + stA.players[1].slot, 1); Assert.AreEqual("cabin", stA.players[0].space);

            // 3. nothing can happen before the window: no jump in the cabin or quiet period, no combat
            var early = Call<BrMoveResultDto>(d => sa.Jump(d));
            Assert.IsFalse(early.Ok); Assert.AreEqual("jump_not_open", early.Code);
            var fireEarly = Call<BrFireResultDto>(d => sa.Fire(1, 0, 0, d));
            Assert.AreEqual("combat_disabled", fireEarly.Code);

            // 4. wait (real time) until the server opens the jump window and the plane is over the Clock Tower
            BrStateDto cur = null; var sw = Stopwatch.StartNew(); bool sawQuiet = false;
            while (sw.Elapsed.TotalSeconds < 30)
            {
                cur = Wait<BrStateDto>(d => sa.GetState(sessA.LastEventSeq, d)); sessA.Apply(cur, NowMs());
                if (sessA.Phase == BrPhase.Flight && !sessA.JumpButtonVisible(NowMs())) sawQuiet = true;
                if (sessA.JumpButtonVisible(NowMs()) && Math.Abs(cur.plane.x - poiX) < 25f) break;
                Thread.Sleep(100);
            }
            Assert.IsTrue(sessA.JumpButtonVisible(NowMs()), "the jump window opened");
            Assert.IsTrue(sawQuiet || cfg.quietSeconds == 0, "there was a quiet period before the button");
            Assert.AreEqual(BrPhase.Flight, sessA.Phase);

            // 5. A jumps. B is not affected and keeps its own timing
            var jump = Call<BrMoveResultDto>(d => sa.Jump(d));
            Assert.IsTrue(jump.Ok, jump.Message); Assert.AreEqual("freefall", jump.Value.state);
            var bView = Wait<BrStateDto>(d => sb.GetState(0, d));
            Assert.AreEqual("aircraft", bView.you.state, "one player's jump does not force another");
            Assert.AreEqual("freefall", bView.players.Find(p => p.publicId == stA.you.publicId).state);

            // 6. fly the descent with the client's own model; the server validates every step
            var air = new AirState { X = jump.Value.x, Y = jump.Value.y, Z = jump.Value.z };
            var last = Stopwatch.StartNew(); string state = "freefall"; float t = 0f; int moves = 0;
            while (state != "landed" && t < 45f)
            {
                Thread.Sleep(120);
                float dt = (float)last.Elapsed.TotalSeconds; last.Restart(); t += dt;
                air.Chute = state == "parachute";
                // steer gently toward the Clock Tower centre
                float sx = Clamp((float)poiX - air.X, 60f), sz = Clamp((float)poiZ - air.Z, 60f);
                air = JumpModel.Step(air, dt, sx, sz, cfg, island.HeightAt, out bool landedLocal);
                var mv = Call<BrMoveResultDto>(d => sa.Move(air.X, air.Y, air.Z, 0f, state == "freefall" ? "freefall" : "glide", d));
                moves++;
                Assert.IsTrue(mv.Ok, "server rejected a normal descent step (" + mv.Code + "): " + mv.Message + " at t=" + t + " state=" + state + " y=" + air.Y);
                state = mv.Value.state;
                air.X = mv.Value.x; air.Y = mv.Value.y; air.Z = mv.Value.z;     // the server's position is the truth
                if (landedLocal && state != "landed") { air.Y = island.HeightAt(air.X, air.Z) - 0.2f; }   // keep pressing down; the server decides
            }
            Assert.AreEqual("landed", state, "landed within " + t + " s");
            var fin = Wait<BrStateDto>(d => sa.GetState(0, d));
            Assert.AreEqual("landed", fin.you.state);
            Assert.AreEqual(100, fin.you.health, "a normal parachute landing costs no health");
            Assert.Less(Math.Abs(fin.you.y - island.HeightAt(fin.you.x, fin.you.z)), 3f, "the server and the client agree on where the ground is");
            TestContext.Progress.WriteLine("full drop: " + moves + " validated moves in " + t.ToString("0.0") + " s, landed at (" + (int)fin.you.x + "," + (int)fin.you.z + ")");

            // 7. the server deadline ejected B; combat is now enabled for everyone by the server
            Assert.AreEqual("combat", fin.status);
            var bAfter = Wait<BrStateDto>(d => sb.GetState(0, d));
            Assert.IsTrue(bAfter.you.forced, "B left at the server-enforced deadline"); Assert.AreEqual("combat", bAfter.status);
            var fire = Call<BrFireResultDto>(d => sa.Fire(0f, 1f, 0.001f, d));
            Assert.IsTrue(fire.Ok, fire.Code); Assert.IsFalse(fire.Value.DidHit);
        }

        private static float Clamp(float v, float m) => v < -m ? -m : v > m ? m : v;
        private static object Parse(string json) { Assert.IsTrue(MiniJson.TryParse(json, out var o)); return o; }
    }
}
