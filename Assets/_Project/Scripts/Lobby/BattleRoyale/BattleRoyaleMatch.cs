using System;
using System.Collections;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;
using Noxy.Core;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Noxy.Lobby
{
    /// <summary>
    /// The Battle Royale scene. It joins the match the lobby's matchmaking already placed the player in, loads the island,
    /// then mirrors what the match server says: phases, countdowns, the plane, other players, damage, loot and results.
    /// Nothing here decides an outcome. See docs/BATTLE_ROYALE.md for what is and is not verified.
    /// </summary>
    public sealed class BattleRoyaleMatch : MonoBehaviour
    {
        public const string LobbySceneName = "MainLobby";
        private const float GroundMoveInterval = 0.12f;
        private const float PickupRange = 2.8f;
        private const float StaleAirStateSeconds = 1.5f;

        private sealed class Pending<T> where T : class
        {
            public bool Done; public ApiResult<T> Result;
            public void Set(ApiResult<T> r) { Result = r; Done = true; }
        }

        private Camera cam;
        private ResponsiveCanvas responsive;
        private RectTransform safeRoot, touchRoot;
        private BrHud hud;
        private HudControlsView touchHud;
        private bool touchEnabled;

        private IBattleRoyaleService svc;
        private BrConfigDto cfg;
        private BrMapInfoDto info;
        private IslandLayout layout;
        private IslandBuilder builder;
        private readonly BattleRoyaleSession session = new BattleRoyaleSession();
        private readonly LoadingProgress loading = new LoadingProgress();

        private BrPlaneRig plane;
        private PlayerController player;
        private ICharacterView view;
        private BrAirController air;
        private readonly Dictionary<string, BrRemoteAvatar> remotes = new Dictionary<string, BrRemoteAvatar>();
        private readonly HashSet<string> pickedLoot = new HashSet<string>();

        private bool ready, finished, failedShown, paused, cabinStarted, pollInFlight, moveInFlight, forceMirror, resyncWanted;
        private float pollTimer, groundTimer, lastFireAt, lastSwitchAt, landedAt = -100f, networkTrouble;
        private Vector3 lastGroundPos;
        private LootSpawn nearbyLoot;

        private static long NowMs() => (long)(Time.realtimeSinceStartup * 1000f);
        private static float Now() => Time.realtimeSinceStartup;

        // ---------------------------------------------------------------- setup
        private void Awake()
        {
            AppServices.EnsureInitialized();
            EventSystemFactory.Ensure();
            Screen.orientation = ScreenOrientation.AutoRotation;
            svc = AppServices.BattleRoyale;
            touchEnabled = Application.isMobilePlatform;

            cam = Camera.main;
            if (cam == null)
            {
                var camGo = new GameObject("Main Camera", typeof(Camera), typeof(AudioListener));
                camGo.tag = "MainCamera";
                cam = camGo.GetComponent<Camera>();
            }
            cam.clearFlags = CameraClearFlags.SolidColor;
            cam.backgroundColor = new Color(0.55f, 0.72f, 0.9f);
            cam.nearClipPlane = 0.1f;
            cam.farClipPlane = 1800f;
            RenderSettings.fog = true; RenderSettings.fogMode = FogMode.Linear;
            RenderSettings.fogColor = cam.backgroundColor; RenderSettings.fogStartDistance = 350f; RenderSettings.fogEndDistance = 1700f;
            RenderSettings.ambientMode = UnityEngine.Rendering.AmbientMode.Flat;
            RenderSettings.ambientLight = new Color(0.55f, 0.58f, 0.64f);
            QualitySettings.shadowDistance = 120f;
            var sun = new GameObject("Sun").AddComponent<Light>();
            sun.type = LightType.Directional; sun.intensity = 1.1f; sun.shadows = LightShadows.Soft;
            sun.transform.rotation = Quaternion.Euler(52f, -35f, 0f);

            var canvas = LobbyCanvas.Create("BrCanvas", 0, out responsive, out safeRoot);
            responsive.LayoutChanged += OnLayoutChanged;
            hud = BrHud.Create(safeRoot);
            hud.CancelLoading += OnCancelLoading;
            hud.RetryClicked += () => SceneManager.LoadScene(gameObject.scene.name);
            hud.ReturnToLobby += LeaveToLobby;
            hud.JumpClicked += () => air?.TryJump();
            hud.DeployClicked += () => air?.TryDeploy();
            hud.PickupClicked += TryPickup;
            hud.HealClicked += TryHeal;
            hud.ResumeClicked += () => SetPaused(false);
            hud.LeaveClicked += LeaveToLobby;
            hud.BindLoading(loading);

            AppServices.Account.PresenceProvider = () => "in_match";
            loading.Begin(Now());
            StartCoroutine(Run());
        }

        private void OnDestroy()
        {
            Time.timeScale = 1f;
            Cursor.lockState = CursorLockMode.None; Cursor.visible = true;
            if (AppServices.IsInitialized) AppServices.Account.PresenceProvider = () => "lobby";
            if (responsive != null) responsive.LayoutChanged -= OnLayoutChanged;
        }

        private bool Aborted => loading.Failed || loading.Cancelled;

        private IEnumerator Wait<T>(Pending<T> p) where T : class
        {
            while (!p.Done) { if (Aborted) yield break; yield return null; }
        }

        private void Fail(string message) { loading.Fail(message); }

        // ---------------------------------------------------------------- loading pipeline
        private IEnumerator Run()
        {
            // 1. connect: config, map identity, and the match this player was placed in
            if (!svc.IsAvailable) { Fail(svc.UnavailableReason); yield break; }
            var pc = new Pending<BrConfigDto>(); svc.LoadConfig(pc.Set);
            yield return Wait(pc);
            if (Aborted) yield break;
            if (!pc.Result.Ok) { Fail("Couldn't reach the match server: " + pc.Result.Friendly); yield break; }
            cfg = pc.Result.Value; loading.Report(LoadStage.ConnectingServer, 0.4f, Now());

            var pm = new Pending<BrMapInfoDto>(); svc.LoadMapInfo(pm.Set);
            yield return Wait(pm);
            if (Aborted) yield break;
            if (!pm.Result.Ok) { Fail("Couldn't read the map version: " + pm.Result.Friendly); yield break; }
            info = pm.Result.Value; loading.Report(LoadStage.ConnectingServer, 0.7f, Now());

            var ps = new Pending<BrStateDto>(); svc.GetState(0, ps.Set);
            yield return Wait(ps);
            if (Aborted) yield break;
            if (!ps.Result.Ok)
            {
                Fail(ps.Result.Code == "not_in_match" || ps.Result.Code == "no_match" ? "You are not in a match. Go back to the lobby and press PLAY." : "Couldn't join the match: " + ps.Result.Friendly);
                yield break;
            }
            session.Apply(ps.Result.Value, NowMs());
            loading.Complete(LoadStage.ConnectingServer, Now());

            // 2. roster: the lobby normally finishes this; a late start can still be waiting
            while (session.Phase == BrPhase.Waiting)
            {
                var w = session.Last.waiting;
                loading.Report(LoadStage.FindingPlayers, Mathf.Clamp01(w.players / (float)Mathf.Max(1, w.minPlayers)) * 0.95f, Now());
                var pw = new Pending<BrStateDto>(); svc.GetState(0, pw.Set);
                yield return Wait(pw);
                if (Aborted) yield break;
                if (!pw.Result.Ok) { Fail(pw.Result.Friendly); yield break; }
                session.Apply(pw.Result.Value, NowMs());
                float t = 0f; while (t < 1f) { if (Aborted) yield break; t += Time.unscaledDeltaTime; yield return null; }
            }
            if (session.Phase == BrPhase.Cancelled) { Fail("The match was cancelled: not enough players joined."); yield break; }
            if (session.Phase == BrPhase.Ended) { Fail("That match has already ended."); yield break; }
            loading.Complete(LoadStage.FindingPlayers, Now());

            // 3. the island: generated from the server's seed and verified against its hash
            loading.Report(LoadStage.LoadingMap, 0.02f, Now());
            yield return null;   // let the loading screen draw before the long generation step
            layout = IslandGenerator.Generate((uint)info.seed);
            loading.Report(LoadStage.LoadingMap, 0.12f, Now());
            yield return null;
            string localHash = MapHash(layout);
            if (localHash != info.hash)
            {
                Fail("This build's Bermuda map (" + localHash + ") does not match the server's (" + info.hash + "). Update the game and try again.");
                yield break;
            }
            loading.Report(LoadStage.LoadingMap, 0.2f, Now());
            builder = new IslandBuilder { VegetationDensity = Application.isMobilePlatform ? 0.5f : 1f };
            yield return StartCoroutine(builder.Build(layout, transform, p => loading.Report(LoadStage.LoadingMap, 0.2f + 0.8f * p, Now())));
            if (Aborted) yield break;
            loading.Complete(LoadStage.LoadingMap, Now());

            // 4. characters
            plane = BrPlaneRig.Create(cfg.cabin);
            loading.Report(LoadStage.PreparingCharacters, 0.3f, Now());
            yield return null;
            BuildLocalPlayer();
            loading.Report(LoadStage.PreparingCharacters, 0.7f, Now());
            yield return null;
            SyncRemotes(session.Last);
            loading.Complete(LoadStage.PreparingCharacters, Now());

            // 5. start
            hud.BindMaps(layout, builder.ColorTexture);
            hud.FitFullMap(responsive.SafeSize);
            BuildTouchHud();
            loading.Complete(LoadStage.StartingMatch, Now());
            hud.HideLoading();
            ready = true;
            pollTimer = 99f;   // poll immediately
            HandleState();
        }

        private static string MapHash(IslandLayout l)
        {
            using (var sha = SHA256.Create())
            {
                var bytes = sha.ComputeHash(Encoding.UTF8.GetBytes(IslandExport.ToJson(l)));
                var sb = new StringBuilder();
                for (int i = 0; i < 8; i++) sb.Append(bytes[i].ToString("x2"));
                return sb.ToString();
            }
        }

        private void BuildLocalPlayer()
        {
            var go = new GameObject("LocalPlayer");
            go.transform.SetParent(transform, false);
            view = CharacterViewFactory.Create(go.transform);
            view.Apply(AppServices.Profile.Profile.loadout, AppServices.Catalog);
            player = go.AddComponent<PlayerController>();
            player.Setup(cam, view);
            player.ClearLocalLoadout();   // the server owns the inventory in an online match
            player.UseMouseLook = !touchEnabled;
            player.RemoteShot = OnShot;
            player.RemoteSwitch = slot => { lastSwitchAt = Time.time; svc.SelectSlot(SlotName(slot), r => { if (!r.Ok) { hud.Toast(r.Friendly); forceMirror = true; } }); };
            player.RemoteReload = () => svc.Reload(r => { if (!r.Ok) { hud.Toast(r.Friendly); forceMirror = true; } });
            player.Message += m => hud.Toast(m);
            player.SetExternallyControlled(true);
            go.transform.position = new Vector3(0f, -1000f, 0f);

            air = go.AddComponent<BrAirController>();
            air.Init(cam, player, view, plane, svc, cfg, layout, null);
            air.UseMouseLook = !touchEnabled;
            air.Message += m => hud.Toast(m);
            air.ResyncRequested += () => { resyncWanted = true; pollTimer = 99f; };
            air.Landed += OnLanded;
            air.Exited += forced => hud.Toast(forced ? "The plane's route ended: you were dropped." : "Jumped!", 2.5f);
        }

        private void BuildTouchHud()
        {
            if (touchRoot != null) Destroy(touchRoot.gameObject);
            touchHud = null;
            if (touchEnabled)
            {
                touchRoot = UIKit.NewRect("TouchHud", safeRoot);
                UIKit.Stretch(touchRoot);
                touchRoot.SetAsFirstSibling();
                var layoutHud = AppServices.Config.LoadHud(responsive.IsPortrait, responsive.SafeSize);
                touchHud = HudControlsView.Create(touchRoot, layoutHud, false);
                touchHud.Relayout(responsive.SafeSize);
            }
            if (player != null) { player.Touch = touchHud; player.UseMouseLook = !touchEnabled; }
            if (air != null) { air.SetTouch(touchHud != null ? touchHud.State : null); air.UseMouseLook = !touchEnabled; }
        }

        private void OnLayoutChanged()
        {
            if (!ready) return;
            BuildTouchHud();
            hud.FitFullMap(responsive.SafeSize);
        }

        private void OnCancelLoading()
        {
            loading.Cancel();
            svc.LeaveQueue(_ => { });   // only succeeds while the match is still waiting for players
            LeaveToLobby();
        }

        private void LeaveToLobby()
        {
            Time.timeScale = 1f;
            SceneManager.LoadScene(LobbySceneName);
        }

        // ---------------------------------------------------------------- per frame
        private void Update()
        {
            float dt = Time.unscaledDeltaTime;
            if (!ready)
            {
                if (!failedShown && (loading.Failed)) { failedShown = true; hud.ShowLoadingFailure(loading.FailureMessage); }
                loading.Tick(Now());
                return;
            }

            var keys = AppServices.Config.Bindings;
            if (InputCompat.KeyDown(keys.Get(GameAction.Pause)) || InputCompat.BackPressed)
            {
                if (hud.FullMapOpen) hud.CloseFullMap(); else SetPaused(!paused);
            }
            if (!paused && (InputCompat.KeyDown(keys.Get(GameAction.Map)) || (touchHud != null && touchHud.State.ConsumeMap()))) hud.ToggleFullMap();
            if (!paused && InputCompat.KeyDown(keys.Get(GameAction.Interact)) && nearbyLoot != null) TryPickup();
            if (!paused && InputCompat.KeyDown(KeyCode.H)) TryHeal();

            bool uiOpen = paused || hud.FullMapOpen || finished;
            player.InputEnabled = !uiOpen;
            air.InputEnabled = !uiOpen;
            ApplyCursor(uiOpen);

            Poll(dt);
            UpdatePlane();
            if (!finished) UpdateGround(dt);
            UpdateHud();
        }

        private void ApplyCursor(bool uiOpen)
        {
            bool lockCursor = !uiOpen && !touchEnabled && !Application.isMobilePlatform;
            Cursor.lockState = lockCursor ? CursorLockMode.Locked : CursorLockMode.None;
            Cursor.visible = !lockCursor;
        }

        private void SetPaused(bool value)
        {
            paused = value;
            hud.ShowPause(value);
        }

        private void Poll(float dt)
        {
            pollTimer += dt;
            if (pollInFlight || finished && !forceMirror) return;
            if (pollTimer < session.PollIntervalSeconds) return;
            pollTimer = 0f; pollInFlight = true;
            svc.GetState(session.LastEventSeq, r =>
            {
                pollInFlight = false;
                if (!r.Ok)
                {
                    networkTrouble += session.PollIntervalSeconds;
                    if (r.Code == "match_over" || r.Code == "not_in_match") { finished = true; hud.Toast("The match is over."); return; }
                    if (networkTrouble > 3f) hud.Toast("Connection problem - reconnecting...", 1.5f);
                    return;
                }
                networkTrouble = 0f;
                session.Apply(r.Value, NowMs());
                HandleState();
            });
        }

        private void UpdatePlane()
        {
            var s = session.Last;
            if (s == null || plane == null) return;
            long now = session.Clock.ServerNow(NowMs());
            bool show = session.Phase == BrPhase.Cabin || session.Phase == BrPhase.Flight || (session.Phase == BrPhase.Combat && now < s.times.flightEndsAt + 5000);
            plane.gameObject.SetActive(show);
            if (show) plane.Pose(s.route, s.times, now);
        }

        private void UpdateGround(float dt)
        {
            if (air.Mode != AirMode.None || session.Last == null || session.Last.you.state != "landed") { UpdateLootPrompt(false); return; }
            var pos = player.transform.position;
            groundTimer += dt;
            if (groundTimer >= GroundMoveInterval && !moveInFlight)
            {
                float speed = (pos - lastGroundPos).magnitude / Mathf.Max(0.001f, groundTimer);
                string anim = player.IsCrouching ? "crouch" : speed > 5f ? "run" : speed > 0.4f ? "walk" : "idle";
                groundTimer = 0f; lastGroundPos = pos; moveInFlight = true;
                svc.Move(pos.x, pos.y, pos.z, player.Yaw, anim, r =>
                {
                    moveInFlight = false;
                    if (!r.Ok && !r.NetworkError && r.Code != "match_over") { resyncWanted = true; pollTimer = 99f; }
                });
            }
            UpdateLootPrompt(true);
        }

        private void UpdateLootPrompt(bool active)
        {
            nearbyLoot = null;
            if (active && layout != null)
            {
                var p = player.transform.position; float best = PickupRange * PickupRange;
                foreach (var l in layout.Loot)
                {
                    if (pickedLoot.Contains(l.Id) || Mathf.Abs(l.Y - p.y) > 3f) continue;
                    float dx = l.X - p.x, dz = l.Z - p.z, d = dx * dx + dz * dz;
                    if (d < best) { best = d; nearbyLoot = l; }
                }
            }
            hud.SetPickup(nearbyLoot != null ? "OPEN " + nearbyLoot.Kind.ToUpperInvariant() + "  [" + AppServices.Config.Bindings.Get(GameAction.Interact) + "]" : "", nearbyLoot != null && !finished);
        }

        private void UpdateHud()
        {
            var s = session.Last;
            if (s == null) return;
            long local = NowMs();
            hud.SetJump(session.JumpButtonVisible(local) && air.Mode == AirMode.Cabin);
            air.SetJumpOpen(session.JumpButtonVisible(local));
            hud.SetDeploy(air.Mode == AirMode.Freefall, air.CanDeployNow);

            string banner = session.CountdownLabel(local);
            long now = session.Clock.ServerNow(local);
            if (session.Phase == BrPhase.Flight && !session.IsAboard && now < s.times.flightEndsAt) banner = "Weapons free in " + Mathf.Max(0, (s.times.flightEndsAt - now + 999) / 1000);
            hud.SetBanner(banner);

            if (air.InAir) hud.SetFlightInfo("ALT " + Mathf.RoundToInt(air.Altitude) + " m   SPEED " + Mathf.RoundToInt(air.FallSpeed) + " m/s" + (air.Mode == AirMode.Freefall ? "   Steer with move keys" : ""));
            else if (session.Phase == BrPhase.Combat || air.Mode == AirMode.None)
            {
                int alive = 0; foreach (var p in s.players) if (p.alive) alive++;
                hud.SetFlightInfo("ALIVE " + alive + "   KILLS " + s.you.kills);
            }
            else hud.SetFlightInfo(air.Mode == AirMode.Cabin ? "Walk to the door. JUMP opens " + (session.Phase == BrPhase.Flight ? "soon." : "after take-off.") : "");

            var w = player.Weapons; var cur = w.CurrentWeapon;
            string weaponLine = cur != null ? cur.DisplayName.ToUpperInvariant() : "UNARMED";
            string ammoLine = cur == null ? "" : (cur.MagazineSize > 0 ? w.Magazine(w.Selected) + " / " + w.Reserve(w.Selected) : "MELEE");
            hud.SetVitals(s.you, weaponLine, ammoLine);
            hud.SetHealVisible(air.Mode == AirMode.None && s.you.medkits > 0 && s.you.health < 100 && !finished);

            Vector3 where = air.Mode == AirMode.Cabin && plane != null ? plane.transform.position : player.transform.position;
            Vector3? planePos = plane != null && plane.gameObject.activeSelf ? plane.transform.position : (Vector3?)null;
            float yaw = air.Mode == AirMode.None ? player.Yaw : air.Yaw;
            hud.RefreshMaps(where, yaw, planePos, s.route);
        }

        // ---------------------------------------------------------------- server state -> scene
        private void HandleState()
        {
            var s = session.Last;
            if (s == null || !ready) return;
            var you = s.you;
            SyncRemotes(s);
            foreach (var e in session.TakeEvents()) HandleEvent(e);

            bool recentlyLanded = Time.time - landedAt < StaleAirStateSeconds;
            if (you.state == "aircraft" && !cabinStarted && (session.Phase == BrPhase.Cabin || session.Phase == BrPhase.Flight))
            {
                cabinStarted = true; air.BeginCabin(you.x, you.z);
            }
            else if ((you.state == "freefall" || you.state == "parachute") && (air.Mode == AirMode.Cabin || air.Mode == AirMode.None) && !recentlyLanded)
            {
                cabinStarted = true; air.Reconcile(you, false);   // the server forced the exit (or this is a reconnect)
            }
            else if (you.state == "landed" && air.InAir) air.Reconcile(you, you.relocated);
            else if (you.state == "landed" && air.Mode == AirMode.None && !cabinStarted)
            {
                cabinStarted = true; OnLanded(new Vector3(you.x, you.y, you.z), 0f, false);   // joined or reconnected on the ground
            }
            else if (resyncWanted)
            {
                if (air.Mode != AirMode.None) air.Reconcile(you, false);
                else if (you.state == "landed") { player.Snap(new Vector3(you.x, you.y, you.z), player.Yaw); hud.Toast("Position corrected by the server", 1.5f); }
            }
            resyncWanted = false;

            Mirror(you);

            if (!finished && (session.Phase == BrPhase.Ended || session.Phase == BrPhase.Cancelled || you.state == "eliminated")) ShowEnd();
        }

        private void OnLanded(Vector3 pos, float yaw, bool relocated)
        {
            landedAt = Time.time; lastGroundPos = pos; forceMirror = true;
            hud.Toast(relocated ? "Water landing - the server moved you to the nearest land" : "Landed", 2.5f);
            pollTimer = 99f;
            if (air.Mode != AirMode.None) return;
            if (!player.enabled) { player.SetExternallyControlled(false); player.Snap(pos, yaw); }
        }

        private void SyncRemotes(BrStateDto s)
        {
            if (s == null || plane == null) return;
            var seen = new HashSet<string>();
            foreach (var p in s.players)
            {
                if (p.publicId == s.you.publicId) continue;
                seen.Add(p.publicId);
                if (!remotes.TryGetValue(p.publicId, out var a)) { a = BrRemoteAvatar.Create(p.publicId, p.username); remotes[p.publicId] = a; }
                a.Apply(p, plane.transform, plane.Cabin);
            }
            List<string> gone = null;
            foreach (var kv in remotes) if (!seen.Contains(kv.Key)) (gone ?? (gone = new List<string>())).Add(kv.Key);
            if (gone != null) foreach (var id in gone) { if (remotes[id] != null) Destroy(remotes[id].gameObject); remotes.Remove(id); }
        }

        private void HandleEvent(BrEventDto e)
        {
            switch (e.type)
            {
                case "elimination":
                    string who = string.IsNullOrEmpty(e.victimName) ? "A player" : e.victimName;
                    hud.AddFeed(!string.IsNullOrEmpty(e.killerName) ? e.killerName + "  eliminated  " + who : who + " was eliminated" + (e.reason == "abandoned" ? " (left the match)" : ""));
                    break;
                case "phase":
                    if (e.phase == "flight") hud.Toast("The plane is on its way", 2.5f);
                    else if (e.phase == "combat") hud.Toast("Weapons free!", 2.5f);
                    break;
            }
        }

        // ---------------------------------------------------------------- inventory and combat
        private static string SlotName(WeaponSlot s) => s == WeaponSlot.Primary ? "primary" : s == WeaponSlot.Secondary ? "secondary" : "melee";

        private static WeaponDefinition DefFor(string id)
        {
            if (id == DefaultWeapons.Rifle.Id) return DefaultWeapons.Rifle;
            if (id == DefaultWeapons.Pistol.Id) return DefaultWeapons.Pistol;
            if (id == DefaultWeapons.Knife.Id) return DefaultWeapons.Knife;
            return null;
        }

        private static int ReserveFor(string id, BrAmmoDto ammo) => id == DefaultWeapons.Rifle.Id ? ammo.rifle : id == DefaultWeapons.Pistol.Id ? ammo.pistol : 0;

        private void Mirror(BrYouDto you)
        {
            if (player == null) return;
            var w = player.Weapons;
            bool firedRecently = Time.time - lastFireAt < 0.7f;
            MirrorSlot(w, WeaponSlot.Primary, you.weapons.primary, you.ammo, firedRecently);
            MirrorSlot(w, WeaponSlot.Secondary, you.weapons.secondary, you.ammo, firedRecently);
            MirrorSlot(w, WeaponSlot.Melee, you.weapons.melee, you.ammo, firedRecently);
            if (Time.time - lastSwitchAt > 0.6f)
            {
                var sel = you.selected == "secondary" ? WeaponSlot.Secondary : you.selected == "melee" ? WeaponSlot.Melee : WeaponSlot.Primary;
                w.SelectAuthoritative(sel);
            }
            forceMirror = false;
        }

        private void MirrorSlot(WeaponInventory w, WeaponSlot slot, BrWeaponDto d, BrAmmoDto ammo, bool firedRecently)
        {
            if (BrWeaponDto.IsNullOrEmpty(d)) { if (w.HasWeapon(slot)) w.SetAuthoritative(slot, null, 0, 0); return; }
            var def = DefFor(d.id);
            if (def == null) return;
            int reserve = ReserveFor(d.id, ammo);
            bool changed = !w.HasWeapon(slot) || w.Weapon(slot).Id != d.id;
            bool drift = w.Magazine(slot) != d.mag || w.Reserve(slot) != reserve;
            if (changed || forceMirror || (drift && !firedRecently && !w.IsReloading)) w.SetAuthoritative(slot, def, d.mag, reserve);
        }

        private void OnShot(Vector3 origin, Vector3 dir, WeaponDefinition weapon)
        {
            lastFireAt = Time.time;
            if (!session.CombatEnabled) { hud.Toast("Weapons are locked until the plane's route ends", 1.5f); forceMirror = true; return; }
            svc.Fire(dir.x, dir.y, dir.z, r =>
            {
                if (!r.Ok)
                {
                    if (r.Code != "fire_rate") { hud.Toast(r.Friendly, 1.5f); forceMirror = true; }
                    return;
                }
                if (r.Value.DidHit) hud.ShowHit(r.Value.hit.head);
            });
        }

        private void TryPickup()
        {
            if (nearbyLoot == null) return;
            var loot = nearbyLoot;
            svc.PickupLoot(loot.Id, r =>
            {
                if (r.Ok)
                {
                    pickedLoot.Add(loot.Id);
                    var i = r.Value.item;
                    hud.Toast("Picked up " + (i != null && !string.IsNullOrEmpty(i.id) ? i.id.Replace('_', ' ') : (i != null ? i.type : "item")) + (i != null && i.amount > 1 ? " x" + i.amount : ""), 2f);
                    forceMirror = true; pollTimer = 99f;
                }
                else
                {
                    if (r.Code == "already_taken") pickedLoot.Add(loot.Id);
                    hud.Toast(r.Friendly, 2f);
                }
            });
        }

        private void TryHeal()
        {
            if (air.Mode != AirMode.None || session.Last == null || session.Last.you.medkits <= 0) return;
            svc.Heal(r => hud.Toast(r.Ok ? "Using medkit..." : r.Friendly, 1.5f));
        }

        // ---------------------------------------------------------------- end of match
        private void ShowEnd()
        {
            finished = true;
            var s = session.Last; var you = s.you;
            var sb = new StringBuilder();
            if (session.Phase == BrPhase.Cancelled) sb.AppendLine("MATCH CANCELLED").AppendLine().AppendLine("Not enough players joined. Nothing was lost.");
            else
            {
                bool won = you.placement == 1;
                sb.AppendLine(won ? "VICTORY" : (you.state == "eliminated" ? "ELIMINATED" : "MATCH OVER"));
                sb.AppendLine();
                if (you.placement > 0) sb.AppendLine("Placement  #" + you.placement + " of " + s.players.Count);
                sb.AppendLine("Eliminations  " + you.kills);
                sb.AppendLine();
                if (session.Phase == BrPhase.Ended)
                {
                    sb.AppendLine(s.rewards.processed ? "Match rewards were processed by the server." : (string.IsNullOrEmpty(s.rewards.error) ? "Match rewards are pending." : "Rewards could not be processed: " + s.rewards.error));
                    var ranked = new List<BrResultDto>(s.results);
                    ranked.Sort((a, b) => a.placement.CompareTo(b.placement));
                    for (int i = 0; i < ranked.Count && i < 5; i++) sb.AppendLine("#" + ranked[i].placement + "  " + ranked[i].username + "  (" + ranked[i].kills + ")");
                }
                else sb.AppendLine("The match is still running. Spectating is not built yet.");
            }
            hud.ShowResults(sb.ToString());
        }
    }
}
