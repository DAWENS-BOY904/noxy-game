using System;
using System.Collections.Concurrent;
using System.Text;
using Noxy.Core;
using UnityEngine;
#if UNITY_STANDALONE || UNITY_EDITOR
using System.Net;
using System.Threading;
#endif

namespace Noxy.Lobby
{
    /// <summary>
    /// Session storage in PlayerPrefs. DEVELOPMENT ONLY: PlayerPrefs is plain text. Before release replace this with the
    /// iOS Keychain / Android Keystore / Windows Credential Manager behind the same ISessionStore interface.
    /// </summary>
    public sealed class PrefsSessionStore : ISessionStore
    {
        private const string Key = "noxy.auth.session";
        public string Load() => PlayerPrefs.GetString(Key, "");
        public void Save(string json) { PlayerPrefs.SetString(Key, json ?? ""); PlayerPrefs.Save(); }
        public void Clear() { PlayerPrefs.DeleteKey(Key); PlayerPrefs.Save(); }
    }

    /// <summary>
    /// Hook for a native provider SDK (e.g. Sign in with Apple on iOS, Google Credential Manager on Android).
    /// No implementation ships with the project: register one at startup if you add a plugin. Until then the
    /// browser (PKCE) flow is used on every platform.
    /// </summary>
    public interface INativeSignIn
    {
        bool IsAvailable(string provider);
        /// <summary>Calls back with (idToken, rawNonce, error). An empty idToken means the user cancelled.</summary>
        void SignIn(string provider, Action<string, string, string> done);
    }

    /// <summary>Loads the public Supabase settings (project URL + anon key). Never put a service-role key here.</summary>
    public static class SupabaseConfigLoader
    {
        public const string ResourceName = "SupabaseConfig";
        public const int DesktopLoopbackPort = 53682;

        public static SupabaseConfig Load()
        {
            var asset = Resources.Load<TextAsset>(ResourceName);
            var cfg = SupabaseConfig.FromJson(asset != null ? asset.text : "");
#if UNITY_STANDALONE || UNITY_EDITOR
            cfg.RedirectUrl = "http://127.0.0.1:" + DesktopLoopbackPort + "/auth-callback";
#else
            cfg.RedirectUrl = "noxy://auth-callback";
#endif
            return cfg;
        }
    }

    /// <summary>
    /// Opens the system browser for Google/Apple and receives the redirect: a custom-scheme deep link on Android/iOS,
    /// a one-shot loopback listener (127.0.0.1) on Windows/macOS/Linux and in the Editor.
    /// </summary>
    public static class OAuthLauncher
    {
        private static readonly ConcurrentQueue<string> Incoming = new ConcurrentQueue<string>();
        private static bool hooked;

        public static void Initialize()
        {
            if (hooked) return;
            hooked = true;
            Application.deepLinkActivated += url => Incoming.Enqueue(url);
            if (!string.IsNullOrEmpty(Application.absoluteURL)) Incoming.Enqueue(Application.absoluteURL);
        }

        public static void Open(string url)
        {
            Initialize();
            StartLoopback();
            Application.OpenURL(url);
        }

        public static void Cancel()
        {
            StopLoopback();
            while (Incoming.TryDequeue(out _)) { }
        }

        /// <summary>Call every frame from the main thread.</summary>
        public static void Pump(AuthService auth)
        {
            if (auth == null) return;
            while (Incoming.TryDequeue(out var url))
            {
                if (!auth.HasPendingOAuth) continue; // stale or unsolicited
                StopLoopback();
                auth.HandleCallback(url);
            }
            if (auth.IsPendingExpired)
            {
                StopLoopback();
                auth.CancelOAuth();
            }
        }

#if UNITY_STANDALONE || UNITY_EDITOR
        private static HttpListener listener;
        private static Thread thread;

        private static void StartLoopback()
        {
            StopLoopback();
            try
            {
                listener = new HttpListener();
                listener.Prefixes.Add("http://127.0.0.1:" + SupabaseConfigLoader.DesktopLoopbackPort + "/");
                listener.Start();
                var l = listener;
                thread = new Thread(() =>
                {
                    try
                    {
                        while (l.IsListening)
                        {
                            var ctx = l.GetContext();
                            var path = ctx.Request.Url.AbsolutePath;
                            if (path == "/auth-callback") Incoming.Enqueue("http://127.0.0.1:" + SupabaseConfigLoader.DesktopLoopbackPort + ctx.Request.Url.PathAndQuery);
                            var html = Encoding.UTF8.GetBytes("<html><body style='font-family:sans-serif;background:#0b1220;color:#eee;text-align:center;padding-top:20vh'><h2>You can close this tab and return to the game.</h2></body></html>");
                            ctx.Response.ContentType = "text/html";
                            ctx.Response.OutputStream.Write(html, 0, html.Length);
                            ctx.Response.Close();
                        }
                    }
                    catch (Exception) { /* listener stopped */ }
                }) { IsBackground = true };
                thread.Start();
            }
            catch (Exception e)
            {
                Debug.LogWarning("[Auth] Could not start the loopback listener: " + e.Message);
            }
        }

        private static void StopLoopback()
        {
            try { listener?.Stop(); listener?.Close(); } catch (Exception) { }
            listener = null; thread = null;
        }
#else
        private static void StartLoopback() { }
        private static void StopLoopback() { }
#endif
    }

    /// <summary>
    /// Mirrors a verified Supabase profile into the lobby's profile object. The client only displays these values;
    /// level/xp/currency are owned by the backend and cannot be written by the game.
    /// </summary>
    public static class AccountBridge
    {
        public static void Apply(AuthService auth)
        {
            var profile = AppServices.Profile;
            if (profile == null) return;

            var account = AppServices.Account;
            if (auth.State == AuthState.SignedIn && auth.Player != null)
            {
                // The game server verifies this same Supabase session and owns level/xp/wallet. Start (or keep) that session.
                account?.SyncWithAuth(auth);
                // Show the Supabase identity only until the game server confirms; its values replace these afterwards.
                if (account != null && account.State == AccountState.SignedIn) return;
                var snapshot = JsonUtility.FromJson<PlayerProfile>(JsonUtility.ToJson(profile.Profile));
                snapshot.publicId = auth.Player.PlayerId;
                snapshot.playerId = auth.Player.UserId;
                snapshot.username = auth.Player.Username;
                snapshot.level = Mathf.Max(1, auth.Player.Level);
                snapshot.xp = (int)Math.Min(int.MaxValue, auth.Player.Xp);
                profile.ApplyServerSnapshot(snapshot);
            }
            else
            {
                if (account != null && account.SupabaseBound) account.SyncWithAuth(auth);
                else profile.RevertToLocal();
            }
        }
    }
}
