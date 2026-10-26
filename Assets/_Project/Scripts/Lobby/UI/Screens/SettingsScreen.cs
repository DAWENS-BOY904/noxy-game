using System;
using System.Collections.Generic;
using Noxy.Core;
using UnityEngine;
using UnityEngine.UI;

namespace Noxy.Lobby
{
    /// <summary>
    /// Tabbed settings. Everything applies immediately and is saved locally; when signed in the privacy and
    /// gameplay preferences are mirrored to the backend a moment after the last change.
    /// </summary>
    public sealed class SettingsScreen : LobbyScreen
    {
        private const float SyncDelaySeconds = 1.5f;
        private static readonly string[] TabLabels = { "GENERAL", "GRAPHICS", "AUDIO", "GAMEPLAY", "CONTROLS", "PRIVACY", "ACCOUNT" };

        private TabBar tabs;
        private RectTransform content;
        private int tab;
        private float syncTimer = -1f;
        private bool visible;

        // Controls tab state
        private KeyBindings bindingsDraft;
        private GameAction? capturing;
        private float captureStartedAt;
        private Text bindingMessage;

        // Account tab state
        private InputField urlInput, userInput, passInput;
        private InputField guestEmail, guestPass;
        private Text accountMessage;
        private string pendingMessage = "";

        private GameSettings S => AppServices.Config.Settings;

        public override LobbyScreenId Id => LobbyScreenId.Settings;
        public override string Title => "Settings";

        protected override void BuildContent(RectTransform parent)
        {
            tabs = TabBar.Create(parent, TabLabels, SelectTab, 18);
            UIKit.Anchor(tabs.Root, new Vector2(0f, 1f), Vector2.one, new Vector2(0.5f, 1f), Vector2.zero, new Vector2(0f, 56f));

            var scroll = UIKit.NewScroll(parent, "Scroll", out content);
            UIKit.Stretch((RectTransform)scroll.transform, 0f, 0f, 0f, 68f);
            UIKit.VStack(content.gameObject, 10f);

            AppServices.Account.Changed += OnAccountChanged;
            AppServices.Auth.Changed += OnAuthChanged;
        }

        private void OnDestroy()
        {
            if (AppServices.Account != null) AppServices.Account.Changed -= OnAccountChanged;
            if (AppServices.Auth != null) AppServices.Auth.Changed -= OnAuthChanged;
        }

        private void OnAuthChanged()
        {
            if (visible && tab == 6 && !(guestEmail != null && (guestEmail.isFocused || guestPass.isFocused))) BuildTab();
        }

        private void OnAccountChanged()
        {
            if (visible && tab == 6 && !(userInput != null && (userInput.isFocused || passInput.isFocused || urlInput.isFocused))) BuildTab();
        }

        protected override void OnShown()
        {
            visible = true;
            bindingsDraft = null;
            SelectTab(tab);
        }

        protected override void OnHidden()
        {
            visible = false;
            capturing = null;
            FlushSync();
            LobbySettings.Save();
        }

        private void SelectTab(int index)
        {
            tab = index;
            capturing = null;
            tabs.SetSelected(index);
            BuildTab();
        }

        private void BuildTab()
        {
            UIKit.DestroyChildren(content);
            switch (tab)
            {
                case 0: BuildGeneral(); break;
                case 1: BuildGraphics(); break;
                case 2: BuildAudio(); break;
                case 3: BuildGameplay(); break;
                case 4: BuildControls(); break;
                case 5: BuildPrivacy(); break;
                default: BuildAccount(); break;
            }
        }

        // ---------- change tracking ----------

        private void Changed()
        {
            AppServices.Config.SaveSettings();
            syncTimer = SyncDelaySeconds;
        }

        private void FlushSync()
        {
            if (syncTimer < 0f) return;
            syncTimer = -1f;
            if (AppServices.Account.State == AccountState.SignedIn) AppServices.Account.PushSettings();
        }

        private void Update()
        {
            if (syncTimer >= 0f)
            {
                syncTimer -= Time.unscaledDeltaTime;
                if (syncTimer < 0f) { syncTimer = 0f; FlushSync(); }
            }

            if (capturing != null && visible) PollKeyCapture();
        }

        // ---------- row helpers ----------

        private RectTransform NewRow(float height = 76f)
        {
            var card = UIKit.NewRounded(content, "Setting", UITheme.PanelLight);
            UIKit.SetLayout(card.gameObject, preferredHeight: height);
            UIKit.HStack(card.gameObject, 12f, 14, TextAnchor.MiddleLeft);
            return card.rectTransform;
        }

