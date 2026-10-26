using System;
using UnityEngine;
using UnityEngine.UI;

namespace Noxy.Lobby
{
    /// <summary>
    /// Picks the canvas reference resolution/matching for the current aspect ratio and keeps
    /// the SafeArea rect inside Screen.safeArea (notches, rounded corners, home indicators).
    /// </summary>
    public sealed class ResponsiveCanvas : MonoBehaviour
    {
        private CanvasScaler scaler;
        private RectTransform safeRoot;
        private Vector2Int lastScreen;
        private Rect lastSafeArea;

        public bool IsPortrait { get; private set; }

        /// <summary>Size of the safe-area root in canvas units.</summary>
        public Vector2 SafeSize { get; private set; }

        public event Action LayoutChanged;

        public void Initialize(CanvasScaler scaler, RectTransform safeRoot)
        {
            this.scaler = scaler;
            this.safeRoot = safeRoot;
            lastScreen = Vector2Int.zero;
            Refresh();
        }

        private void Update()
        {
            if (scaler == null) return;
            if (lastScreen.x != Screen.width || lastScreen.y != Screen.height || lastSafeArea != Screen.safeArea)
                Refresh();
        }

        private void Refresh()
        {
            int w = Mathf.Max(1, Screen.width), h = Mathf.Max(1, Screen.height);
            lastScreen = new Vector2Int(w, h);
            lastSafeArea = Screen.safeArea;
            float aspect = (float)w / h;
            IsPortrait = h > w;

            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.screenMatchMode = CanvasScaler.ScreenMatchMode.MatchWidthOrHeight;
            if (IsPortrait)
            {
                scaler.referenceResolution = new Vector2(900f, 1600f);
                scaler.matchWidthOrHeight = 0f; // keep width constant, extra height is free space
            }
            else
            {
                scaler.referenceResolution = new Vector2(1600f, 900f);
                // 4:3 matches width so nothing overflows; 16:9 and wider match height.
                scaler.matchWidthOrHeight = Mathf.Clamp01(Mathf.InverseLerp(1.33f, 1.78f, aspect));
            }

            Rect safe = Screen.safeArea;
            if (safe.width < 1f || safe.height < 1f) safe = new Rect(0, 0, w, h);
            safeRoot.anchorMin = new Vector2(safe.xMin / w, safe.yMin / h);
            safeRoot.anchorMax = new Vector2(safe.xMax / w, safe.yMax / h);
            safeRoot.offsetMin = Vector2.zero;
            safeRoot.offsetMax = Vector2.zero;

            // Computed directly: CanvasScaler applies its new scale on its own update, after this call.
            float scale = Mathf.Pow(w / scaler.referenceResolution.x, 1f - scaler.matchWidthOrHeight)
                          * Mathf.Pow(h / scaler.referenceResolution.y, scaler.matchWidthOrHeight);
            SafeSize = new Vector2(safe.width / scale, safe.height / scale);
            LayoutChanged?.Invoke();
        }
    }
}
