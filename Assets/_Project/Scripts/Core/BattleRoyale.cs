using System;
using System.Collections.Generic;
using UnityEngine;

namespace Noxy.Core
{
    // Battle Royale client model. Everything here either mirrors what the match server says or computes harmless local
    // presentation. The server decides phases, jump eligibility, landing, damage, loot and results.

    // ---------------------------------------------------------------- DTOs (field names match the server JSON)
    [Serializable]
    public sealed class BrWeaponDto
    {
        public string id = ""; public int mag;
        /// <summary>True for an empty slot. The server sends null there, and parsers differ on whether that becomes null or a blank object.</summary>
        public static bool IsNullOrEmpty(BrWeaponDto w) => w == null || string.IsNullOrEmpty(w.id);
    }
    [Serializable] public sealed class BrWeaponsDto { public BrWeaponDto primary = new BrWeaponDto(), secondary = new BrWeaponDto(), melee = new BrWeaponDto(); }
    [Serializable] public sealed class BrAmmoDto { public int pistol, rifle; }
    [Serializable] public sealed class BrYouDto
    {
        public string publicId = "", state = "", space = "", selected = "";
        public float x, y, z;
        public int health, armor, medkits, kills, placement, matchCoins;
        public BrWeaponsDto weapons = new BrWeaponsDto();
        public BrAmmoDto ammo = new BrAmmoDto();
        public long reloadingUntil, healingUntil;
        public bool relocated, forced;
    }
    [Serializable] public sealed class BrShopDto { public string id = "", name = ""; public float x, y, z; }
    [Serializable] public sealed class BrCurrencyPickupDto { public string id = ""; public float x, y, z; public int amount; }
    [Serializable] public sealed class BrShopOfferDto
    {
        public string weaponId = "", name = "", category = "", slot = "";
        public int price, damage, range, magazine;
        public float fireRate;
        public bool owned;
    }
    [Serializable] public sealed class BrShopOpenDto { public bool ok; public BrShopDto shop = new BrShopDto(); public int balance; public List<BrShopOfferDto> offers = new List<BrShopOfferDto>(); }
    [Serializable] public sealed class BrShopBuyItemDto { public string weaponId = "", slot = ""; public int price; }
    [Serializable] public sealed class BrShopBuyDto
    {
        public bool ok, duplicate;
        public int balance;
        public BrShopBuyItemDto item = new BrShopBuyItemDto();
        public BrYouDto you = new BrYouDto();
    }
    [Serializable] public sealed class BrCurrencyPickupResultDto { public bool ok; public int amount, balance; }
    [Serializable] public sealed class BrPlayerDto
    {
        public string publicId = "", username = "", state = "", anim = "", space = "";
        public int slot, kills;
        public bool alive, connected, visible;
        public float x, y, z, yaw;
    }
    [Serializable] public sealed class BrTimesDto { public long cabinStartsAt, cabinEndsAt, flightStartsAt, jumpOpensAt, flightEndsAt, combatStartsAt; }
    [Serializable] public sealed class BrRouteDto { public float ax, az, bx, bz, altitude, headingDeg; }
    [Serializable] public sealed class BrPlaneDto { public float x, y, z; }
    [Serializable] public sealed class BrJumpDto { public bool open; public long opensAt, deadlineAt; }
    [Serializable] public sealed class BrCombatDto { public bool enabled; public long startsAt; }
    [Serializable] public sealed class BrWaitingDto { public int players, minPlayers, maxPlayers; public long fillDeadlineAt, cancelAt; }
    [Serializable] public sealed class BrServerDto { public string id = "", name = "", region = "", host = ""; public int port; }
    [Serializable] public sealed class BrEventDto
    {
        public int seq, placement, count;
        public long at;
        public string type = "", phase = "", reason = "", player = "", victim = "", victimName = "", killer = "", killerName = "";
        public bool forced, auto, relocated;
    }
    [Serializable] public sealed class BrResultDto { public string publicId = "", username = ""; public int placement, kills; }
    [Serializable] public sealed class BrRewardsDto { public bool processed; public string error = ""; }
    [Serializable] public sealed class BrStateDto
    {
        public string matchId = "", mode = "", status = "", endReason = "";
        public long serverNow;
        public int eventSeq;
        public BrServerDto server = new BrServerDto();
        public List<BrPlayerDto> players = new List<BrPlayerDto>();
        public BrYouDto you = new BrYouDto();
        public BrTimesDto times = new BrTimesDto();
        public BrWaitingDto waiting = new BrWaitingDto();
        public BrRouteDto route = new BrRouteDto();
        public BrPlaneDto plane = new BrPlaneDto();
        public BrJumpDto jump = new BrJumpDto();
        public BrCombatDto combat = new BrCombatDto();
        public List<BrShopDto> shops = new List<BrShopDto>();
        public List<BrCurrencyPickupDto> currency = new List<BrCurrencyPickupDto>();
        public List<BrEventDto> events = new List<BrEventDto>();
        public List<BrResultDto> results = new List<BrResultDto>();
        public BrRewardsDto rewards = new BrRewardsDto();
    }
    [Serializable] public sealed class BrMoveResultDto { public bool ok; public string state = ""; public float x, y, z; public bool relocated; public int health; }
    [Serializable] public sealed class BrHitDto { public string target = ""; public int damage; public bool head; public float distance; }
    [Serializable] public sealed class BrAmmoMagDto { public int mag, reserve; }
    [Serializable] public sealed class BrFireResultDto { public bool ok; public BrHitDto hit = new BrHitDto(); public BrAmmoMagDto ammo = new BrAmmoMagDto(); public bool DidHit => hit != null && !string.IsNullOrEmpty(hit.target); }
    [Serializable] public sealed class BrLootItemDto { public string type = "", id = "", ammo = ""; public int amount; }
    [Serializable] public sealed class BrPickupDto { public bool ok; public BrLootItemDto item = new BrLootItemDto(); }
    [Serializable] public sealed class BrMapInfoDto { public string mapId = "", hash = ""; public long seed; public float worldSize; public int grid; }
    [Serializable] public sealed class BrCabinCfg { public float halfLength = 13f, halfWidth = 3f, spacing = 1.8f, maxSpeed = 5f; }
    [Serializable] public sealed class BrGroundCfg { public float maxSpeed = 9f, maxUp = 12f, maxDown = 30f, eyeHeight = 1.6f; }
    [Serializable] public sealed class BrFreefallCfg { public float terminalSpeed = 55f, maxHorizontal = 35f, autoDeployAltitude = 90f; }
    [Serializable] public sealed class BrChuteCfg { public float maxDescent = 12f, maxHorizontal = 16f, minDeployAltitude = 20f, transitionSeconds = 2f; }
    [Serializable] public sealed class BrLandingCfg { public float safeSpeed = 14f, damagePerExcess = 6f; }
    [Serializable]
    public sealed class BrConfigDto
    {
        public int cabinSeconds = 15, quietSeconds = 5, flightSeconds = 60;
        public float planeAltitude = 450f, headshotMultiplier = 2f;
        public BrCabinCfg cabin = new BrCabinCfg();
        public BrGroundCfg ground = new BrGroundCfg();
        public BrFreefallCfg freefall = new BrFreefallCfg();
        public BrChuteCfg parachute = new BrChuteCfg();
        public BrLandingCfg landing = new BrLandingCfg();
    }