        private static Text RowLabel(RectTransform row, string text)
        {
            var label = UIKit.NewText(row, text, UITheme.FontBody, UITheme.Text, TextAnchor.MiddleLeft);
            UIKit.SetLayout(label.gameObject, minWidth: 120f, flexibleWidth: 1f);
            UIKit.FitText(label, 16);
            return label;
        }

        private void ToggleRow(string label, Func<bool> get, Action<bool> set)
        {
            var row = NewRow();
            RowLabel(row, label);
            UIForm.NewToggle(row, get, v => { set(v); Changed(); });
        }

        private void SliderRow(string label, float min, float max, float value, bool whole, Func<float, string> format, Action<float> set)
        {
            var row = NewRow();
            RowLabel(row, label);
            UIForm.NewValueSlider(row, min, max, value, whole, format, v => { set(v); Changed(); });
        }

        /// <summary>Button that cycles through options and shows the current one.</summary>
        private void ChoiceRow(string label, IList<string> options, Func<int> get, Action<int> set)
        {
            var row = NewRow();
            RowLabel(row, label);
            ButtonView button = null;
            button = UIKit.NewButton(row, "Choice", options[Mathf.Clamp(get(), 0, options.Count - 1)], UITheme.Button, () =>
            {
                int next = (get() + 1) % options.Count;
                set(next);
                button.SetLabel(options[next]);
                Changed();
            }, UITheme.FontBody);
            UIKit.SetLayout(button.gameObject, preferredWidth: 260f, preferredHeight: 56f, flexibleWidth: 0f);
        }

        private Text Note(string text, Color? color = null) => UIWidgets.Paragraph(content, text, color ?? UITheme.TextDim, 20);

        private ButtonView ActionButton(string label, Color color, Action click, float height = 68f)
        {
            var b = UIKit.NewButton(content, "Action_" + label, label, color, () => click(), UITheme.FontBody);
            UIKit.SetLayout(b.gameObject, preferredHeight: height);
            return b;
        }

        // ---------- tabs ----------

        private void BuildGeneral()
        {
            UIWidgets.SectionTitle(content, "General");
            var lang = NewRow();
            RowLabel(lang, "Language");
            var l = UIKit.NewText(lang, "English", UITheme.FontBody, UITheme.TextDim, TextAnchor.MiddleRight, FontStyle.Bold);
            UIKit.SetLayout(l.gameObject, preferredWidth: 260f, flexibleWidth: 0f);
            Note("Only English is available; localization isn't set up yet.");

            SliderRow("UI scale", 0.8f, 1.3f, S.uiScale, false, v => Mathf.RoundToInt(v * 100f) + "%", v => S.uiScale = v);
            ToggleRow("Show crosshair", () => S.showCrosshair, v => S.showCrosshair = v);
            ToggleRow("In-game notifications", () => S.notifications, v => S.notifications = v);
            ToggleRow("Confirm important actions", () => S.confirmDialogs, v => S.confirmDialogs = v);
            Note("Destructive actions (deleting an account, blocking, leaving a party) always ask for confirmation.");

            UIWidgets.SectionTitle(content, "Lobby");
            ToggleRow("Rotate character automatically", () => LobbySettings.AutoRotateCharacter, v => LobbySettings.AutoRotateCharacter = v);

            UIWidgets.SectionTitle(content, "Developer");
            ToggleRow("Simulate matchmaking error", () => LobbySettings.SimulateMatchmakingError, v => LobbySettings.SimulateMatchmakingError = v);
            Note("When ON, pressing PLAY for Training fails on purpose so the error and retry UI can be tested.");

            UIWidgets.SectionTitle(content, "Reset");
            ActionButton("RESET SETTINGS TO DEFAULTS", UITheme.Danger, () => Ctx.Confirm("Reset settings",
                "Restore every setting on this screen to its default? Key bindings and HUD layouts are not affected.", "RESET", () =>
                {
                    AppServices.Config.ResetSettings();
                    syncTimer = SyncDelaySeconds;
                    BuildTab();
                }, true));
        }

