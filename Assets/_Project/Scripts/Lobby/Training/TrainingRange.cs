using System.Collections.Generic;
using Noxy.Core;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

namespace Noxy.Lobby
{
    /// <summary>
    /// Offline Training range: a small playable scene that exercises movement, camera sensitivity, three weapon slots,
    /// touch controls (with the saved HUD layout), key bindings and the radar. There are no opponents and no
    /// networking; targets are local practice dummies.
    /// </summary>
    public sealed class TrainingRange : MonoBehaviour
    {
        public const string LobbySceneName = "MainLobby";
        private static readonly Vector3 ObjectivePosition = new Vector3(0f, 0f, 24f);

        private Camera cam;
        private PlayerController player;
        private readonly List<TargetDummy> targets = new List<TargetDummy>();
        private ResponsiveCanvas responsive;
        private RectTransform canvasRoot, safeRoot, hudRoot;
        private HudControlsView touchHud;
        private RadarView radar;
        private bool touchEnabled;
        private bool paused;
        private int kills;

        private Text weaponText, ammoText, scoreText, messageText, hintText;
        private Image reloadFill;
        private Image[] crosshair;
        private Image hitMarker;
        private readonly Image[] slotBoxes = new Image[3];
        private float messageTimer, hitTimer;
        private GameObject pausePanel;
        private string previousPresence;

        private void Awake()
        {
            AppServices.EnsureInitialized();
            EventSystemFactory.Ensure();
            Screen.orientation = ScreenOrientation.AutoRotation;

            cam = Camera.main;
            if (cam == null)
            {
                var camGo = new GameObject("Main Camera", typeof(Camera), typeof(AudioListener));
                camGo.tag = "MainCamera";
                cam = camGo.GetComponent<Camera>();
            }
            cam.clearFlags = CameraClearFlags.SolidColor;
            cam.backgroundColor = new Color(0.45f, 0.6f, 0.75f);
            cam.nearClipPlane = 0.1f;
            cam.farClipPlane = 200f;

            BuildWorld();
            BuildPlayer();

            touchEnabled = Application.isMobilePlatform;
            var canvas = LobbyCanvas.Create("TrainingCanvas", 0, out responsive, out safeRoot);
            canvasRoot = (RectTransform)canvas.transform;
            responsive.LayoutChanged += OnLayoutChanged;
            BuildPauseMenu();
            BuildHud();

            AppServices.Account.PresenceProvider = () => "in_match";
        }

        private void OnDestroy()
        {
            Time.timeScale = 1f;
            Cursor.lockState = CursorLockMode.None;
            Cursor.visible = true;
            if (AppServices.IsInitialized) AppServices.Account.PresenceProvider = () => "lobby";
            if (responsive != null) responsive.LayoutChanged -= OnLayoutChanged;
        }

        // ---------- world ----------

        private GameObject Solid(PrimitiveType type, string name, Vector3 pos, Vector3 scale, Color color, bool shadows = true)
        {
            var go = GameObject.CreatePrimitive(type);
            go.name = name;
            go.transform.SetParent(transform, false);
            go.transform.position = pos;
            go.transform.localScale = scale;
            var r = go.GetComponent<Renderer>();
            r.sharedMaterial = MaterialFactory.Lit(color, 0f, 0.2f);
            r.shadowCastingMode = shadows ? UnityEngine.Rendering.ShadowCastingMode.On : UnityEngine.Rendering.ShadowCastingMode.Off;
            return go;
        }