    public enum BrPhase { None, Waiting, Cabin, Flight, Combat, Ended, Cancelled }

    // ---------------------------------------------------------------- service
    public interface IBattleRoyaleService
    {
        bool IsAvailable { get; }
        string UnavailableReason { get; }
        void LoadConfig(Action<ApiResult<BrConfigDto>> done);
        void LoadMapInfo(Action<ApiResult<BrMapInfoDto>> done);
        void Queue(string modeId, Action<ApiResult<BrStateDto>> done);
        void LeaveQueue(Action<ApiResult<OkDto>> done);
        void GetState(int sinceEvent, Action<ApiResult<BrStateDto>> done);
        void Move(float x, float y, float z, float yaw, string anim, Action<ApiResult<BrMoveResultDto>> done);
        void Jump(Action<ApiResult<BrMoveResultDto>> done);
        void DeployParachute(Action<ApiResult<OkDto>> done);
        void Fire(float dx, float dy, float dz, Action<ApiResult<BrFireResultDto>> done);
        void Reload(Action<ApiResult<OkDto>> done);
        void SelectSlot(string slot, Action<ApiResult<OkDto>> done);
        void Heal(Action<ApiResult<OkDto>> done);
        void PickupLoot(string lootId, Action<ApiResult<BrPickupDto>> done);
        void PickupCurrency(string pickupId, Action<ApiResult<BrCurrencyPickupResultDto>> done);
        void OpenShop(string shopId, Action<ApiResult<BrShopOpenDto>> done);
        void BuyShopItem(string shopId, string weaponId, string requestId, Action<ApiResult<BrShopBuyDto>> done);
    }