        private void BuildGraphics()
        {
            bool mobile = DisplayApplier.IsMobile;
            UIWidgets.SectionTitle(content, "Quality");

            var presets = mobile ? new List<string> { "LOW", "MEDIUM", "HIGH" } : new List<string> { "LOW", "MEDIUM", "HIGH", "ULTRA" };
            ChoiceRow(mobile ? "Mobile preset" : "Graphics preset", presets, () => Mathf.Clamp(S.graphicsPreset, 0, presets.Count - 1), v => S.graphicsPreset = v);
            ChoiceRow("Texture quality", new[] { "FULL", "HALF", "QUARTER" }, () => S.textureQuality, v => S.textureQuality = v);
            ChoiceRow("Shadows", new[] { "OFF", "LOW", "HIGH" }, () => S.shadowQuality, v => S.shadowQuality = v);
            ToggleRow("Post-processing effects", () => S.postProcessing, v => S.postProcessing = v);
            Note("No post-processing stack is installed yet, so this switch has no visible effect today. Shadow, texture and anti-aliasing settings do apply (Built-in Render Pipeline).");

            UIWidgets.SectionTitle(content, "Frame rate");
            var options = FramePolicy.Options(mobile, DisplayApplier.DisplayRefreshRate);
            var labels = new List<string>();
            foreach (int o in options) labels.Add(o == 0 ? "UNLIMITED" : o + " FPS");
            ChoiceRow("FPS limit", labels, () => Mathf.Max(0, Array.IndexOf(options, S.fpsLimit)), v => S.fpsLimit = options[v]);
            if (!mobile) ToggleRow("VSync", () => S.vSync, v => S.vSync = v);

            var r = FramePolicy.Resolve(S.fpsLimit, S.vSync, mobile);
            string cap = r.TargetFrameRate <= 0 ? "no cap (display/VSync decides)" : r.TargetFrameRate + " FPS maximum";
            Note("Current: " + DisplayApplier.CurrentPreset(S).ToString().ToUpperInvariant() + " preset, " + cap + "." +
                 (r.CapIgnoredByVSync ? "\n<color=#FFC742>VSync is on, so it overrides the FPS limit.</color>" : "") +
                 "\nThe limit is a maximum target, not a guarantee: real FPS depends on your device, display and the scene." +
                 (mobile ? "\nLower limits save battery and reduce heat." : ""));

            if (!mobile)
            {
                UIWidgets.SectionTitle(content, "Display");
                ChoiceRow("Window mode", new[] { "FULLSCREEN", "BORDERLESS", "WINDOWED" }, () => S.windowMode, v => S.windowMode = v);

                var resolutions = new List<Vector2Int> { Vector2Int.zero };
                foreach (var res in Screen.resolutions)
                {
                    var v = new Vector2Int(res.width, res.height);
                    if (!resolutions.Contains(v)) resolutions.Add(v);
                }
                var names = new List<string>();
                foreach (var v in resolutions) names.Add(v == Vector2Int.zero ? "NATIVE" : v.x + " x " + v.y);
                ChoiceRow("Resolution", names, () => Mathf.Max(0, resolutions.FindIndex(v => v.x == S.resolutionWidth && v.y == S.resolutionHeight)),
                    i => { S.resolutionWidth = resolutions[i].x; S.resolutionHeight = resolutions[i].y; });
            }
        }

        private void BuildAudio()
        {
            UIWidgets.SectionTitle(content, "Volume");
            SliderRow("Master volume", 0f, 1f, S.masterVolume, false, v => Mathf.RoundToInt(v * 100f) + "%", v => S.masterVolume = v);
            SliderRow("Music", 0f, 1f, S.musicVolume, false, v => Mathf.RoundToInt(v * 100f) + "%", v => S.musicVolume = v);
            SliderRow("Sound effects", 0f, 1f, S.sfxVolume, false, v => Mathf.RoundToInt(v * 100f) + "%", v => S.sfxVolume = v);
            SliderRow("Voice chat", 0f, 1f, S.voiceVolume, false, v => Mathf.RoundToInt(v * 100f) + "%", v => S.voiceVolume = v);
            UIWidgets.SectionTitle(content, "Mute");
            ToggleRow("Mute music", () => S.musicMuted, v => S.musicMuted = v);
            ToggleRow("Mute sound effects", () => S.sfxMuted, v => S.sfxMuted = v);
            ToggleRow("Mute voice chat", () => S.voiceMuted, v => S.voiceMuted = v);
            Note("UI feedback uses original generated tones and follows Master / Sound effects. Lobby music is ready at Resources/Audio/LobbyMusic; no music clip is included. Voice chat audio is not implemented.");
        }

