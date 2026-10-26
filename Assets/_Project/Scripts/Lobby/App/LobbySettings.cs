using UnityEngine;

namespace Noxy.Lobby
{
    /// <summary>Device-local preferences (PlayerPrefs). Not account data.</summary>
    public static class LobbySettings
    {
        private const string MasterVolumeKey = "noxy.masterVolume";
        private const string QualityKey = "noxy.quality";
        private const string AutoRotateKey = "noxy.autoRotate";
        private const string SimulateErrorKey = "noxy.simulateMatchmakingError";

        public static float MasterVolume
        {
            get => PlayerPrefs.GetFloat(MasterVolumeKey, 1f);
            set { PlayerPrefs.SetFloat(MasterVolumeKey, Mathf.Clamp01(value)); AudioListener.volume = Mathf.Clamp01(value); }
        }

        public static int QualityLevel
        {
            get => Mathf.Clamp(PlayerPrefs.GetInt(QualityKey, QualitySettings.GetQualityLevel()), 0, QualitySettings.names.Length - 1);
            set { PlayerPrefs.SetInt(QualityKey, value); QualitySettings.SetQualityLevel(value, true); }
        }

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

        private static int autoRotateCache = -1;

        public static bool SimulateMatchmakingError
        {
            get => PlayerPrefs.GetInt(SimulateErrorKey, 0) == 1;
            set
            {
                PlayerPrefs.SetInt(SimulateErrorKey, value ? 1 : 0);
                if (AppServices.OfflineMatchmaking != null) AppServices.OfflineMatchmaking.SimulateFailure = value;
            }
        }

        public static void Apply()
        {
            AudioListener.volume = MasterVolume;
            if (PlayerPrefs.HasKey(QualityKey)) QualitySettings.SetQualityLevel(QualityLevel, true);
        }

        public static void Save() => PlayerPrefs.Save();
    }
}