    public sealed class OfflineBattleRoyaleService : IBattleRoyaleService
    {
        public bool IsAvailable => false;
        public string UnavailableReason => "Battle Royale matchmaking requires a signed-in account. Sign in from Settings > Account, then retry.";
        private void Fail<T>(Action<ApiResult<T>> d) where T : class => d?.Invoke(new ApiResult<T> { Ok = false, Code = "offline", Message = UnavailableReason });
        public void LoadConfig(Action<ApiResult<BrConfigDto>> d) => Fail(d);
        public void LoadMapInfo(Action<ApiResult<BrMapInfoDto>> d) => Fail(d);
        public void Queue(string m, Action<ApiResult<BrStateDto>> d) => Fail(d);
        public void LeaveQueue(Action<ApiResult<OkDto>> d) => Fail(d);
        public void GetState(int s, Action<ApiResult<BrStateDto>> d) => Fail(d);
        public void Move(float x, float y, float z, float yaw, string a, Action<ApiResult<BrMoveResultDto>> d) => Fail(d);
        public void Jump(Action<ApiResult<BrMoveResultDto>> d) => Fail(d);
        public void DeployParachute(Action<ApiResult<OkDto>> d) => Fail(d);
        public void Fire(float dx, float dy, float dz, Action<ApiResult<BrFireResultDto>> d) => Fail(d);
        public void Reload(Action<ApiResult<OkDto>> d) => Fail(d);
        public void SelectSlot(string s, Action<ApiResult<OkDto>> d) => Fail(d);
        public void Heal(Action<ApiResult<OkDto>> d) => Fail(d);
        public void PickupLoot(string id, Action<ApiResult<BrPickupDto>> d) => Fail(d);
        public void PickupCurrency(string id, Action<ApiResult<BrCurrencyPickupResultDto>> d) => Fail(d);
        public void OpenShop(string shopId, Action<ApiResult<BrShopOpenDto>> d) => Fail(d);
        public void BuyShopItem(string shopId, string weaponId, string requestId, Action<ApiResult<BrShopBuyDto>> d) => Fail(d);
    }

    public sealed class RemoteBattleRoyaleService : IBattleRoyaleService
    {
        private readonly ApiClient api;
        public RemoteBattleRoyaleService(ApiClient api) { this.api = api; }
        public bool SignedIn { get; set; }
        public Func<string> UnavailableDetail;
        public Action OnUnauthorized;
        public bool IsAvailable => SignedIn;
        public string UnavailableReason => SignedIn ? "" : (UnavailableDetail?.Invoke() ?? CommunityText.OfflineReason);

        private void Call<T>(string method, string path, Dictionary<string, object> body, Action<ApiResult<T>> done) where T : class, new()
        {
            if (!SignedIn) { done?.Invoke(new ApiResult<T> { Ok = false, Code = "offline", Message = UnavailableReason }); return; }
            api.Request<T>(method, path, body, result =>
            {
                if (result.Status == 401) OnUnauthorized?.Invoke();
                done?.Invoke(result);
            });
        }
        private static Dictionary<string, object> D(params object[] kv)
        {
            var d = new Dictionary<string, object>();
            for (int i = 0; i + 1 < kv.Length; i += 2) d[(string)kv[i]] = kv[i + 1];
            return d;
        }