        private void BuildGameplay()
        {
            UIWidgets.SectionTitle(content, "Radar / minimap");
            ChoiceRow("Orientation", new[] { "ROTATES WITH PLAYER", "NORTH UP" }, () => S.radarNorthUp ? 1 : 0, v => S.radarNorthUp = v == 1);
            SliderRow("Size", 0.7f, 1.6f, S.radarSize, false, v => Mathf.RoundToInt(v * 100f) + "%", v => S.radarSize = v);
            ChoiceRow("Screen corner", new[] { "TOP LEFT", "TOP RIGHT", "BOTTOM LEFT", "BOTTOM RIGHT" }, () => S.radarCorner, v => S.radarCorner = v);
            Note("The radar is used in the Training range. In online modes it will only show what the server allows (teammates, objectives), never hidden enemies.");

            UIWidgets.SectionTitle(content, "Feedback");
            ToggleRow("Damage indicators", () => S.damageIndicators, v => S.damageIndicators = v);
            ToggleRow("Invert vertical look", () => S.invertY, v => S.invertY = v);
        }

        private void BuildControls()
        {
            UIWidgets.SectionTitle(content, "Sensitivity");
            Func<float, string> n = v => Mathf.RoundToInt(v).ToString();
            SliderRow("General (hip-fire)", SensitivityModel.MinSlider, SensitivityModel.MaxSlider, S.sensGeneral, true, n, v => S.sensGeneral = Mathf.RoundToInt(v));
            SliderRow("Aim down sights", SensitivityModel.MinSlider, SensitivityModel.MaxSlider, S.sensAds, true, n, v => S.sensAds = Mathf.RoundToInt(v));
            SliderRow("Touch camera", SensitivityModel.MinSlider, SensitivityModel.MaxSlider, S.sensTouch, true, n, v => S.sensTouch = Mathf.RoundToInt(v));
            SliderRow("Horizontal", 20, 200, S.sensHorizontal, true, v => Mathf.RoundToInt(v) + "%", v => S.sensHorizontal = Mathf.RoundToInt(v));
            SliderRow("Vertical", 20, 200, S.sensVertical, true, v => Mathf.RoundToInt(v) + "%", v => S.sensVertical = Mathf.RoundToInt(v));
            Note("These sliders drive the camera in the Training range and in Battle Royale. Changes apply immediately and are saved to your account. Controller sensitivity isn't offered because controller support doesn't exist yet.");

            UIWidgets.SectionTitle(content, "Look feel");
            Func<float, string> pct = v => Mathf.RoundToInt(v) + "%";
            if (!DisplayApplier.IsMobile) SliderRow("Mouse smoothing", 0, 100, S.mouseSmoothing, true, pct, v => S.mouseSmoothing = Mathf.RoundToInt(v));
            if (DisplayApplier.IsMobile)
            {
                SliderRow("Touch smoothing", 0, 100, S.touchSmoothing, true, pct, v => S.touchSmoothing = Mathf.RoundToInt(v));
                SliderRow("Touch aim acceleration", 0, 100, S.touchAcceleration, true, pct, v => S.touchAcceleration = Mathf.RoundToInt(v));
                if (InputCompat.GyroSupported)
                {
                    ToggleRow("Gyroscope aiming", () => S.gyroEnabled, v => S.gyroEnabled = v);
                    SliderRow("Gyroscope sensitivity", SensitivityModel.MinSlider, SensitivityModel.MaxSlider, S.sensGyro, true, n, v => S.sensGyro = Mathf.RoundToInt(v));
                }
                else Note("This device has no gyroscope, so gyro aiming is not offered.");
            }
            Note("Smoothing and acceleration are time-based, so they feel the same at any frame rate. Gyro direction has not been verified on a physical device yet.");

            UIWidgets.SectionTitle(content, "On-screen controls");
            ActionButton("EDIT HUD LAYOUT", UITheme.Accent, () => Ctx.Screens.Open(LobbyScreenId.HudEditor));
            Note("Move, resize and fade the touch controls for the current orientation. Used on phones and tablets.");

            if (!DisplayApplier.IsMobile) BuildKeyBindings();
        }

