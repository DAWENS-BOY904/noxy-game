using Noxy.Core;
using UnityEngine;

namespace Noxy.Lobby
{
    /// <summary>Applies GameSettings to the engine: audio, graphics preset, frame cap/VSync and window mode.</summary>
    public static class DisplayApplier
    {
        // Gameplay audio sources should multiply their volume by these (no music/SFX sources exist yet).
        public static float MusicLevel { get; private set; } = 1f;
        public static float SfxLevel { get; private set; } = 1f;
        public static float VoiceLevel { get; private set; } = 1f;

        /// <summary>No post-processing stack is installed yet; gameplay code should check this before enabling effects.</summary>
        public static bool PostProcessingEnabled { get; private set; } = true;

        public static bool IsMobile => Application.isMobilePlatform;

        public static int DisplayRefreshRate
        {
            get
            {
#if UNITY_2022_2_OR_NEWER
                return Mathf.RoundToInt((float)Screen.currentResolution.refreshRateRatio.value);
#else
                return Screen.currentResolution.refreshRate;
#endif
            }
        }

        public static GraphicsPreset CurrentPreset(GameSettings s) => (GraphicsPreset)Mathf.Clamp(s.graphicsPreset, 0, 3);

        /// <summary>Chooses first-run defaults from device capability (conservative on phones).</summary>
        public static void ApplyFirstRunDefaults(LocalConfigStore config)
        {
            if (!config.NeedsGraphicsPreset) return;
            var s = config.Settings;
            s.graphicsPreset = (int)GraphicsPresets.Recommend(IsMobile, SystemInfo.systemMemorySize, SystemInfo.processorCount);
            s.fpsLimit = IsMobile ? 60 : Mathf.Clamp(NearestPcCap(DisplayRefreshRate), 60, 300);
            config.SaveSettings();
        }

        private static int NearestPcCap(int refresh)
        {
            foreach (int cap in GameSettings.PcFpsOptions)
                if (cap != 0 && cap >= refresh) return cap;
            return 60;
        }

        public static void Apply(GameSettings s)
        {
            s.Sanitize(IsMobile);

            AudioListener.volume = s.masterVolume;
            MusicLevel = s.musicMuted ? 0f : s.musicVolume;
            SfxLevel = s.sfxMuted ? 0f : s.sfxVolume;
            VoiceLevel = s.voiceMuted ? 0f : s.voiceVolume;
            PostProcessingEnabled = s.postProcessing && GraphicsPresets.Get(CurrentPreset(s)).PostProcessing;

            ApplyGraphics(s);
            ApplyFrameRate(s);
            if (!IsMobile) ApplyWindow(s);
        }

        private static void ApplyGraphics(GameSettings s)
        {
            var p = GraphicsPresets.Get(CurrentPreset(s));

            QualitySettings.shadows = !p.ShadowsEnabled || s.shadowQuality == 0
                ? ShadowQuality.Disable
                : (s.shadowQuality == 1 ? ShadowQuality.HardOnly : ShadowQuality.All);
            QualitySettings.shadowResolution = (ShadowResolution)Mathf.Clamp(p.ShadowResolution, 0, 3);
            QualitySettings.shadowDistance = p.ShadowDistance;
            QualitySettings.antiAliasing = p.AntiAliasing;
            QualitySettings.pixelLightCount = p.PixelLights;

            int textureLimit = Mathf.Max(p.TextureLimit, s.textureQuality);
#if UNITY_6000_0_OR_NEWER
            QualitySettings.globalTextureMipmapLimit = textureLimit;
#else
            QualitySettings.masterTextureLimit = textureLimit;
#endif
        }

        /// <summary>
        /// Unity ignores Application.targetFrameRate while vSyncCount &gt; 0, so both values come from FramePolicy
        /// and are always set together. The cap is a maximum target, not a promise of that frame rate.
        /// </summary>
        public static FramePolicy.Result ApplyFrameRate(GameSettings s)
        {
            var r = FramePolicy.Resolve(s.fpsLimit, s.vSync, IsMobile);
            QualitySettings.vSyncCount = r.VSyncCount;
            Application.targetFrameRate = r.TargetFrameRate;
            return r;
        }

        private static void ApplyWindow(GameSettings s)
        {
            var mode = s.windowMode == (int)WindowMode.Windowed ? FullScreenMode.Windowed
                : s.windowMode == (int)WindowMode.Borderless ? FullScreenMode.FullScreenWindow
                : FullScreenMode.ExclusiveFullScreen;

            int w = s.resolutionWidth, h = s.resolutionHeight;
            if (w <= 0 || h <= 0) { w = Screen.currentResolution.width; h = Screen.currentResolution.height; }

            if (Screen.width != w || Screen.height != h || Screen.fullScreenMode != mode)
                Screen.SetResolution(w, h, mode);
        }
    }
}