        public void LoadConfig(Action<ApiResult<BrConfigDto>> done) => Call("GET", "/api/br/config", null, done);
        public void LoadMapInfo(Action<ApiResult<BrMapInfoDto>> done) => Call("GET", "/api/br/map-info", null, done);
        public void Queue(string modeId, Action<ApiResult<BrStateDto>> done) => Call("POST", "/api/br/queue", D("mode", modeId), done);
        public void LeaveQueue(Action<ApiResult<OkDto>> done) => Call("DELETE", "/api/br/queue", null, done);
        public void GetState(int since, Action<ApiResult<BrStateDto>> done) => Call("GET", "/api/br/state?since=" + since, null, done);
        public void Move(float x, float y, float z, float yaw, string anim, Action<ApiResult<BrMoveResultDto>> done) =>
            Call("POST", "/api/br/move", D("x", (double)x, "y", (double)y, "z", (double)z, "yaw", (double)yaw, "anim", anim ?? "idle"), done);
        public void Jump(Action<ApiResult<BrMoveResultDto>> done) => Call("POST", "/api/br/jump", D(), done);
        public void DeployParachute(Action<ApiResult<OkDto>> done) => Call("POST", "/api/br/parachute", D(), done);
        // Only a direction goes out. The origin is the server's own copy of the shooter's position; damage is never sent.
        public void Fire(float dx, float dy, float dz, Action<ApiResult<BrFireResultDto>> done) =>
            Call("POST", "/api/br/fire", D("dir", new List<object> { (double)dx, (double)dy, (double)dz }), done);
        public void Reload(Action<ApiResult<OkDto>> done) => Call("POST", "/api/br/reload", D(), done);
        public void SelectSlot(string slot, Action<ApiResult<OkDto>> done) => Call("POST", "/api/br/select", D("slot", slot), done);
        public void Heal(Action<ApiResult<OkDto>> done) => Call("POST", "/api/br/heal", D(), done);
        public void PickupLoot(string lootId, Action<ApiResult<BrPickupDto>> done) => Call("POST", "/api/br/loot/pickup", D("lootId", lootId), done);
        public void PickupCurrency(string pickupId, Action<ApiResult<BrCurrencyPickupResultDto>> done) => Call("POST", "/api/br/currency/pickup", D("pickupId", pickupId), done);
        public void OpenShop(string shopId, Action<ApiResult<BrShopOpenDto>> done) => Call("POST", "/api/br/shop/open", D("shopId", shopId), done);
        public void BuyShopItem(string shopId, string weaponId, string requestId, Action<ApiResult<BrShopBuyDto>> done) =>
            Call("POST", "/api/br/shop/buy", D("shopId", shopId, "weaponId", weaponId, "requestId", requestId), done);
    }

    // ---------------------------------------------------------------- session (phase and countdown logic)
    /// <summary>Turns server snapshots into what the HUD needs. Countdowns come from the server's own clock, never a local timer.</summary>
    public sealed class BattleRoyaleSession
    {
        public readonly MapEventClock Clock = new MapEventClock();
        private int lastEventSeq;
        private readonly List<BrEventDto> pending = new List<BrEventDto>();

        public BrStateDto Last { get; private set; }
        public BrPhase Phase { get; private set; } = BrPhase.None;
        public bool Corrected { get; private set; }    // the server moved the player (rejected move or water landing)

        public static BrPhase ParsePhase(string s)
        {
            switch (s)
            {
                case "waiting": return BrPhase.Waiting;
                case "cabin": return BrPhase.Cabin;
                case "flight": return BrPhase.Flight;
                case "combat": return BrPhase.Combat;
                case "ended": return BrPhase.Ended;
                case "cancelled": return BrPhase.Cancelled;
                default: return BrPhase.None;
            }
        }

        public void Apply(BrStateDto s, long localNowMs)
        {
            Clock.Sync(s.serverNow, localNowMs);
            Corrected = Last != null && (s.you.relocated && !Last.you.relocated);
            Last = s;
            Phase = ParsePhase(s.status);
            foreach (var e in s.events) if (e.seq > lastEventSeq) { pending.Add(e); lastEventSeq = e.seq; }
        }

        public int LastEventSeq => lastEventSeq;
        public List<BrEventDto> TakeEvents() { var r = new List<BrEventDto>(pending); pending.Clear(); return r; }

        public bool IsAboard => Last != null && Last.you.state == "aircraft";
        public bool CombatEnabled => Phase == BrPhase.Combat;

        /// <summary>True once the server's clock passes the jump window start, while the player is still aboard during the flight.</summary>
        public bool JumpButtonVisible(long localNowMs) =>
            Phase == BrPhase.Flight && IsAboard && Clock.ServerNow(localNowMs) >= Last.times.jumpOpensAt;

