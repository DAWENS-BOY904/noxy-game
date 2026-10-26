using UnityEngine;

namespace Noxy.Lobby
{
    /// <summary>
    /// Developer/device-only lobby toggles (PlayerPrefs). Gameplay preferences live in GameSettings
    /// (LocalConfigStore) so they can sync to the backend.
    /// </summary>
    public static class LobbySettings
    {
        private const string AutoRotateKey = "noxy.autoRotate";
        private const string SimulateErrorKey = "noxy.simulateMatchmakingError";

        private static int autoRotateCache = -1;

        public static bool AutoRotateCharacter
        {
            get
            {
                if (autoRotateCache < 0) autoRotateCache = PlayerPrefs.GetInt(AutoRotateKey, 1);
                return autoRotateCache == 1;
            }
            set
            {
                autoRotateCache = value ? 1 : 0;
                PlayerPrefs.SetInt(AutoRotateKey, autoRotateCache);
            }
        }

        public static bool SimulateMatchmakingError
        {
            get => PlayerPrefs.GetInt(SimulateErrorKey, 0) == 1;
            set
            {
                PlayerPrefs.SetInt(SimulateErrorKey, value ? 1 : 0);
                if (AppServices.OfflineMatchmaking != null) AppServices.OfflineMatchmaking.SimulateFailure = value;
            }
        }

        public static void Save() => PlayerPrefs.Save();
    }
}
