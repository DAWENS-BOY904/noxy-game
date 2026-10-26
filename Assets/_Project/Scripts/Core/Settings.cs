using System;
using System.Collections.Generic;
using UnityEngine;

namespace Noxy.Core
{
    public enum RadarCorner { TopLeft, TopRight, BottomLeft, BottomRight }
    public enum WindowMode { Fullscreen, Borderless, Windowed }
    public enum GraphicsPreset { Low, Medium, High, Ultra }
    public enum Visibility { Everyone, Friends, Nobody }

    /// <summary>
    /// All user preferences. Flat and JsonUtility-friendly. Every value is range-checked by Sanitize(), so
    /// hand-edited or synced data can never put the game into an extreme configuration.
    /// </summary>
    [Serializable]
    public sealed class GameSettings
    {
        public const int Version = 1;

        // General
        public int version = Version;
        public string language = "en";
        public float uiScale = 1f;
        public bool showCrosshair = true;
        public bool notifications = true;
        public bool confirmDialogs = true;

        // Graphics
        public int graphicsPreset = -1;       // -1 = pick from device capability on first run
        public int textureQuality = 0;        // 0 full, 1 half, 2 quarter
        public int shadowQuality = 1;         // 0 off, 1 low, 2 high
        public bool postProcessing = true;
        public int resolutionWidth = 0;       // 0 = native
        public int resolutionHeight = 0;
        public int windowMode = (int)WindowMode.Fullscreen;
        public int fpsLimit = 60;             // 0 = unlimited (PC only)
        public bool vSync = false;

        // Audio
        public float masterVolume = 1f;
        public float musicVolume = 0.8f;
        public float sfxVolume = 1f;
        public float voiceVolume = 1f;
        public bool musicMuted;
        public bool sfxMuted;
        public bool voiceMuted;

        // Gameplay
        public bool radarNorthUp;
        public float radarSize = 1f;
        public int radarCorner = (int)RadarCorner.TopLeft;
        public bool damageIndicators = true;
        public bool autoPickup = true;
        public bool invertY;

        // Sensitivity (0-200 sliders; 50 is a moderate default)
        public int sensGeneral = 50;
        public int sensAds = 35;
        public int sensTouch = 50;
        public int sensHorizontal = 100;      // percent multipliers
        public int sensVertical = 100;
        // Look feel (all default to off so existing behaviour is unchanged)
        public int mouseSmoothing;            // 0-100: how much the mouse is smoothed (100 = about 80 ms)
        public int touchSmoothing;            // 0-100
        public int touchAcceleration;         // 0-100: fast swipes turn further (100 = up to 2x)
        public bool gyroEnabled;              // mobile only, needs a gyroscope
        public int sensGyro = 50;             // same 1-200 scale as the other sliders

        // Privacy and social (mirrored to the backend when signed in)
        public int friendRequests = (int)Visibility.Everyone;   // Everyone | Nobody
        public int partyInvites = (int)Visibility.Friends;      // Friends | Nobody
        public int onlineStatus = (int)Visibility.Friends;

        public static readonly int[] MobileFpsOptions = { 30, 45, 60 };
        public static readonly int[] PcFpsOptions = { 60, 120, 144, 240, 300, 0 };

