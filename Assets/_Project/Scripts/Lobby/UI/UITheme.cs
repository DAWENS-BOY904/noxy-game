using Noxy.Core;
using UnityEngine;

namespace Noxy.Lobby
{
    public static class UITheme
    {
        public static readonly Color Accent = new Color(1f, 0.56f, 0.12f, 1f);
        public static readonly Color AccentDark = new Color(0.82f, 0.34f, 0.04f, 1f);
        public static readonly Color Cyan = new Color(0.18f, 0.88f, 1f, 1f);
        public static readonly Color Panel = new Color(0.05f, 0.07f, 0.12f, 0.95f);
        public static readonly Color PanelLight = new Color(0.11f, 0.14f, 0.21f, 0.95f);
        public static readonly Color Button = new Color(0.12f, 0.16f, 0.24f, 0.92f);
        public static readonly Color Chip = new Color(0f, 0f, 0f, 0.45f);
        public static readonly Color Text = new Color(0.96f, 0.98f, 1f, 1f);
        public static readonly Color TextDim = new Color(0.68f, 0.75f, 0.84f, 1f);
        public static readonly Color Success = new Color(0.36f, 0.86f, 0.52f, 1f);
        public static readonly Color Warning = new Color(1f, 0.78f, 0.26f, 1f);
        public static readonly Color Danger = new Color(0.96f, 0.32f, 0.34f, 1f);
        public static readonly Color Disabled = new Color(0.22f, 0.25f, 0.31f, 0.95f);

        // Sizes are in canvas reference pixels (900 px reference height), so they stay readable on phones.
        public const int FontCaption = 24;
        public const int FontBody = 28;
        public const int FontButton = 30;
        public const int FontTitle = 40;
        public const int FontHuge = 60;

        public static Color Rarity(CosmeticRarity rarity)
        {
            switch (rarity)
            {
                case CosmeticRarity.Rare: return new Color(0.25f, 0.6f, 1f, 1f);
                case CosmeticRarity.Epic: return new Color(0.72f, 0.4f, 1f, 1f);
                case CosmeticRarity.Legendary: return new Color(1f, 0.72f, 0.2f, 1f);
                default: return new Color(0.7f, 0.75f, 0.8f, 1f);
            }
        }

        public static Color Severity(NotificationSeverity severity)
        {
            switch (severity)
            {
                case NotificationSeverity.Success: return Success;
                case NotificationSeverity.Warning: return Warning;
                case NotificationSeverity.Error: return Danger;
                default: return Cyan;
            }
        }
    }

    /// <summary>Runtime-generated UI sprites so the project needs no imported art to run.</summary>
    public static class UISprites
    {
        private static Sprite rounded;
        private static Sprite circle;
        private static Sprite spinner;
        private static Sprite gradientTop;

        public static Sprite Rounded
        {
            get { if (rounded == null) rounded = MakeRounded(64, 22); return rounded; }
        }

        public static Sprite Circle
        {
            get { if (circle == null) circle = MakeCircle(128, false); return circle; }
        }

        /// <summary>Arc with fading tail; rotate it for a loading spinner.</summary>
        public static Sprite Spinner
        {
            get { if (spinner == null) spinner = MakeCircle(128, true); return spinner; }
        }

        /// <summary>Opaque at the top, transparent at the bottom.</summary>
        public static Sprite GradientTop
        {
            get { if (gradientTop == null) gradientTop = MakeGradient(); return gradientTop; }
        }

        private static Texture2D NewTexture(int w, int h)
        {
            return new Texture2D(w, h, TextureFormat.RGBA32, false)
            {
                hideFlags = HideFlags.HideAndDontSave,
                wrapMode = TextureWrapMode.Clamp,
                filterMode = FilterMode.Bilinear
            };
        }

        private static Sprite MakeRounded(int size, int radius)
        {
            var tex = NewTexture(size, size);
            var px = new Color32[size * size];
            float half = size * 0.5f;
            for (int y = 0; y < size; y++)
            {
                for (int x = 0; x < size; x++)
                {
                    float qx = Mathf.Abs(x + 0.5f - half) - (half - radius);
                    float qy = Mathf.Abs(y + 0.5f - half) - (half - radius);
                    float outside = new Vector2(Mathf.Max(qx, 0f), Mathf.Max(qy, 0f)).magnitude + Mathf.Min(Mathf.Max(qx, qy), 0f) - radius;
                    px[y * size + x] = new Color32(255, 255, 255, (byte)(Mathf.Clamp01(0.5f - outside) * 255f));
                }
            }
            tex.SetPixels32(px);
            tex.Apply(false, true);
            return Sprite.Create(tex, new Rect(0, 0, size, size), new Vector2(0.5f, 0.5f), 100f, 0,
                SpriteMeshType.FullRect, new Vector4(radius, radius, radius, radius));
        }

        private static Sprite MakeCircle(int size, bool arc)
        {
            var tex = NewTexture(size, size);
            var px = new Color32[size * size];
            float half = size * 0.5f;
            for (int y = 0; y < size; y++)
            {
                for (int x = 0; x < size; x++)
                {
                    float dx = x + 0.5f - half, dy = y + 0.5f - half;
                    float dist = Mathf.Sqrt(dx * dx + dy * dy);
                    float alpha;
                    if (!arc)
                    {
                        alpha = Mathf.Clamp01(half - dist);
                    }
                    else
                    {
                        float ring = Mathf.Clamp01(half - dist) * Mathf.Clamp01(dist - (half - 12f));
                        float angle = (Mathf.Atan2(dy, dx) + Mathf.PI) / (2f * Mathf.PI);
                        alpha = ring * angle;
                    }
                    px[y * size + x] = new Color32(255, 255, 255, (byte)(alpha * 255f));
                }
            }
            tex.SetPixels32(px);
            tex.Apply(false, true);
            return Sprite.Create(tex, new Rect(0, 0, size, size), new Vector2(0.5f, 0.5f), 100f);
        }

        private static Sprite MakeGradient()
        {
            const int h = 64;
            var tex = NewTexture(4, h);
            var px = new Color32[4 * h];
            for (int y = 0; y < h; y++)
            {
                float a = y / (h - 1f);
                for (int x = 0; x < 4; x++) px[y * 4 + x] = new Color32(255, 255, 255, (byte)(a * a * 255f));
            }
            tex.SetPixels32(px);
            tex.Apply(false, true);
            return Sprite.Create(tex, new Rect(0, 0, 4, h), new Vector2(0.5f, 0.5f), 100f);
        }
    }
}