        /// <summary>Milliseconds to the next phase boundary the player cares about, by server time.</summary>
        public long CountdownMs(long localNowMs)
        {
            if (Last == null) return 0;
            long now = Clock.ServerNow(localNowMs), t;
            switch (Phase)
            {
                case BrPhase.Waiting: t = Last.waiting.fillDeadlineAt; break;
                case BrPhase.Cabin: t = Last.times.cabinEndsAt; break;
                case BrPhase.Flight: t = now < Last.times.jumpOpensAt ? Last.times.jumpOpensAt : Last.times.flightEndsAt; break;
                default: return 0;
            }
            return Math.Max(0, t - now);
        }

        public string CountdownLabel(long localNowMs)
        {
            switch (Phase)
            {
                case BrPhase.Waiting: return "Waiting for players " + Last.waiting.players + "/" + Last.waiting.minPlayers;
                case BrPhase.Cabin: return "Match starts in " + Math.Max(0, (CountdownMs(localNowMs) + 999) / 1000);
                case BrPhase.Flight:
                    return Clock.ServerNow(localNowMs) < Last.times.jumpOpensAt
                        ? "Jump opens in " + Math.Max(0, (CountdownMs(localNowMs) + 999) / 1000)
                        : (IsAboard ? "Jump! Forced exit in " + Math.Max(0, (CountdownMs(localNowMs) + 999) / 1000) : "");
                case BrPhase.Combat: return "";
                case BrPhase.Ended: return "Match over";
                case BrPhase.Cancelled: return "Match cancelled";
                default: return "";
            }
        }

        public float PollIntervalSeconds
        {
            get
            {
                switch (Phase)
                {
                    case BrPhase.Waiting: return 1.0f;
                    case BrPhase.Cabin:
                    case BrPhase.Flight: return 0.25f;
                    case BrPhase.Combat: return 0.2f;
                    default: return 1.0f;
                }
            }
        }
    }

    // ---------------------------------------------------------------- loading progress
    public enum LoadStage { ConnectingServer = 0, FindingPlayers = 1, LoadingMap = 2, PreparingCharacters = 3, StartingMatch = 4 }

    /// <summary>
    /// Weighted, honest loading model. Each stage reports real progress (HTTP result, matchmaking fill, async scene/asset load).
    /// It is complete only when every stage reported completion: nothing here advances on a timer. It also detects stalls
    /// ("still working") and stage timeouts, and supports cancellation.
    /// </summary>
    public sealed class LoadingProgress
    {
        public static readonly float[] Weights = { 0.10f, 0.25f, 0.40f, 0.20f, 0.05f };
        public static readonly float[] StageTimeoutSeconds = { 35f, 240f, 90f, 30f, 20f };
        public const float SlowAfterSeconds = 8f;

        private readonly float[] progress = new float[5];
        private readonly float[] startedAt = new float[5];
        private readonly bool[] started = new bool[5];
        private float lastAdvanceAt;

        public bool Failed { get; private set; }
        public bool Cancelled { get; private set; }
        public string FailureMessage { get; private set; } = "";
        public LoadStage? FailedStage { get; private set; }
        public bool Slow { get; private set; }

        public static string StageMessage(LoadStage s)
        {
            switch (s)
            {
                case LoadStage.ConnectingServer: return "Connecting to match backend";
                case LoadStage.FindingPlayers: return "Finding players";
                case LoadStage.LoadingMap: return "Loading the Bermuda map";
                case LoadStage.PreparingCharacters: return "Preparing characters";
                default: return "Starting match";
            }
        }

        public void Begin(float now)
        {
            lastAdvanceAt = now;
            Slow = false;
            Report(LoadStage.ConnectingServer, 0f, now);
        }

        /// <summary>Reports measured progress for a stage (0..1). Progress never goes backwards.</summary>
        public void Report(LoadStage stage, float value, float now)
        {
            if (Failed || Cancelled) return;
            int i = (int)stage;
            value = value < 0f ? 0f : value > 1f ? 1f : value;
            if (!started[i]) { started[i] = true; startedAt[i] = now; }
            if (value > progress[i]) { progress[i] = value; lastAdvanceAt = now; Slow = false; }
        }

        public void Complete(LoadStage stage, float now) => Report(stage, 1f, now);

