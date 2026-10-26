using System;
using System.Collections;
using System.Text;
using Noxy.Core;
using UnityEngine;
using UnityEngine.Networking;

namespace Noxy.Lobby
{
    /// <summary>
    /// Persistent object that ticks account/network work and hosts coroutines for web requests.
    /// Created once by AppServices and kept across scene loads.
    /// </summary>
    public sealed class AppRunner : MonoBehaviour
    {
        public static AppRunner Instance { get; private set; }

        public static AppRunner Ensure()
        {
            if (Instance != null) return Instance;
            var go = new GameObject("AppRunner");
            DontDestroyOnLoad(go);
            Instance = go.AddComponent<AppRunner>();
            return Instance;
        }

        private void OnDestroy()
        {
            if (Instance == this) Instance = null;
        }

        private void Update()
        {
            if (AppServices.Account != null) AppServices.Account.Tick(Time.unscaledDeltaTime);
            if (AppServices.BrAvailability != null) AppServices.BrAvailability.Tick(Time.unscaledDeltaTime);
            if (AppServices.Auth != null)
            {
                OAuthLauncher.Pump(AppServices.Auth);
                AppServices.Auth.Tick();
            }
        }

        private void OnApplicationPause(bool paused)
        {
            if (paused) LobbySettings.Save();
        }

        private void OnApplicationQuit() => LobbySettings.Save();
    }

    /// <summary>ITransport over UnityWebRequest. Callbacks run on the main thread.</summary>
    public sealed class UnityWebRequestTransport : IHeaderTransport
    {
        private const int TimeoutSeconds = 10;

        public void Send(string method, string url, string jsonBody, string bearerToken, Action<HttpResponse> done)
        {
            AppRunner.Ensure().StartCoroutine(Run(method, url, jsonBody, bearerToken, null, done));
        }

        public void SendWithHeaders(string method, string url, string jsonBody, string bearerToken,
            System.Collections.Generic.IDictionary<string, string> headers, Action<HttpResponse> done)
        {
            AppRunner.Ensure().StartCoroutine(Run(method, url, jsonBody, bearerToken, headers, done));
        }

        private static IEnumerator Run(string method, string url, string jsonBody, string token,
            System.Collections.Generic.IDictionary<string, string> headers, Action<HttpResponse> done)
        {
            var response = new HttpResponse();
            using (var request = new UnityWebRequest(url, method))
            {
                request.downloadHandler = new DownloadHandlerBuffer();
                request.timeout = TimeoutSeconds;
                if (jsonBody != null)
                {
                    request.uploadHandler = new UploadHandlerRaw(Encoding.UTF8.GetBytes(jsonBody));
                    request.SetRequestHeader("Content-Type", "application/json");
                }
                if (!string.IsNullOrEmpty(token)) request.SetRequestHeader("Authorization", "Bearer " + token);
                if (headers != null)
                    foreach (var h in headers) request.SetRequestHeader(h.Key, h.Value);

                yield return request.SendWebRequest();

                if (request.result == UnityWebRequest.Result.ConnectionError)
                {
                    response.Status = 0;
                    response.NetworkError = request.error;
                }
                else
                {
                    response.Status = (int)request.responseCode;
                    response.Body = request.downloadHandler.text;
                }
            }
            done(response);
        }
    }

    /// <summary>
    /// Stores the session token and server address in PlayerPrefs. Fine for development; for production keep the
    /// token in the platform keystore (Android Keystore / iOS Keychain) instead of plain preferences.
    /// </summary>
    public sealed class PrefsTokenStore : ITokenStore
    {
        private const string TokenKey = "noxy.session";
        private const string UrlKey = "noxy.serverUrl";

        public string LoadToken() => PlayerPrefs.GetString(TokenKey, "");
        public void SaveToken(string token) { PlayerPrefs.SetString(TokenKey, token ?? ""); PlayerPrefs.Save(); }
        public string LoadServerUrl() => PlayerPrefs.GetString(UrlKey, "");
        public void SaveServerUrl(string url) { PlayerPrefs.SetString(UrlKey, url ?? ""); PlayerPrefs.Save(); }
    }
}