        private void BuildKeyBindings()
        {
            if (bindingsDraft == null) bindingsDraft = AppServices.Config.Bindings.Clone();

            UIWidgets.SectionTitle(content, "Keyboard & mouse");
            bindingMessage = Note("");
            foreach (GameAction action in Enum.GetValues(typeof(GameAction)))
            {
                var captured = action;
                var row = NewRow(68f);
                RowLabel(row, PrettyAction(action));
                KeyCode key = bindingsDraft.Get(action);
                bool conflict = bindingsDraft.ConflictsFor(action, key).Count > 0;
                bool waiting = capturing == action;
                var b = UIKit.NewButton(row, "Bind_" + action, waiting ? "PRESS A KEY..." : key.ToString().ToUpperInvariant(),
                    waiting ? UITheme.AccentDark : (conflict ? UITheme.Danger : UITheme.Button), () => StartCapture(captured), UITheme.FontCaption + 2);
                UIKit.SetLayout(b.gameObject, preferredWidth: 260f, preferredHeight: 52f, flexibleWidth: 0f);
            }

            var buttons = UIKit.NewRect("BindButtons", content);
            UIKit.HStack(buttons.gameObject, 8f).childForceExpandWidth = true;
            UIKit.SetLayout(buttons.gameObject, preferredHeight: 68f);
            var defaults = UIKit.NewButton(buttons, "RestoreKeys", "RESTORE DEFAULTS", UITheme.Button, () =>
            {
                bindingsDraft.RestoreDefaults();
                capturing = null;
                BuildTab();
            }, UITheme.FontBody);
            UIKit.SetLayout(defaults.gameObject, minWidth: 0f, flexibleWidth: 1f);
            var save = UIKit.NewButton(buttons, "SaveKeys", "SAVE BINDINGS", UITheme.Accent, SaveBindings, UITheme.FontBody);
            UIKit.SetLayout(save.gameObject, minWidth: 0f, flexibleWidth: 1f);

            RefreshBindingMessage();
        }

        private static string PrettyAction(GameAction a)
        {
            switch (a)
            {
                case GameAction.MoveForward: return "Move forward";
                case GameAction.MoveBack: return "Move backward";
                case GameAction.MoveLeft: return "Move left";
                case GameAction.MoveRight: return "Move right";
                case GameAction.Slot1: return "Weapon slot 1 (primary)";
                case GameAction.Slot2: return "Weapon slot 2 (secondary)";
                case GameAction.Slot3: return "Weapon slot 3 (melee)";
                case GameAction.Map: return "Expand radar";
                case GameAction.Inventory: return "Inventory (not used yet)";
                case GameAction.Interact: return "Interact (not used yet)";
                default: return a.ToString();
            }
        }

        private void StartCapture(GameAction action)
        {
            capturing = action;
            captureStartedAt = Time.unscaledTime;
            BuildTab();
        }

        private void PollKeyCapture()
        {
            if (Time.unscaledTime - captureStartedAt < 0.15f) return; // ignore the click that started capture
            if (!InputCompat.TryGetPressedKey(out var key)) return;

            var action = capturing.Value;
            capturing = null;
            if (key != KeyCode.Escape) bindingsDraft.Set(action, key);
            BuildTab();
        }

        private void RefreshBindingMessage()
        {
            if (bindingMessage == null) return;
            var conflicts = bindingsDraft.FindAllConflicts();
            if (conflicts.Count == 0)
            {
                bindingMessage.text = "Click a binding, then press the new key. Esc cancels.";
                bindingMessage.color = UITheme.TextDim;
                return;
            }
            var c = conflicts[0];
            bindingMessage.text = $"<color=#F55257>Conflict:</color> {PrettyAction(c.Key)} and {PrettyAction(c.Value)} both use {bindingsDraft.Get(c.Key).ToString().ToUpperInvariant()}. Change one of them before saving.";
        }

        private void SaveBindings()
        {
            if (bindingsDraft.FindAllConflicts().Count > 0)
            {
                RefreshBindingMessage();
                AppServices.Notifications.Post("Key bindings", "Resolve the highlighted conflicts first.", NotificationSeverity.Warning);
                return;
            }
            AppServices.Config.Bindings.entries = bindingsDraft.Clone().entries;
            AppServices.Config.SaveBindings();
            AppServices.Notifications.Post("Key bindings", "Saved.", NotificationSeverity.Success);
        }