        public float Overall
        {
            get { float sum = 0f; for (int i = 0; i < 5; i++) sum += Weights[i] * progress[i]; return sum; }
        }
        public int Percent => (int)Math.Floor(Overall * 100f + 0.0001f);
        public float StageProgress(LoadStage s) => progress[(int)s];

        public LoadStage Current
        {
            get { for (int i = 0; i < 5; i++) if (progress[i] < 1f) return (LoadStage)i; return LoadStage.StartingMatch; }
        }

        public bool IsComplete { get { if (Failed || Cancelled) return false; for (int i = 0; i < 5; i++) if (progress[i] < 1f) return false; return true; } }

        public string Message => Failed ? FailureMessage : Cancelled ? "Cancelled" : IsComplete ? "Ready" : StageMessage(Current) + (Slow ? " (slow connection, still working)" : "");

        public void Fail(string message) { if (Failed || Cancelled) return; Failed = true; FailedStage = Current; FailureMessage = message; }
        public void Cancel() { if (!Failed) Cancelled = true; }

        /// <summary>Call every frame with a monotonic clock. Flags a stall and fails a stage that exceeds its timeout.</summary>
        public void Tick(float now)
        {
            if (Failed || Cancelled || IsComplete) return;
            Slow = now - lastAdvanceAt > SlowAfterSeconds;
            var s = Current; int i = (int)s;
            if (started[i] && now - startedAt[i] > StageTimeoutSeconds[i])
                Fail("Timed out: " + StageMessage(s).ToLowerInvariant() + ". Check your connection and try again.");
        }
    }

    // ---------------------------------------------------------------- jump, freefall, parachute
    public struct AirState
    {
        public float X, Y, Z, VelY;
        public bool Chute;
    }

    /// <summary>
    /// Local presentation kinematics for freefall and parachute. It is tuned to stay well inside the limits the server
    /// publishes in /api/br/config (the server still validates every position), so a normal player is never "corrected".
    /// </summary>
    public static class JumpModel
    {
        public const float Gravity = 25f;           // how quickly freefall speed builds
        public const float SafetyFactor = 0.85f;    // stay this fraction of the server's limits

        public static float FreefallTarget(BrConfigDto c) => c.freefall.terminalSpeed * SafetyFactor;
        public static float ChuteTarget(BrConfigDto c) => c.parachute.maxDescent * 0.72f;

        public static bool CanDeploy(float altitudeAboveGround, BrConfigDto c) => altitudeAboveGround >= c.parachute.minDeployAltitude;

        public static AirState Step(AirState s, float dt, float steerX, float steerZ, BrConfigDto c, Func<float, float, float> groundHeight, out bool landed)
        {
            landed = false;
            float mag = (float)Math.Sqrt(steerX * steerX + steerZ * steerZ);
            if (mag > 1f) { steerX /= mag; steerZ /= mag; }
            float horizontal = (s.Chute ? c.parachute.maxHorizontal : c.freefall.maxHorizontal) * SafetyFactor;
            float target = s.Chute ? ChuteTarget(c) : FreefallTarget(c);
            // speed approaches the target without overshooting it (opening the chute slows the fall smoothly)
            float rate = s.Chute ? 40f : Gravity;
            float speed = -s.VelY;
            speed = speed < target ? Math.Min(target, speed + rate * dt) : Math.Max(target, speed - rate * dt);
            s.VelY = -speed;
            s.X += steerX * horizontal * dt;
            s.Z += steerZ * horizontal * dt;
            s.Y += s.VelY * dt;
            float ground = groundHeight(s.X, s.Z);
            if (s.Y <= ground) { s.Y = ground; landed = true; }
            return s;
        }
    }

    // ---------------------------------------------------------------- matchmaking
    /// <summary>
    /// PLAY for Battle Royale: queues with the backend, which only accepts the request when a verified game server is online,
    /// then follows the roster until the server starts the match. Nothing is simulated.
    /// </summary>
    public sealed class BattleRoyaleMatchmakingService : IMatchmakingService
    {
        public const string SceneName = "BattleRoyale";
        public const float PollSeconds = 1f;

        private readonly IBattleRoyaleService svc;
        private float poll;
        private bool active, inFlight;
        private int networkFailures;
        private int attemptId;
        private float attemptStartedAt;
        private string lastLoggedPhase = "";
        private int lastLoggedPlayers = -1;
        private GameModeDefinition mode;

