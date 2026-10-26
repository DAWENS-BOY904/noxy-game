using System;
using Noxy.Core;
using UnityEngine;
using UnityEngine.UI;

namespace Noxy.Lobby
{
    /// <summary>
    /// Full-screen sign-in gate shown before the lobby. It only hides when AuthService reports a verified session
    /// (SignedIn) or the player explicitly chooses "Play offline". Provider buttons start real Supabase flows.
    /// </summary>
    public sealed class AuthGate : MonoBehaviour
    {
        public static AuthGate Instance { get; private set; }

        private LobbyBackground background;
        private string returnKey = BackgroundLibrary.Lobby;
        private RectTransform content;
        private InputField email, password;
        private Text spinner;
        private string draftEmail = "";

        public static AuthGate Create(Transform canvasRoot, LobbyBackground background)
        {
            var root = UIKit.NewImage(canvasRoot, "AuthGate", new Color(0.02f, 0.03f, 0.06f, 0.62f), null, true);
            UIKit.Stretch(root.rectTransform);
            var gate = root.gameObject.AddComponent<AuthGate>();
            gate.background = background;
            gate.Build(root.rectTransform);
            Instance = gate;
            AppServices.Auth.Changed += gate.Refresh;
            return gate;
        }

        public static void Show()
        {
            if (Instance == null) return;
            Instance.gameObject.SetActive(true);
            Instance.transform.SetAsLastSibling();
            Instance.background.SetKey(BackgroundLibrary.Auth);
            Instance.Refresh();
        }

        private void OnDestroy()
        {
            if (AppServices.Auth != null) AppServices.Auth.Changed -= Refresh;
            if (Instance == this) Instance = null;
        }

        private void Build(RectTransform root)
        {
            var title = UIKit.NewText(root, "NOXY", 84, UITheme.Accent, TextAnchor.MiddleCenter, FontStyle.Bold, "Logo");
            UIKit.Anchor(title.rectTransform, new Vector2(0.5f, 1f), new Vector2(0.5f, 1f), new Vector2(0.5f, 1f), new Vector2(0f, -40f), new Vector2(700f, 110f));
            UIKit.FitText(title, 40);

            var scroll = UIKit.NewScroll(root, "Scroll", out content);
            var rt = (RectTransform)scroll.transform;
            rt.anchorMin = new Vector2(0.5f, 0f);
            rt.anchorMax = new Vector2(0.5f, 1f);
            rt.pivot = new Vector2(0.5f, 0.5f);
            rt.offsetMin = new Vector2(-340f, 24f);
            rt.offsetMax = new Vector2(340f, -160f);
            UIKit.VStack(content.gameObject, 14f, 8, TextAnchor.UpperCenter);
        }

        private void Update()
        {
            if (spinner != null)
            {
                int dots = 1 + (int)(Time.unscaledTime * 3f) % 3;
                spinner.text = "Working" + new string('.', dots);
            }
        }

        private void Refresh()
        {
            var auth = AppServices.Auth;
            if (auth.State == AuthState.SignedIn)
            {
                background.SetKey(returnKey);
                gameObject.SetActive(false);
                return;
            }

            // Avoid rebuilding while the player is typing.
            if (email != null && email.isFocused) draftEmail = email.text;
            Rebuild();
        }

        private void Rebuild()
        {
            var auth = AppServices.Auth;
            if (email != null) draftEmail = email.text;
            UIKit.DestroyChildren(content);
            spinner = null; email = null; password = null;

            switch (auth.State)
            {
                case AuthState.Restoring:
                case AuthState.Authenticating:
                    BuildBusy(auth);
                    return;
                case AuthState.NotConfigured:
                    BuildUnconfigured();
                    return;
                default:
                    BuildForm(auth);
                    return;
            }
        }

        private Text Line(string text, int size, Color color, FontStyle style = FontStyle.Normal, float height = -1f)
        {
            var t = UIKit.NewText(content, text, size, color, TextAnchor.MiddleCenter, style, "Line");
            UIKit.SetLayout(t.gameObject, preferredHeight: height > 0f ? height : Mathf.Max(40f, size * 1.6f));
            return t;
        }

        private ButtonView Btn(string label, Color color, Action click, float height = 76f)
        {
            var b = UIKit.NewButton(content, label, label, color, () => click(), UITheme.FontButton);
            UIKit.SetLayout(b.gameObject, preferredHeight: height, flexibleWidth: 1f);
            return b;
        }