        public void Sanitize(bool isMobile = false)
        {
            if (string.IsNullOrEmpty(language)) language = "en";
            uiScale = Mathf.Clamp(uiScale, 0.8f, 1.3f);
            graphicsPreset = Mathf.Clamp(graphicsPreset, -1, (int)GraphicsPreset.Ultra);
            if (isMobile && graphicsPreset == (int)GraphicsPreset.Ultra) graphicsPreset = (int)GraphicsPreset.High;
            textureQuality = Mathf.Clamp(textureQuality, 0, 2);
            shadowQuality = Mathf.Clamp(shadowQuality, 0, 2);
            resolutionWidth = Mathf.Clamp(resolutionWidth, 0, 16384);
            resolutionHeight = Mathf.Clamp(resolutionHeight, 0, 16384);
            windowMode = Mathf.Clamp(windowMode, 0, 2);
            fpsLimit = FramePolicy.NormalizeLimit(fpsLimit, isMobile, 60);

            masterVolume = Mathf.Clamp01(masterVolume);
            musicVolume = Mathf.Clamp01(musicVolume);
            sfxVolume = Mathf.Clamp01(sfxVolume);
            voiceVolume = Mathf.Clamp01(voiceVolume);

            radarSize = Mathf.Clamp(radarSize, 0.7f, 1.6f);
            radarCorner = Mathf.Clamp(radarCorner, 0, 3);

            sensGeneral = Mathf.Clamp(sensGeneral, SensitivityModel.MinSlider, SensitivityModel.MaxSlider);
            sensAds = Mathf.Clamp(sensAds, SensitivityModel.MinSlider, SensitivityModel.MaxSlider);
            sensTouch = Mathf.Clamp(sensTouch, SensitivityModel.MinSlider, SensitivityModel.MaxSlider);
            sensHorizontal = Mathf.Clamp(sensHorizontal, 20, 200);
            sensVertical = Mathf.Clamp(sensVertical, 20, 200);
            mouseSmoothing = Mathf.Clamp(mouseSmoothing, 0, 100);
            touchSmoothing = Mathf.Clamp(touchSmoothing, 0, 100);
            touchAcceleration = Mathf.Clamp(touchAcceleration, 0, 100);
            sensGyro = Mathf.Clamp(sensGyro, SensitivityModel.MinSlider, SensitivityModel.MaxSlider);

            friendRequests = friendRequests == (int)Visibility.Nobody ? (int)Visibility.Nobody : (int)Visibility.Everyone;
            partyInvites = partyInvites == (int)Visibility.Nobody ? (int)Visibility.Nobody : (int)Visibility.Friends;
            onlineStatus = Mathf.Clamp(onlineStatus, 0, 2);
        }

        public GameSettings Clone() => JsonUtility.FromJson<GameSettings>(JsonUtility.ToJson(this));

        /// <summary>Applies values received from the backend. Unknown keys are ignored; the result is range-checked.</summary>
        public void ApplySyncMap(Dictionary<string, object> map)
        {
            int I(string k, int cur) => map.TryGetValue(k, out var v) && v is double d ? (int)Math.Round(d) : cur;
            float F(string k, float cur) => map.TryGetValue(k, out var v) && v is double d ? (float)d : cur;
            bool B(string k, bool cur) => map.TryGetValue(k, out var v) && v is bool b ? b : cur;

            if (map.TryGetValue("language", out var lang) && lang is string l && l.Length > 0 && l.Length <= 8) language = l;
            uiScale = F("uiScale", uiScale); showCrosshair = B("showCrosshair", showCrosshair);
            notifications = B("notifications", notifications); confirmDialogs = B("confirmDialogs", confirmDialogs);
            graphicsPreset = I("graphicsPreset", graphicsPreset); textureQuality = I("textureQuality", textureQuality);
            shadowQuality = I("shadowQuality", shadowQuality); postProcessing = B("postProcessing", postProcessing);
            fpsLimit = I("fpsLimit", fpsLimit); vSync = B("vSync", vSync);
            masterVolume = F("masterVolume", masterVolume); musicVolume = F("musicVolume", musicVolume);
            sfxVolume = F("sfxVolume", sfxVolume); voiceVolume = F("voiceVolume", voiceVolume);
            radarNorthUp = B("radarNorthUp", radarNorthUp); radarSize = F("radarSize", radarSize); radarCorner = I("radarCorner", radarCorner);
            damageIndicators = B("damageIndicators", damageIndicators); autoPickup = B("autoPickup", autoPickup); invertY = B("invertY", invertY);
            sensGeneral = I("sensGeneral", sensGeneral); sensAds = I("sensAds", sensAds); sensTouch = I("sensTouch", sensTouch);
            sensHorizontal = I("sensHorizontal", sensHorizontal); sensVertical = I("sensVertical", sensVertical);
            mouseSmoothing = I("mouseSmoothing", mouseSmoothing); touchSmoothing = I("touchSmoothing", touchSmoothing);
            touchAcceleration = I("touchAcceleration", touchAcceleration); gyroEnabled = B("gyroEnabled", gyroEnabled); sensGyro = I("sensGyro", sensGyro);
            Sanitize();
        }