        private void BuildWorld()
        {
            RenderSettings.ambientMode = UnityEngine.Rendering.AmbientMode.Flat;
            RenderSettings.ambientLight = new Color(0.5f, 0.54f, 0.6f);

            var sun = new GameObject("Sun").AddComponent<Light>();
            sun.transform.SetParent(transform, false);
            sun.type = LightType.Directional;
            sun.transform.rotation = Quaternion.Euler(50f, -30f, 0f);
            sun.shadows = LightShadows.Soft;

            Solid(PrimitiveType.Cube, "Ground", new Vector3(0f, -0.5f, 10f), new Vector3(60f, 1f, 80f), new Color(0.33f, 0.4f, 0.3f), false);

            var wall = new Color(0.55f, 0.57f, 0.6f);
            Solid(PrimitiveType.Cube, "WallN", new Vector3(0f, 2f, 50f), new Vector3(60f, 4f, 1f), wall);
            Solid(PrimitiveType.Cube, "WallS", new Vector3(0f, 2f, -30f), new Vector3(60f, 4f, 1f), wall);
            Solid(PrimitiveType.Cube, "WallE", new Vector3(30f, 2f, 10f), new Vector3(1f, 4f, 80f), wall);
            Solid(PrimitiveType.Cube, "WallW", new Vector3(-30f, 2f, 10f), new Vector3(1f, 4f, 80f), wall);

            var cover = new Color(0.45f, 0.38f, 0.3f);
            Solid(PrimitiveType.Cube, "Cover1", new Vector3(-6f, 0.6f, 8f), new Vector3(3f, 1.2f, 0.8f), cover);
            Solid(PrimitiveType.Cube, "Cover2", new Vector3(5f, 0.6f, 12f), new Vector3(3f, 1.2f, 0.8f), cover);
            Solid(PrimitiveType.Cube, "Cover3", new Vector3(0f, 1f, 6f), new Vector3(1.2f, 2f, 1.2f), cover);
            Solid(PrimitiveType.Cube, "Step", new Vector3(10f, 0.25f, 4f), new Vector3(4f, 0.5f, 4f), cover);
            Solid(PrimitiveType.Cube, "Platform", new Vector3(-12f, 0.75f, 16f), new Vector3(5f, 1.5f, 5f), cover);

            // Objective marker (radar shows it as the training zone).
            var zone = Solid(PrimitiveType.Cylinder, "TrainingZone", ObjectivePosition + Vector3.up * 0.02f, new Vector3(6f, 0.02f, 6f), new Color(0.18f, 0.88f, 1f), false);
            Destroy(zone.GetComponent<Collider>());

            // Target lanes at increasing distance.
            float[] distances = { 10f, 15f, 20f, 28f, 36f };
            for (int i = 0; i < distances.Length; i++)
            {
                float x = (i % 2 == 0 ? -1f : 1f) * (2f + i * 1.5f);
                AddTarget(new Vector3(x, 0f, 6f + distances[i]));
            }
            AddTarget(new Vector3(-18f, 1.5f, 16f));
        }

        private void AddTarget(Vector3 pos)
        {
            var root = new GameObject("TargetDummy");
            root.transform.SetParent(transform, false);
            root.transform.position = pos;

            var bodyGo = GameObject.CreatePrimitive(PrimitiveType.Capsule);
            bodyGo.transform.SetParent(root.transform, false);
            bodyGo.transform.localPosition = new Vector3(0f, 1f, 0f);
            bodyGo.transform.localScale = new Vector3(0.8f, 1f, 0.8f);
            var mat = MaterialFactory.Lit(new Color(0.8f, 0.35f, 0.2f), 0f, 0.3f);
            bodyGo.GetComponent<Renderer>().sharedMaterial = mat;

            var dummy = root.AddComponent<TargetDummy>();
            dummy.Died += _ => { kills++; };
            targets.Add(dummy);
        }

        private void BuildPlayer()
        {
            var go = new GameObject("Player");
            go.transform.SetParent(transform, false);
            go.transform.position = new Vector3(0f, 0.1f, -2f);

            var view = CharacterViewFactory.Create(go.transform);
            view.Apply(AppServices.Profile.Profile.loadout, AppServices.Catalog);

            player = go.AddComponent<PlayerController>();
            player.Setup(cam, view);
            player.Message += ShowMessage;
            player.HitRegistered += killed => { hitTimer = 0.18f; if (killed) ShowMessage("Target down"); };
        }

        // ---------- HUD ----------