        private void BuildPrivacy()
        {
            bool online = AppServices.Account.State == AccountState.SignedIn;
            UIWidgets.SectionTitle(content, "Privacy & social");
            ChoiceRow("Friend requests from", new[] { "EVERYONE", "NOBODY" }, () => S.friendRequests == (int)Visibility.Nobody ? 1 : 0, v => S.friendRequests = v == 1 ? (int)Visibility.Nobody : (int)Visibility.Everyone);
            ChoiceRow("Party invites from", new[] { "FRIENDS", "NOBODY" }, () => S.partyInvites == (int)Visibility.Nobody ? 1 : 0, v => S.partyInvites = v == 1 ? (int)Visibility.Nobody : (int)Visibility.Friends);
            ChoiceRow("Online status visible to", new[] { "EVERYONE", "FRIENDS", "NOBODY" }, () => S.onlineStatus, v => S.onlineStatus = v);
            ToggleRow("Notifications", () => S.notifications, v => S.notifications = v);
            Note(online ? "These choices are enforced by the server." : "Saved on this device. They are enforced by the server only while you are signed in.");

            UIWidgets.SectionTitle(content, "Blocked players");
            if (!online) { Note("Sign in to manage blocked players."); return; }

            var blocked = AppServices.Social.Snapshot.Blocked;
            if (blocked.Count == 0) Note("You haven't blocked anyone.");
            foreach (var b in blocked)
            {
                var player = b;
                var row = SocialUi.Row(content, player.username, "Blocked", UITheme.Text,
                    new RowButton("UNBLOCK", UITheme.Button, () => AppServices.Social.Unblock(player.publicId, r =>
                    {
                        SocialUi.Report(r, "Player unblocked.");
                        if (visible && tab == 5) BuildTab();
                    })));
            }
        }

        private void BuildAccount()
        {
            BuildGameAccount();
            var acct = AppServices.Account;
            if (acct.State == AccountState.SignedIn) { BuildSignedIn(acct); return; }
            if (AppServices.Auth.State != AuthState.NotConfigured) { BuildGameServer(acct); return; }

            UIWidgets.SectionTitle(content, "Local development server");
            Note("You are using the offline profile on this device. Sign in to get a permanent Player ID, friends, parties and synced settings. " +
                 "Online features need the development server (see server/README).");

            var urlRow = NewRow(76f);
            RowLabel(urlRow, "Server");
            urlInput = UIForm.NewInput(urlRow, "Url", "http://192.168.1.20:8787", InputField.ContentType.Standard, 120);
            urlInput.text = acct.ServerUrl;
            UIKit.SetLayout(urlInput.gameObject, preferredWidth: 420f, preferredHeight: 56f, flexibleWidth: 0f);
            urlInput.onEndEdit.AddListener(v => acct.ServerUrl = v);

            var userRow = NewRow(76f);
            RowLabel(userRow, "Username");
            userInput = UIForm.NewInput(userRow, "User", "3-16 letters, numbers, _", InputField.ContentType.Standard, 16);
            UIKit.SetLayout(userInput.gameObject, preferredWidth: 420f, preferredHeight: 56f, flexibleWidth: 0f);

            var passRow = NewRow(76f);
            RowLabel(passRow, "Password");
            passInput = UIForm.NewInput(passRow, "Pass", "At least 8 characters", InputField.ContentType.Password, 128);
            UIKit.SetLayout(passInput.gameObject, preferredWidth: 420f, preferredHeight: 56f, flexibleWidth: 0f);

            var buttons = UIKit.NewRect("AuthButtons", content);
            UIKit.HStack(buttons.gameObject, 8f).childForceExpandWidth = true;
            UIKit.SetLayout(buttons.gameObject, preferredHeight: 72f);
            bool busy = acct.State == AccountState.Working;
            var signIn = UIKit.NewButton(buttons, "SignIn", busy ? "PLEASE WAIT..." : "SIGN IN", UITheme.Accent, () => Authenticate(false), UITheme.FontBody);
            UIKit.SetLayout(signIn.gameObject, minWidth: 0f, flexibleWidth: 1f);
            var register = UIKit.NewButton(buttons, "Register", "CREATE ACCOUNT", UITheme.Button, () => Authenticate(true), UITheme.FontBody);
            UIKit.SetLayout(register.gameObject, minWidth: 0f, flexibleWidth: 1f);
            signIn.SetInteractable(!busy);
            register.SetInteractable(!busy);

            accountMessage = Note(string.IsNullOrEmpty(pendingMessage) ? acct.LastError : pendingMessage, UITheme.Warning);
            Note("Your password is sent to the server over the address above. Use https:// for anything that isn't your own computer or home network.");
        }

        private static string ProviderName(string id) =>
            id == "google" ? "Google" : id == "apple" ? "Apple" : id == "email" ? "E-mail" : id;