        /// <summary>Flat key/value form used for backend sync. Privacy fields travel as dedicated server columns.</summary>
        public Dictionary<string, object> ToSyncMap()
        {
            return new Dictionary<string, object>
            {
                ["language"] = language, ["uiScale"] = uiScale, ["showCrosshair"] = showCrosshair,
                ["notifications"] = notifications, ["confirmDialogs"] = confirmDialogs,
                ["graphicsPreset"] = graphicsPreset, ["textureQuality"] = textureQuality, ["shadowQuality"] = shadowQuality,
                ["postProcessing"] = postProcessing, ["fpsLimit"] = fpsLimit, ["vSync"] = vSync,
                ["masterVolume"] = masterVolume, ["musicVolume"] = musicVolume, ["sfxVolume"] = sfxVolume, ["voiceVolume"] = voiceVolume,
                ["radarNorthUp"] = radarNorthUp, ["radarSize"] = radarSize, ["radarCorner"] = radarCorner,
                ["damageIndicators"] = damageIndicators, ["autoPickup"] = autoPickup, ["invertY"] = invertY,
                ["sensGeneral"] = sensGeneral, ["sensAds"] = sensAds, ["sensTouch"] = sensTouch,
                ["sensHorizontal"] = sensHorizontal, ["sensVertical"] = sensVertical,
                ["mouseSmoothing"] = mouseSmoothing, ["touchSmoothing"] = touchSmoothing, ["touchAcceleration"] = touchAcceleration,
                ["gyroEnabled"] = gyroEnabled, ["sensGyro"] = sensGyro,
            };
        }
    }

    public static class FramePolicy
    {
        /// <summary>Frame-rate caps offered on this platform. 0 means "unlimited" (PC only).</summary>
        public static int[] Options(bool isMobile, int displayRefreshRate)
        {
            if (!isMobile) return GameSettings.PcFpsOptions;
            var list = new List<int>(GameSettings.MobileFpsOptions);
            if (displayRefreshRate >= 90) list.Add(90);
            if (displayRefreshRate >= 120) list.Add(120);
            return list.ToArray();
        }

        public static int NormalizeLimit(int requested, bool isMobile, int fallback)
        {
            if (isMobile)
            {
                if (requested <= 0) return fallback;
                return Mathf.Clamp(requested, 30, 120);
            }
            return requested < 0 ? fallback : Mathf.Min(requested, 1000);
        }

        public readonly struct Result
        {
            public readonly int TargetFrameRate;   // value for Application.targetFrameRate (-1 = platform default)
            public readonly int VSyncCount;        // value for QualitySettings.vSyncCount
            public readonly bool CapIgnoredByVSync;

            public Result(int fps, int vsync, bool ignored) { TargetFrameRate = fps; VSyncCount = vsync; CapIgnoredByVSync = ignored; }
        }

        /// <summary>
        /// Unity ignores Application.targetFrameRate while vSyncCount > 0, so the two are resolved together:
        /// mobile always uses a plain cap (no vSync); PC uses vSync when requested, otherwise the cap.
        /// </summary>
        public static Result Resolve(int fpsLimit, bool vSync, bool isMobile)
        {
            if (isMobile) return new Result(NormalizeLimit(fpsLimit, true, 60), 0, false);
            if (vSync) return new Result(-1, 1, fpsLimit != 0);
            return new Result(fpsLimit <= 0 ? -1 : fpsLimit, 0, false);
        }
    }