        private void BuildHud()
        {
            if (hudRoot != null) Destroy(hudRoot.gameObject);
            hudRoot = UIKit.NewRect("Hud", safeRoot);
            UIKit.Stretch(hudRoot);
            hudRoot.SetAsFirstSibling();

            var s = AppServices.Config.Settings;
            touchHud = null;

            if (touchEnabled)
            {
                var layout = AppServices.Config.LoadHud(responsive.IsPortrait, responsive.SafeSize);
                touchHud = HudControlsView.Create(hudRoot, layout, false);
                touchHud.Relayout(responsive.SafeSize);
            }
            player.Touch = touchHud;
            player.UseMouseLook = !touchEnabled;

            BuildInfoPanel();
            if (s.showCrosshair) BuildCrosshair();
            if (!touchEnabled) BuildDesktopHints();

            radar = RadarView.Create(hudRoot);
            radar.Bind(player.transform, GameModeIds.Training, FillMarkers);
            PlaceRadar();

            var pauseButton = UIKit.NewButton(hudRoot, "PauseButton", "II", UITheme.Chip, () => SetPaused(true), 28);
            UIKit.Anchor(pauseButton.Background.rectTransform, Vector2.one, Vector2.one, Vector2.one, new Vector2(-12f, -12f), new Vector2(72f, 72f));
        }

        private void PlaceRadar()
        {
            var s = AppServices.Config.Settings;
            if (touchHud != null && touchHud.GetRect(HudElementId.Minimap) != null) radar.AttachTo(touchHud.GetRect(HudElementId.Minimap));
            else radar.PlaceInCorner(hudRoot, (RadarCorner)s.radarCorner, s.radarSize);
        }

        private void OnLayoutChanged()
        {
            if (hudRoot == null || player == null) return;
            // The HUD layout is stored per orientation, so rebuild when the orientation (or UI scale) changes.
            BuildHud();
        }

        private void BuildInfoPanel()
        {
            var panel = UIKit.NewRounded(hudRoot, "Info", new Color(0.03f, 0.05f, 0.09f, 0.7f));
            UIKit.Anchor(panel.rectTransform, new Vector2(0.5f, 1f), new Vector2(0.5f, 1f), new Vector2(0.5f, 1f), new Vector2(0f, -12f), new Vector2(430f, 112f));

            weaponText = UIKit.NewText(panel.transform, "", 26, UITheme.Text, TextAnchor.MiddleLeft, FontStyle.Bold, "Weapon");
            UIKit.Anchor(weaponText.rectTransform, new Vector2(0f, 1f), new Vector2(1f, 1f), new Vector2(0.5f, 1f), new Vector2(0f, -6f), new Vector2(-28f, 36f));
            weaponText.rectTransform.offsetMin = new Vector2(16f, weaponText.rectTransform.offsetMin.y);
            UIKit.FitText(weaponText, 16);

            ammoText = UIKit.NewText(panel.transform, "", 40, UITheme.Accent, TextAnchor.MiddleLeft, FontStyle.Bold, "Ammo");
            UIKit.Anchor(ammoText.rectTransform, new Vector2(0f, 0f), new Vector2(0.55f, 1f), new Vector2(0f, 0.5f), Vector2.zero, Vector2.zero);
            ammoText.rectTransform.offsetMin = new Vector2(16f, 8f);
            ammoText.rectTransform.offsetMax = new Vector2(0f, -42f);
            UIKit.FitText(ammoText, 20);

            scoreText = UIKit.NewText(panel.transform, "", 24, UITheme.TextDim, TextAnchor.MiddleRight, FontStyle.Bold, "Score");
            UIKit.Anchor(scoreText.rectTransform, new Vector2(0.5f, 0f), new Vector2(1f, 1f), new Vector2(0.5f, 0.5f), Vector2.zero, Vector2.zero);
            scoreText.rectTransform.offsetMin = new Vector2(0f, 8f);
            scoreText.rectTransform.offsetMax = new Vector2(-16f, -42f);
            UIKit.FitText(scoreText, 14);

            var track = UIKit.NewRounded(panel.transform, "ReloadTrack", new Color(1f, 1f, 1f, 0.12f));
            UIKit.Anchor(track.rectTransform, new Vector2(0f, 0f), new Vector2(1f, 0f), new Vector2(0.5f, 0f), new Vector2(0f, 10f), new Vector2(-32f, 8f));
            reloadFill = UIKit.NewRounded(track.transform, "Fill", UITheme.Cyan);
            reloadFill.rectTransform.anchorMin = Vector2.zero;
            reloadFill.rectTransform.anchorMax = new Vector2(0f, 1f);
            reloadFill.rectTransform.offsetMin = reloadFill.rectTransform.offsetMax = Vector2.zero;

            messageText = UIKit.NewText(hudRoot, "", UITheme.FontBody, UITheme.Warning, TextAnchor.MiddleCenter, FontStyle.Bold, "Message");
            UIKit.Anchor(messageText.rectTransform, new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), new Vector2(0f, -150f), new Vector2(700f, 50f));
        }