        /// <summary>Supabase account: Player ID, linked sign-in methods, guest upgrade and sign-out.</summary>
        private void BuildGameAccount()
        {
            var auth = AppServices.Auth;
            UIWidgets.SectionTitle(content, "Game account");

            if (auth.State != AuthState.SignedIn || auth.Session == null || auth.Player == null)
            {
                Note(auth.State == AuthState.NotConfigured
                    ? "Online accounts are not configured in this build, so you are playing offline."
                    : "You are playing offline. Sign in to keep one Player ID and your progress on every device.", UITheme.Warning);
                if (auth.State != AuthState.NotConfigured)
                    ActionButton("SIGN IN / CREATE ACCOUNT", UITheme.Accent, () => AuthGate.Show());
                return;
            }

            var s = auth.Session;
            UIWidgets.KeyValueRow(content, "Username", auth.Player.Username, UITheme.Text);
            UIWidgets.KeyValueRow(content, "Player ID", auth.Player.PlayerId, UITheme.Cyan);
            UIWidgets.KeyValueRow(content, "Sign-in", s.IsAnonymous ? "Guest (not linked yet)" : string.Join(", ", s.Providers.ConvertAll(ProviderName)), UITheme.Text);
            if (!string.IsNullOrEmpty(s.Email)) UIWidgets.KeyValueRow(content, "E-mail", s.Email, UITheme.TextDim);

            ActionButton("COPY PLAYER ID", UITheme.Button, () =>
            {
                GUIUtility.systemCopyBuffer = auth.Player.PlayerId;
                AppServices.Notifications.Post("Copied", "Player ID copied.", NotificationSeverity.Success);
            });

            if (s.IsAnonymous)
                Note("Guest progress lives only in this sign-in. Link Google, Apple or an e-mail now so you can recover it on another device. Linking keeps your Player ID.", UITheme.Warning);

            if (auth.HasPendingOAuth)
            {
                Note("Waiting for the browser to finish... Return to the game when you are done.", UITheme.Cyan);
                ActionButton("CANCEL LINKING", UITheme.Button, () => { OAuthLauncher.Cancel(); auth.CancelOAuth(); });
            }
            else
            {
                foreach (var provider in new[] { "google", "apple" })
                {
                    if (s.Providers.Contains(provider)) continue;
                    var id = provider;
                    ActionButton("LINK " + ProviderName(id).ToUpperInvariant(), UITheme.Button, () => Ctx.Confirm(
                        "Link " + ProviderName(id),
                        "You will be sent to " + ProviderName(id) + " to sign in. That sign-in is added to THIS account (Player ID " + auth.Player.PlayerId +
                        "). Accounts are never merged by e-mail address. If that " + ProviderName(id) + " account already has its own Noxy account, linking is refused " +
                        "and nothing changes: sign out and sign in with it directly instead.",
                        "CONTINUE", () => StartLink(id)));
                }
            }

            if (s.IsAnonymous)
            {
                var emailRow = NewRow(76f);
                RowLabel(emailRow, "E-mail");
                guestEmail = UIForm.NewInput(emailRow, "GuestEmail", "you@example.com", InputField.ContentType.EmailAddress, 120);
                UIKit.SetLayout(guestEmail.gameObject, preferredWidth: 420f, preferredHeight: 56f, flexibleWidth: 0f);
                var passRow2 = NewRow(76f);
                RowLabel(passRow2, "Password");
                guestPass = UIForm.NewInput(passRow2, "GuestPass", "At least 8 characters", InputField.ContentType.Password, 128);
                UIKit.SetLayout(guestPass.gameObject, preferredWidth: 420f, preferredHeight: 56f, flexibleWidth: 0f);
                ActionButton("UPGRADE GUEST WITH E-MAIL", UITheme.Button, () => auth.UpgradeGuestWithEmail(guestEmail.text, guestPass.text, r =>
                {
                    AppServices.Notifications.Post(r.Ok ? "Account upgraded" : "Couldn't upgrade", r.Ok ? "Your guest account now has an e-mail sign-in." : r.Message,
                        r.Ok ? NotificationSeverity.Success : NotificationSeverity.Error);
                    if (visible) BuildTab();
                }));
            }

            if (!string.IsNullOrEmpty(auth.Notice)) Note(auth.Notice, UITheme.Success);
            if (!string.IsNullOrEmpty(auth.LastError) && auth.LastErrorKind != AuthErrorKind.Cancelled) Note(auth.LastError, UITheme.Warning);

            ActionButton("SIGN OUT", UITheme.Button, () => Ctx.Confirm("Sign out",
                "Sign out of your game account on this device? Your progress stays on your account.", "SIGN OUT", () =>
                {
                    OAuthLauncher.Cancel();
                    auth.SignOut();
                    AuthGate.Show();
                }));
        }