    public readonly struct GraphicsPresetData
    {
        public readonly int ShadowResolution;     // 0 low .. 3 very high (UnityEngine.ShadowResolution)
        public readonly float ShadowDistance;
        public readonly bool ShadowsEnabled;
        public readonly int AntiAliasing;         // 0,2,4,8
        public readonly int TextureLimit;         // 0 full .. 2 quarter
        public readonly int PixelLights;
        public readonly bool PostProcessing;
        public readonly float RenderScale;

        public GraphicsPresetData(bool shadows, int shadowRes, float shadowDist, int aa, int tex, int lights, bool post, float scale)
        {
            ShadowsEnabled = shadows; ShadowResolution = shadowRes; ShadowDistance = shadowDist; AntiAliasing = aa;
            TextureLimit = tex; PixelLights = lights; PostProcessing = post; RenderScale = scale;
        }
    }

    public static class GraphicsPresets
    {
        public static GraphicsPresetData Get(GraphicsPreset preset)
        {
            switch (preset)
            {
                case GraphicsPreset.Low: return new GraphicsPresetData(false, 0, 0f, 0, 1, 1, false, 0.75f);
                case GraphicsPreset.Medium: return new GraphicsPresetData(true, 0, 20f, 0, 0, 2, false, 0.9f);
                case GraphicsPreset.High: return new GraphicsPresetData(true, 1, 40f, 2, 0, 4, true, 1f);
                default: return new GraphicsPresetData(true, 2, 80f, 4, 0, 8, true, 1f);
            }
        }

        /// <summary>Conservative first-run choice so low-end phones are not forced to maximum settings.</summary>
        public static GraphicsPreset Recommend(bool isMobile, int systemMemoryMb, int processorCount)
        {
            if (isMobile)
            {
                if (systemMemoryMb < 3500 || processorCount < 6) return GraphicsPreset.Low;
                if (systemMemoryMb < 6000) return GraphicsPreset.Medium;
                return GraphicsPreset.High;
            }
            if (systemMemoryMb < 6000 || processorCount < 4) return GraphicsPreset.Medium;
            return GraphicsPreset.High;
        }
    }

    public enum LookSource { Mouse, Touch }

    /// <summary>Converts raw look input into camera rotation. This is the single place sensitivity is applied.</summary>
    public static class SensitivityModel
    {
        public const int MinSlider = 1;
        public const int MaxSlider = 200;

        private const float MouseDegreesPerCount = 0.06f;   // at slider 50
        private const float TouchDegreesPerPixel = 0.14f;   // at slider 50

        /// <returns>(yaw degrees, pitch degrees) to add for this frame. Positive pitch looks down.</returns>
        public static Vector2 LookDelta(Vector2 rawDelta, LookSource source, bool aiming, GameSettings s)
        {
            float slider = source == LookSource.Touch ? s.sensTouch : (aiming ? s.sensAds : s.sensGeneral);
            if (source == LookSource.Touch && aiming) slider = s.sensTouch * (s.sensAds / (float)Mathf.Max(1, s.sensGeneral));
            slider = Mathf.Clamp(slider, MinSlider, MaxSlider * 2f);

            float perUnit = (source == LookSource.Touch ? TouchDegreesPerPixel : MouseDegreesPerCount) * (slider / 50f);
            float yaw = rawDelta.x * perUnit * (s.sensHorizontal / 100f);
            float pitch = rawDelta.y * perUnit * (s.sensVertical / 100f) * (s.invertY ? 1f : -1f);
            return new Vector2(yaw, pitch);
        }
    }