        private void BuildBusy(AuthService auth)
        {
            Line("Signing in", UITheme.FontTitle, UITheme.Text, FontStyle.Bold);
            spinner = Line("Working.", UITheme.FontBody, UITheme.Cyan);
            if (auth.HasPendingOAuth)
            {
                Line("Finish signing in in your browser, then return here.", UITheme.FontCaption, UITheme.TextDim, FontStyle.Normal, 80f);
                Btn("CANCEL", UITheme.Button, () => { OAuthLauncher.Cancel(); auth.CancelOAuth(); });
            }
        }

        private void BuildUnconfigured()
        {
            Line("Accounts unavailable", UITheme.FontTitle, UITheme.Text, FontStyle.Bold);
            Line("This build has no account server configured, so Google, Apple and e-mail sign-in are disabled. " +
                 "Add your Supabase URL and public key to Resources/SupabaseConfig.json (see docs/AUTH_SETUP.md).",
                UITheme.FontCaption, UITheme.Warning, FontStyle.Normal, 150f);
            Btn("PLAY OFFLINE", UITheme.Accent, PlayOffline);
            Line("Offline play uses a local profile with no Player ID and no online features.", 20, UITheme.TextDim, FontStyle.Normal, 70f);
        }

        private void BuildForm(AuthService auth)
        {
            Line("Sign in to play", UITheme.FontTitle, UITheme.Text, FontStyle.Bold);
            Line("One account, one Player ID, on PC, Android and iOS.", UITheme.FontCaption, UITheme.TextDim);

            if (!string.IsNullOrEmpty(auth.Notice)) Line(auth.Notice, UITheme.FontCaption, UITheme.Success, FontStyle.Normal, 70f);
            if (!string.IsNullOrEmpty(auth.LastError) && auth.LastErrorKind != AuthErrorKind.Cancelled)
                Line(auth.LastError, UITheme.FontCaption, UITheme.Danger, FontStyle.Normal, 100f);

            Btn("CONTINUE WITH GOOGLE", Color.white * 0.95f, () => StartProvider("google")).Label.color = new Color(0.1f, 0.1f, 0.12f);
            Btn("CONTINUE WITH APPLE", new Color(0.04f, 0.04f, 0.05f, 1f), () => StartProvider("apple"));

            Line("- or use e-mail -", 20, UITheme.TextDim, FontStyle.Normal, 40f);

            email = UIForm.NewInput(content, "Email", "E-mail", InputField.ContentType.EmailAddress, 120);
            UIKit.SetLayout(email.gameObject, preferredHeight: 68f, flexibleWidth: 1f);
            email.text = draftEmail;
            password = UIForm.NewInput(content, "Password", "Password (8+ characters to register)", InputField.ContentType.Password, 128);
            UIKit.SetLayout(password.gameObject, preferredHeight: 68f, flexibleWidth: 1f);

            var row = UIKit.NewRect("EmailButtons", content);
            UIKit.HStack(row.gameObject, 10f).childForceExpandWidth = true;
            UIKit.SetLayout(row.gameObject, preferredHeight: 72f);
            var signIn = UIKit.NewButton(row, "SignIn", "SIGN IN", UITheme.Accent, () => AppServices.Auth.SignInEmail(email.text, password.text), UITheme.FontBody);
            UIKit.SetLayout(signIn.gameObject, minWidth: 0f, flexibleWidth: 1f);
            var register = UIKit.NewButton(row, "Register", "CREATE ACCOUNT", UITheme.Button, () => AppServices.Auth.SignUpEmail(email.text, password.text), UITheme.FontBody);
            UIKit.SetLayout(register.gameObject, minWidth: 0f, flexibleWidth: 1f);

            Line("- or -", 20, UITheme.TextDim, FontStyle.Normal, 40f);
            Btn("CONTINUE AS GUEST", UITheme.Button, () => AppServices.Auth.SignInGuest(), 68f);
            Line("A guest account can be linked to Google, Apple or e-mail later, keeping the same Player ID.", 20, UITheme.TextDim, FontStyle.Normal, 70f);
            Btn("PLAY OFFLINE", UITheme.Button, PlayOffline, 60f);
        }

        private void StartProvider(string provider)
        {
            var auth = AppServices.Auth;
            var native = AppServices.NativeSignIn;
            if (native != null && native.IsAvailable(provider))
            {
                native.SignIn(provider, (token, nonce, error) =>
                {
                    if (!string.IsNullOrEmpty(error)) AppServices.Notifications.Post("Sign-in failed", error, NotificationSeverity.Error);
                    auth.SignInWithNativeToken(provider, token, nonce);
                });
                return;
            }

            var url = auth.BeginOAuth(provider);
            if (url == null) { Rebuild(); return; }
            OAuthLauncher.Open(url);
        }

        private void PlayOffline()
        {
            AppServices.OfflineMode = true;
            background.SetKey(returnKey);
            gameObject.SetActive(false);
        }
    }
}