        private void BuildCrosshair()
        {
            crosshair = new Image[4];
            var root = UIKit.NewRect("Crosshair", hudRoot);
            UIKit.Anchor(root, new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), new Vector2(0f, 60f), new Vector2(60f, 60f));
            Vector2[] offsets = { new Vector2(0f, 14f), new Vector2(0f, -14f), new Vector2(14f, 0f), new Vector2(-14f, 0f) };
            Vector2[] sizes = { new Vector2(3f, 12f), new Vector2(3f, 12f), new Vector2(12f, 3f), new Vector2(12f, 3f) };
            for (int i = 0; i < 4; i++)
            {
                crosshair[i] = UIKit.NewImage(root, "Line" + i, new Color(1f, 1f, 1f, 0.9f));
                UIKit.Anchor(crosshair[i].rectTransform, new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), offsets[i], sizes[i]);
            }
            hitMarker = UIKit.NewImage(root, "Hit", new Color(1f, 0.3f, 0.3f, 0f), UISprites.Circle);
            UIKit.Anchor(hitMarker.rectTransform, new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), Vector2.zero, new Vector2(34f, 34f));
        }

        private void BuildDesktopHints()
        {
            var keys = AppServices.Config.Bindings;
            hintText = UIKit.NewText(hudRoot, string.Format(
                "{0}{1}{2}{3} move   Mouse look   {4} fire   {5} aim   {6} reload   {7}/{8}/{9} weapons   {10} radar   {11} pause",
                Short(keys.Get(GameAction.MoveForward)), Short(keys.Get(GameAction.MoveLeft)), Short(keys.Get(GameAction.MoveBack)), Short(keys.Get(GameAction.MoveRight)),
                Short(keys.Get(GameAction.Fire)), Short(keys.Get(GameAction.Aim)), Short(keys.Get(GameAction.Reload)),
                Short(keys.Get(GameAction.Slot1)), Short(keys.Get(GameAction.Slot2)), Short(keys.Get(GameAction.Slot3)),
                Short(keys.Get(GameAction.Map)), Short(keys.Get(GameAction.Pause))),
                20, UITheme.TextDim, TextAnchor.MiddleLeft, FontStyle.Normal, "Hints");
            UIKit.Anchor(hintText.rectTransform, new Vector2(0f, 0f), new Vector2(1f, 0f), new Vector2(0.5f, 0f), new Vector2(0f, 8f), new Vector2(-32f, 32f));
            hintText.rectTransform.offsetMin = new Vector2(16f, hintText.rectTransform.offsetMin.y);
            UIKit.FitText(hintText, 12);

            string[] names = { "1  RIFLE", "2  PISTOL", "3  KNIFE" };
            var row = UIKit.NewRect("Slots", hudRoot);
            UIKit.Anchor(row, new Vector2(0.5f, 0f), new Vector2(0.5f, 0f), new Vector2(0.5f, 0f), new Vector2(0f, 50f), new Vector2(560f, 64f));
            UIKit.HStack(row.gameObject, 8f).childForceExpandWidth = true;
            for (int i = 0; i < 3; i++)
            {
                var box = UIKit.NewRounded(row, "Slot" + i, new Color(0.03f, 0.05f, 0.09f, 0.7f));
                UIKit.SetLayout(box.gameObject, minWidth: 0f, flexibleWidth: 1f);
                var t = UIKit.NewText(box.transform, names[i].Replace("1", Short(keys.Get(GameAction.Slot1))).Replace("2", Short(keys.Get(GameAction.Slot2))).Replace("3", Short(keys.Get(GameAction.Slot3))), 22, UITheme.Text, TextAnchor.MiddleCenter, FontStyle.Bold);
                UIKit.FitText(t, 14);
                slotBoxes[i] = box;
            }
        }

        private static string Short(KeyCode k)
        {
            string s = k.ToString();
            if (s.StartsWith("Alpha")) return s.Substring(5);
            if (s == "Mouse0") return "LMB";
            if (s == "Mouse1") return "RMB";
            if (s == "LeftShift") return "Shift";
            if (s == "LeftControl") return "Ctrl";
            return s;
        }

        private int FillMarkers(List<RadarMarker> list)
        {
            list.Add(new RadarMarker { Position = ObjectivePosition, Kind = RadarMarkerKind.Objective });
            foreach (var t in targets)
                if (t.IsAlive) list.Add(new RadarMarker { Position = t.transform.position, Kind = RadarMarkerKind.PracticeTarget });
            return list.Count;
        }

        private void ShowMessage(string text)
        {
            if (messageText == null) return;
            messageText.text = text;
            messageTimer = 1.6f;
        }

        // ---------- pause ----------

        private void BuildPauseMenu()
        {
            var overlay = UIKit.NewImage(canvasRoot, "PauseOverlay", new Color(0f, 0f, 0f, 0.7f), null, true);
            UIKit.Stretch(overlay.rectTransform);
            pausePanel = overlay.gameObject;

            var card = UIKit.NewRounded(overlay.transform, "Card", UITheme.Panel, true);
            UIKit.Anchor(card.rectTransform, new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), Vector2.zero, new Vector2(760f, 760f));
            UIKit.VStack(card.gameObject, 12f, 24);

            var title = UIKit.NewText(card.transform, "PAUSED", UITheme.FontTitle, UITheme.Accent, TextAnchor.MiddleCenter, FontStyle.Bold, "Title");
            UIKit.SetLayout(title.gameObject, preferredHeight: 60f);

            var resume = UIKit.NewButton(card.transform, "Resume", "RESUME", UITheme.Accent, () => SetPaused(false), UITheme.FontButton);
            UIKit.SetLayout(resume.gameObject, preferredHeight: 76f);

            SensitivityRow(card.transform, "Sensitivity", () => AppServices.Config.Settings.sensGeneral, v => AppServices.Config.Settings.sensGeneral = v);
            SensitivityRow(card.transform, "Aim sensitivity", () => AppServices.Config.Settings.sensAds, v => AppServices.Config.Settings.sensAds = v);
            SensitivityRow(card.transform, "Touch camera", () => AppServices.Config.Settings.sensTouch, v => AppServices.Config.Settings.sensTouch = v);

            var touchRow = UIKit.NewRounded(card.transform, "TouchRow", UITheme.PanelLight);
            UIKit.SetLayout(touchRow.gameObject, preferredHeight: 72f);
            UIKit.HStack(touchRow.gameObject, 12f, 14, TextAnchor.MiddleLeft);
            var label = UIKit.NewText(touchRow.transform, "Touch controls", UITheme.FontBody, UITheme.Text, TextAnchor.MiddleLeft);
            UIKit.SetLayout(label.gameObject, flexibleWidth: 1f);
            UIForm.NewToggle(touchRow.transform, () => touchEnabled, v => { touchEnabled = v; BuildHud(); ApplyCursor(); });

            var note = UIKit.NewText(card.transform, "Edit the touch layout from the lobby: Settings > Controls > Edit HUD layout.", UITheme.FontCaption, UITheme.TextDim, TextAnchor.MiddleCenter);
            UIKit.SetLayout(note.gameObject, preferredHeight: 56f);
            UIKit.FitText(note, 14);

            var leave = UIKit.NewButton(card.transform, "Leave", "BACK TO LOBBY", UITheme.Button, ReturnToLobby, UITheme.FontButton);
            UIKit.SetLayout(leave.gameObject, preferredHeight: 76f);

            pausePanel.SetActive(false);
        }

        private void SensitivityRow(Transform parent, string label, System.Func<int> get, System.Action<int> set)
        {
            var row = UIKit.NewRounded(parent, "Sens_" + label, UITheme.PanelLight);
            UIKit.SetLayout(row.gameObject, preferredHeight: 72f);
            UIKit.HStack(row.gameObject, 12f, 14, TextAnchor.MiddleLeft);
            var text = UIKit.NewText(row.transform, label, UITheme.FontBody, UITheme.Text, TextAnchor.MiddleLeft);
            UIKit.SetLayout(text.gameObject, flexibleWidth: 1f);
            UIKit.FitText(text, 16);
            UIForm.NewValueSlider(row.transform, SensitivityModel.MinSlider, SensitivityModel.MaxSlider, get(), true,
                v => Mathf.RoundToInt(v).ToString(), v => { set(Mathf.RoundToInt(v)); AppServices.Config.SaveSettings(); });
        }

        private void SetPaused(bool value)
        {
            paused = value;
            pausePanel.SetActive(value);
            player.InputEnabled = !value;
            Time.timeScale = value ? 0f : 1f;
            ApplyCursor();
        }

        private void ApplyCursor()
        {
            bool lockCursor = !paused && !touchEnabled && !Application.isMobilePlatform;
            Cursor.lockState = lockCursor ? CursorLockMode.Locked : CursorLockMode.None;
            Cursor.visible = !lockCursor;
        }

        private void ReturnToLobby()
        {
            Time.timeScale = 1f;
            SceneManager.LoadScene(LobbySceneName);
        }

        // ---------- per frame ----------

        private void Update()
        {
            var keys = AppServices.Config.Bindings;
            if (InputCompat.KeyDown(keys.Get(GameAction.Pause)) || InputCompat.BackPressed) SetPaused(!paused);
            if (!paused && (InputCompat.KeyDown(keys.Get(GameAction.Map)) || (touchHud != null && touchHud.State.ConsumeMap()))) radar.ToggleExpanded();
            if (paused && touchHud != null) touchHud.State.ConsumeMap();

            if (!paused) ApplyCursor();

            var w = player.Weapons;
            var weapon = w.CurrentWeapon;
            weaponText.text = weapon != null ? weapon.DisplayName.ToUpperInvariant() : "UNARMED";
            ammoText.text = weapon == null ? "" : (weapon.MagazineSize > 0 ? w.Magazine(w.Selected) + " / " + w.Reserve(w.Selected) : "MELEE");
            scoreText.text = "TARGETS " + kills + "\nHITS " + player.Hits + "/" + player.Shots;
            reloadFill.rectTransform.anchorMax = new Vector2(w.IsReloading ? w.ReloadProgress : 0f, 1f);

            for (int i = 0; i < 3; i++)
            {
                bool selected = (int)w.Selected == i;
                if (slotBoxes[i] != null) slotBoxes[i].color = selected ? new Color(0.8f, 0.4f, 0.1f, 0.85f) : new Color(0.03f, 0.05f, 0.09f, 0.7f);
            }
            if (touchHud != null)
            {
                touchHud.SetHighlight(HudElementId.Slot1, w.Selected == WeaponSlot.Primary);
                touchHud.SetHighlight(HudElementId.Slot2, w.Selected == WeaponSlot.Secondary);
                touchHud.SetHighlight(HudElementId.Slot3, w.Selected == WeaponSlot.Melee);
                touchHud.SetHighlight(HudElementId.Crouch, touchHud.State.CrouchToggled);
            }

            if (messageTimer > 0f)
            {
                messageTimer -= Time.unscaledDeltaTime;
                if (messageTimer <= 0f) messageText.text = "";
            }
            if (hitMarker != null)
            {
                hitTimer = Mathf.Max(0f, hitTimer - Time.unscaledDeltaTime);
                hitMarker.color = new Color(1f, 0.3f, 0.3f, hitTimer > 0f ? 0.8f : 0f);
            }
        }
    }
}