    /// <summary>
    /// Smooths a stream of look deltas without changing the total rotation: every degree that comes in eventually comes out.
    /// It is time-based (exponential), so it behaves the same at 30, 60 or 240 FPS.
    /// </summary>
    public sealed class LookFilter
    {
        public const float MaxTauSeconds = 0.08f;
        private float pendingX, pendingY;

        public Vector2 Apply(float dx, float dy, float dt, int smoothingPercent)
        {
            if (smoothingPercent <= 0)
            {
                // smoothing was switched off: release whatever was still pending, then pass input straight through
                float ox = dx + pendingX, oy = dy + pendingY; pendingX = 0f; pendingY = 0f;
                return new Vector2(ox, oy);
            }
            pendingX += dx; pendingY += dy;
            float tau = MaxTauSeconds * (smoothingPercent / 100f);
            float k = dt <= 0f ? 1f : 1f - (float)Math.Exp(-dt / tau);
            float outX = pendingX * k, outY = pendingY * k;
            pendingX -= outX; pendingY -= outY;
            return new Vector2(outX, outY);
        }

        public void Reset() { pendingX = 0f; pendingY = 0f; }
    }

    /// <summary>
    /// The ONE place look input becomes camera rotation: sensitivity, optional smoothing, touch acceleration and gyro are
    /// applied here exactly once. Controllers pass raw device input and add the result to yaw/pitch; they never scale it again.
    /// </summary>
    public sealed class LookPipeline
    {
        public const float TouchAccelReferencePixelsPerSecond = 1500f;
        public const float GyroSignYaw = -1f, GyroSignPitch = 1f;   // UNVERIFIED on a device: flip here if the gyro feels reversed

        private readonly LookFilter mouse = new LookFilter(), touch = new LookFilter();

        /// <summary>Acceleration factor (1 = none) for a touch swipe of the given size in this frame.</summary>
        public static float TouchGain(float rawX, float rawY, float dt, int accelerationPercent)
        {
            if (accelerationPercent <= 0 || dt <= 0f) return 1f;
            float speed = (float)Math.Sqrt(rawX * rawX + rawY * rawY) / dt;
            float t = Mathf.Clamp01(speed / TouchAccelReferencePixelsPerSecond);
            return 1f + (accelerationPercent / 100f) * t;
        }

        /// <summary>Gyro rotation (radians per second around the device's x and y axes) as yaw/pitch degrees for this frame.</summary>
        public static Vector2 GyroDelta(float rateX, float rateY, float dt, bool aiming, GameSettings s)
        {
            if (!s.gyroEnabled || dt <= 0f) return new Vector2(0f, 0f);
            float slider = s.sensGyro;
            if (aiming) slider *= s.sensAds / (float)Mathf.Max(1, s.sensGeneral);
            float deg = 57.29578f * dt * (slider / 50f);
            return new Vector2(GyroSignYaw * rateY * deg * (s.sensHorizontal / 100f), GyroSignPitch * rateX * deg * (s.sensVertical / 100f) * (s.invertY ? 1f : -1f));
        }

        public Vector2 Process(Vector2 rawMouse, Vector2 rawTouch, float gyroRateX, float gyroRateY, bool aiming, float dt, GameSettings s)
        {
            var m = SensitivityModel.LookDelta(rawMouse, LookSource.Mouse, aiming, s);
            m = mouse.Apply(m.x, m.y, dt, s.mouseSmoothing);

            float gain = TouchGain(rawTouch.x, rawTouch.y, dt, s.touchAcceleration);
            var t = SensitivityModel.LookDelta(new Vector2(rawTouch.x * gain, rawTouch.y * gain), LookSource.Touch, aiming, s);
            t = touch.Apply(t.x, t.y, dt, s.touchSmoothing);

            var g = GyroDelta(gyroRateX, gyroRateY, dt, aiming, s);
            return new Vector2(m.x + t.x + g.x, m.y + t.y + g.y);
        }

        public void Reset() { mouse.Reset(); touch.Reset(); }
    }
}