        public BattleRoyaleMatchmakingService(IBattleRoyaleService service) { svc = service; }

        public event Action<MatchmakingStatus> StatusChanged;
        public bool CanHandle(GameModeDefinition m) => m != null && m.Id == GameModeIds.BattleRoyale;

        private void Emit(MatchmakingState s, string message, float progress = 0f, MatchmakingError err = MatchmakingError.None, MatchAssignment a = null) =>
            StatusChanged?.Invoke(new MatchmakingStatus { State = s, Error = err, Message = message, Progress = progress, Mode = mode, Assignment = a });

        public void Begin(GameModeDefinition m, MatchmakingRequest request)
        {
            int attempt = ++attemptId;
            mode = m; active = true; inFlight = true; networkFailures = 0; poll = 0f;
            attemptStartedAt = Time.realtimeSinceStartup;
            lastLoggedPhase = "";
            lastLoggedPlayers = -1;
            if (!svc.IsAvailable) { active = false; inFlight = false; Emit(MatchmakingState.Failed, svc.UnavailableReason, 0f, MatchmakingError.ServerUnavailable); return; }
            Emit(MatchmakingState.Searching, LoadingProgress.StageMessage(LoadStage.ConnectingServer), 0.05f);
            svc.Queue(m.Id, r =>
            {
                if (attempt != attemptId) return;
                inFlight = false;
                if (!active) return;
                if (r == null)
                {
                    active = false;
                    Emit(MatchmakingState.Failed, "The matchmaking service returned no response. Please retry.", 0f, MatchmakingError.ServerUnavailable);
                    return;
                }
                Debug.Log($"[Noxy BR] state=requesting_matchmaking elapsed={Time.realtimeSinceStartup - attemptStartedAt:0.0}s http={r.Status} code={SafeCode(r.Code)}");
                if (!r.Ok)
                {
                    active = false;
                    string message = r.Status == 401 ? "Your account session was rejected. Sign in again in Settings > Account, then retry." : r.Friendly;
                    Emit(MatchmakingState.Failed, message, 0f, r.Code == "no_server_available" || r.NetworkError ? MatchmakingError.ServerUnavailable : MatchmakingError.ModeUnavailable);
                    return;
                }
                Handle(r.Value);
            });
        }

        public void Cancel()
        {
            if (!active) return;
            ++attemptId;
            active = false;
            inFlight = false;
            svc.LeaveQueue(_ => { });   // best effort; the server also drops stale waiting players
            Emit(MatchmakingState.Cancelled, "Left the queue.");
        }

        public void Tick(float dt)
        {
            if (!active || inFlight) return;
            poll += dt;
            if (poll < PollSeconds) return;
            poll = 0f; inFlight = true;
            int attempt = attemptId;
            svc.GetState(0, r =>
            {
                if (attempt != attemptId) return;
                inFlight = false;
                if (!active) return;
                if (r == null)
                {
                    active = false;
                    Emit(MatchmakingState.Failed, "The matchmaking service returned no response. Please retry.", 0f, MatchmakingError.ServerUnavailable);
                    return;
                }
                if (!r.Ok)
                {
                    Debug.LogWarning($"[Noxy BR] state=waiting_for_match elapsed={Time.realtimeSinceStartup - attemptStartedAt:0.0}s http={r.Status} code={SafeCode(r.Code)}");
                    if (r.NetworkError && ++networkFailures < 8) { Emit(MatchmakingState.Searching, "Connection is slow, retrying...", 0.1f); return; }
                    active = false;
                    string message = r.Status == 401 ? "Your account session was rejected. Sign in again in Settings > Account, then retry." : r.Friendly;
                    Emit(MatchmakingState.Failed, message, 0f, MatchmakingError.ServerUnavailable);
                    return;
                }
                networkFailures = 0;
                Handle(r.Value);
            });
        }