        private void StartLink(string provider)
        {
            AppServices.Auth.BeginLink(provider, r =>
            {
                if (r.Ok) OAuthLauncher.Open(r.Url);
                else AppServices.Notifications.Post("Couldn't link " + ProviderName(provider), r.Message, NotificationSeverity.Error);
                if (visible) BuildTab();
            });
        }

        private void Authenticate(bool register)
        {
            string url = urlInput.text.Trim();
            if (url != AppServices.Account.ServerUrl) AppServices.Account.ServerUrl = url;
            string user = userInput.text.Trim();
            string pass = passInput.text;
            pendingMessage = "";

            void Done(ServiceResult r)
            {
                pendingMessage = r.Success ? "" : r.Error;
                if (visible && tab == 6) BuildTab();
            }
            if (register) AppServices.Account.Register(user, pass, Done);
            else AppServices.Account.SignIn(user, pass, Done);
            BuildTab();
        }

        /// <summary>Supabase is the only sign-in. This shows whether the game server accepted it, and why not if it didn't.</summary>
        private void BuildGameServer(AccountService acct)
        {
            UIWidgets.SectionTitle(content, "Game server");
            var urlRow = NewRow(76f);
            RowLabel(urlRow, "Server");
            urlInput = UIForm.NewInput(urlRow, "Url", "https://your-game-server", InputField.ContentType.Standard, 120);
            urlInput.text = acct.ServerUrl;
            UIKit.SetLayout(urlInput.gameObject, preferredWidth: 420f, preferredHeight: 56f, flexibleWidth: 0f);
            urlInput.onEndEdit.AddListener(v => acct.ServerUrl = v);

            var auth = AppServices.Auth;
            if (auth.State != AuthState.SignedIn)
            {
                Note("Sign in to your game account above to use friends, the store, clans and other online features.", UITheme.Warning);
                return;
            }
            bool working = acct.State == AccountState.Working;
            Note(working ? "Connecting to the game server..." : acct.UnavailableReason, working ? UITheme.TextDim : UITheme.Warning);
            ActionButton(working ? "PLEASE WAIT..." : "RETRY CONNECTION", UITheme.Accent, () =>
            {
                if (acct.State == AccountState.Working) return;
                acct.SignInWithSupabase(auth.ValidAccessToken, _ => { if (visible && tab == 6) BuildTab(); });
                BuildTab();
            });
        }

        private void BuildSignedIn(AccountService acct)
        {
            var p = AppServices.Profile.Profile;
            UIWidgets.SectionTitle(content, "Account");
            UIWidgets.KeyValueRow(content, "Username", p.username, UITheme.Text);
            UIWidgets.KeyValueRow(content, "Player ID", p.publicId, UITheme.Cyan);
            UIWidgets.KeyValueRow(content, "Server", acct.ServerUrl, UITheme.TextDim);
            UIWidgets.KeyValueRow(content, "Connection", acct.ServerReachable ? "Connected" : "Server unreachable", acct.ServerReachable ? UITheme.Success : UITheme.Warning);

            ActionButton("COPY PLAYER ID", UITheme.Button, () =>
            {
                GUIUtility.systemCopyBuffer = p.publicId;
                AppServices.Notifications.Post("Copied", "Player ID copied.", NotificationSeverity.Success);
            });
            if (acct.SupabaseBound)
            {
                Note("Your game account is managed above (Game account). Sign out there. Deleting an account is not available in the game yet; ask the server operator.", UITheme.TextDim);
                return;
            }
            ActionButton("SIGN OUT", UITheme.Button, () => Ctx.Confirm("Sign out", "Sign out and return to the offline profile on this device?", "SIGN OUT", () =>
            {
                FlushSync();
                AppServices.Account.SignOut(_ => { if (visible) BuildTab(); });
            }));

            UIWidgets.SectionTitle(content, "Danger zone");
            ActionButton("DELETE ACCOUNT", UITheme.Danger, () => Ctx.Dialog.AskPassword("Delete account",
                "This permanently deletes your account, Player ID, friends and inventory from the server. It cannot be undone. Enter your password to confirm.",
                "DELETE", password => AppServices.Account.DeleteAccount(password, r =>
                {
                    if (!r.Success) AppServices.Notifications.Post("Couldn't delete account", r.Error, NotificationSeverity.Error);
                    if (visible) BuildTab();
                })));
            Note("Passwords and session tokens are never displayed. Your session token is stored on this device only.");
        }
    }
}