        private void Handle(BrStateDto s)
        {
            if (s == null)
            {
                active = false;
                Emit(MatchmakingState.Failed, "The matchmaking service returned an empty match assignment. Please retry.", 0f, MatchmakingError.ServerUnavailable);
                return;
            }
            var phase = BattleRoyaleSession.ParsePhase(s.status);
            int players = s.waiting != null ? s.waiting.players : 0;
            if (s.status != lastLoggedPhase || players != lastLoggedPlayers)
            {
                Debug.Log($"[Noxy BR] state=matchmaking_assignment elapsed={Time.realtimeSinceStartup - attemptStartedAt:0.0}s phase={s.status} players={players}/{(s.waiting != null ? s.waiting.minPlayers : 0)} assignment={(string.IsNullOrEmpty(s.server?.host) ? "missing" : "received")}");
                lastLoggedPhase = s.status;
                lastLoggedPlayers = players;
            }
            if ((phase == BrPhase.Waiting || phase == BrPhase.Cabin || phase == BrPhase.Flight || phase == BrPhase.Combat) &&
                (s.server == null || string.IsNullOrEmpty(s.server.host) || s.server.port < 1 || s.server.port > 65535))
            {
                active = false;
                Emit(MatchmakingState.Failed, "The server returned a match without a valid server assignment. Please return to the lobby and retry.", 0f, MatchmakingError.ServerUnavailable);
                return;
            }
            switch (phase)
            {
                case BrPhase.Waiting:
                    if (s.waiting == null)
                    {
                        active = false;
                        Emit(MatchmakingState.Failed, "The match response is missing its player roster. Please retry.", 0f, MatchmakingError.ServerUnavailable);
                        return;
                    }
                    Emit(MatchmakingState.Searching, "Finding players " + s.waiting.players + "/" + s.waiting.minPlayers, 0.1f + 0.8f * Math.Min(1f, s.waiting.players / (float)Math.Max(1, s.waiting.minPlayers)));
                    break;
                case BrPhase.Cancelled:
                    active = false; Emit(MatchmakingState.Failed, "The match was cancelled because not enough players joined.", 0f, MatchmakingError.NotEnoughPlayers);
                    break;
                case BrPhase.Ended:
                    active = false; Emit(MatchmakingState.Failed, "That match already ended.", 0f, MatchmakingError.ServerUnavailable);
                    break;
                case BrPhase.Cabin:
                case BrPhase.Flight:
                case BrPhase.Combat:
                    active = false;
                    Emit(MatchmakingState.Succeeded, "Match found", 1f, MatchmakingError.None,
                        new MatchAssignment { ModeId = GameModeIds.BattleRoyale, SceneName = SceneName, ServerAddress = s.server.host, ServerPort = s.server.port, SessionToken = "" });
                    break;
                default:
                    active = false;
                    Emit(MatchmakingState.Failed, "The server returned an unknown match phase. Please retry or return to the lobby.", 0f, MatchmakingError.ServerUnavailable);
                    break;
            }
        }

        private static string SafeCode(string code) =>
            string.IsNullOrEmpty(code) ? "none" : code.Length <= 48 ? code : code.Substring(0, 48);
    }

    /// <summary>Keeps the Battle Royale mode honest: it is playable only while the backend lists a verified online server.</summary>
    public sealed class BattleRoyaleAvailability
    {
        public const float PollSeconds = 15f;
        private readonly ICommunityService community;
        private readonly GameModeDefinition mode;
        private readonly Func<bool> sceneAvailable;
        private float timer;
        private bool busy;

        public BattleRoyaleAvailability(ICommunityService community, GameModeDefinition mode, Func<bool> sceneAvailable)
        { this.community = community; this.mode = mode; this.sceneAvailable = sceneAvailable; timer = PollSeconds; }

        public void Tick(float dt)
        {
            timer += dt;
            if (busy || timer < PollSeconds) return;
            timer = 0f;
            if (!community.IsAvailable) { Set(false, "Sign in to play Battle Royale online."); return; }
            busy = true;
            community.LoadServers(GameModeIds.BattleRoyale, r =>
            {
                busy = false;
                if (!r.Ok) { Set(false, "Can't reach the game server list: " + r.Friendly); return; }
                int n = r.Value.servers.Count;
                if (n == 0) Set(false, "No Battle Royale game server is online right now.");
                else if (!sceneAvailable()) Set(false, "The Battle Royale scene is missing from this build (Noxy > Build Battle Royale Scene).");
                else Set(true, n + (n == 1 ? " server online" : " servers online") + " - matches need " + "at least the server's minimum player count.");
            });
        }

        private void Set(bool playable, string note)
        {
            mode.Availability = playable ? ModeAvailability.Available : ModeAvailability.ComingSoon;
            mode.StatusNote = note;
        }
    }
}
