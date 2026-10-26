using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;

namespace Noxy.Lobby
{
    /// <summary>
    /// Every full-screen backdrop is an image. To use real art, import a PNG/JPG as
    /// Assets/_Project/Resources/Backgrounds/{key}.png (Texture Type: Sprite). Keys: lobby, menu, auth.
    /// When no file exists a generated placeholder is used, so the game never shows an empty background.
    /// </summary>
    public static class BackgroundLibrary
    {
        public const string Lobby = "lobby";
        public const string Menu = "menu";
        public const string Auth = "auth";
        public static readonly string[] Keys = { Lobby, Menu, Auth };

        private static readonly Dictionary<string, Sprite> Cache = new Dictionary<string, Sprite>();
        private static readonly HashSet<string> Placeholders = new HashSet<string>();

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void Reset() { Cache.Clear(); Placeholders.Clear(); }

        public static bool IsPlaceholder(string key) { Get(key); return Placeholders.Contains(key); }

        public static Sprite Get(string key)
        {
            if (Cache.TryGetValue(key, out var cached) && cached != null) return cached;

            var sprite = Resources.Load<Sprite>("Backgrounds/" + key);
            if (sprite == null)
            {
                var tex = Resources.Load<Texture2D>("Backgrounds/" + key);
                if (tex != null)
                    sprite = Sprite.Create(tex, new Rect(0, 0, tex.width, tex.height), new Vector2(0.5f, 0.5f), 100f);
            }
            if (sprite == null)
            {
                var generated = GeneratePlaceholder(key);
                generated.hideFlags = HideFlags.HideAndDontSave;
                sprite = Sprite.Create(generated, new Rect(0, 0, generated.width, generated.height), new Vector2(0.5f, 0.5f), 100f);
                Placeholders.Add(key);
            }
            Cache[key] = sprite;
            return sprite;
        }

        /// <summary>Deterministic stylised night-city image. Also used by the editor tool to write real PNG files.</summary>
        public static Texture2D GeneratePlaceholder(string key, int width = 1024, int height = 576)
        {
            Color top, mid, horizon, glow, window;
            switch (key)
            {
                case Menu:
                    top = new Color(0.02f, 0.03f, 0.07f); mid = new Color(0.05f, 0.08f, 0.15f); horizon = new Color(0.07f, 0.13f, 0.22f);
                    glow = new Color(0.18f, 0.88f, 1f); window = new Color(0.25f, 0.7f, 0.9f); break;
                case Auth:
                    top = new Color(0.03f, 0.02f, 0.08f); mid = new Color(0.10f, 0.06f, 0.20f); horizon = new Color(0.30f, 0.14f, 0.26f);
                    glow = new Color(1f, 0.45f, 0.2f); window = new Color(1f, 0.75f, 0.4f); break;
                default:
                    top = new Color(0.015f, 0.03f, 0.07f); mid = new Color(0.05f, 0.11f, 0.20f); horizon = new Color(0.16f, 0.28f, 0.38f);
                    glow = new Color(1f, 0.56f, 0.12f); window = new Color(1f, 0.82f, 0.45f); break;
            }

            var px = new Color[width * height];
            var rng = new System.Random(key.GetHashCode() & 0x7fffffff ^ 4242);

            // Buildings: x-ranges with heights, drawn back (dim) to front (dark).
            var layers = new List<(int x0, int x1, int h, float shade)>[2];
            for (int l = 0; l < 2; l++)
            {
                layers[l] = new List<(int, int, int, float)>();
                int x = -10;
                while (x < width)
                {
                    int w = 28 + rng.Next(50);
                    int h = (int)(height * (l == 0 ? 0.20f + 0.26f * (float)rng.NextDouble() : 0.10f + 0.20f * (float)rng.NextDouble()));
                    layers[l].Add((x, x + w, h, l == 0 ? 0.55f : 0.25f));
                    x += w + rng.Next(6);
                }
            }

            var colH = new int[2][];
            var colX0 = new int[2][];
            for (int l = 0; l < 2; l++)
            {
                colH[l] = new int[width];
                colX0[l] = new int[width];
                foreach (var b in layers[l])
                    for (int x = Mathf.Max(0, b.x0); x < Mathf.Min(width, b.x1); x++) { colH[l][x] = b.h; colX0[l][x] = b.x0; }
            }

            for (int y = 0; y < height; y++)
            {
                float t = y / (height - 1f); // 0 bottom
                Color sky = t < 0.42f ? Color.Lerp(horizon, mid, t / 0.42f) : Color.Lerp(mid, top, (t - 0.42f) / 0.58f);
                for (int x = 0; x < width; x++)
                {
                    var c = sky;

                    // Sun/moon glow behind the centre-right of the skyline.
                    float dx = (x - width * 0.68f) / (width * 0.45f), dy = (y - height * 0.40f) / (height * 0.55f);
                    float g = Mathf.Clamp01(1f - Mathf.Sqrt(dx * dx + dy * dy));
                    c += glow * (g * g * 0.35f);

                    // Faint stars in the upper sky.
                    if (t > 0.55f && ((x * 73856093) ^ (y * 19349663)) % 997 == 0) c += Color.white * 0.35f;

                    for (int l = 0; l < 2; l++)
                    {
                        int h = colH[l][x];
                        if (y >= h) continue;
                        int x0 = colX0[l][x];
                        float shade = l == 0 ? 0.55f : 0.25f;
                        var body = Color.Lerp(horizon, Color.black, 0.7f + (1f - shade) * 0.3f);
                        bool lit = (x - x0) % 8 < 3 && y % 10 < 4 && ((x / 8 * 31 + y / 10 * 17 + x0) % 5) == 0;
                        c = lit ? Color.Lerp(body, window, l == 0 ? 0.45f : 0.8f) : body;
                        if (y == h - 1) c = Color.Lerp(c, glow, 0.35f);
                    }

                    // Vignette so UI text stays readable near the edges.
                    float vx = (x / (float)width - 0.5f) * 2f, vy = (y / (float)height - 0.5f) * 2f;
                    float v = 1f - Mathf.Clamp01((vx * vx + vy * vy) * 0.32f);
                    c *= Mathf.Lerp(0.65f, 1f, v);
                    c.a = 1f;
                    px[y * width + x] = c;
                }
            }

            var tex = new Texture2D(width, height, TextureFormat.RGB24, false)
            {
                wrapMode = TextureWrapMode.Clamp,
                filterMode = FilterMode.Bilinear,
                name = "Placeholder_" + key
            };
            tex.SetPixels(px);
            tex.Apply(false, false);
            return tex;
        }
    }

    /// <summary>
    /// Draws the current background image behind the 3D character using a dedicated camera, with a slow
    /// "drift" animation and a cross-fade when the key changes. Aspect-fills any screen size.
    /// </summary>
    public sealed class LobbyBackground : MonoBehaviour
    {
        private const float FadeSeconds = 0.35f;
        private const int UiLayer = 5;

        private Camera bgCamera;
        private Image front, back;
        private RectTransform frontHolder, backHolder;
        private string key = "";
        private float fade = 1f;

        public static LobbyBackground Create(Camera mainCamera, string key)
        {
            var go = new GameObject("LobbyBackground");
            var bg = go.AddComponent<LobbyBackground>();
            bg.Build(mainCamera);
            bg.SetKey(key, true);
            return bg;
        }

        private void Build(Camera mainCamera)
        {
            var camGo = new GameObject("BackgroundCamera") { layer = UiLayer };
            camGo.transform.SetParent(transform, false);
            bgCamera = camGo.AddComponent<Camera>();
            bgCamera.orthographic = true;
            bgCamera.clearFlags = CameraClearFlags.SolidColor;
            bgCamera.backgroundColor = Color.black;
            bgCamera.cullingMask = 1 << UiLayer;
            bgCamera.depth = -100f;
            bgCamera.allowHDR = false;
            bgCamera.allowMSAA = false;

            // The character camera now only clears depth so the image stays visible behind it.
            mainCamera.clearFlags = CameraClearFlags.Depth;
            mainCamera.cullingMask &= ~(1 << UiLayer);

            var canvasGo = new GameObject("BackgroundCanvas", typeof(RectTransform)) { layer = UiLayer };
            canvasGo.transform.SetParent(transform, false);
            var canvas = canvasGo.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceCamera;
            canvas.worldCamera = bgCamera;
            canvas.planeDistance = 1f;
            canvas.sortingOrder = -100;

            backHolder = NewLayer(canvasGo.transform, "Back", out back);
            frontHolder = NewLayer(canvasGo.transform, "Front", out front);
            back.color = new Color(1f, 1f, 1f, 0f);
        }

        private static RectTransform NewLayer(Transform parent, string name, out Image image)
        {
            var holderGo = new GameObject(name, typeof(RectTransform), typeof(RectMask2D)) { layer = UiLayer };
            var holder = (RectTransform)holderGo.transform;
            holder.SetParent(parent, false);
            UIKit.Stretch(holder);

            var imgGo = new GameObject("Image", typeof(RectTransform), typeof(Image), typeof(AspectRatioFitter)) { layer = UiLayer };
            var rt = (RectTransform)imgGo.transform;
            rt.SetParent(holder, false);
            rt.anchorMin = rt.anchorMax = rt.pivot = new Vector2(0.5f, 0.5f);
            rt.anchoredPosition = Vector2.zero;
            image = imgGo.GetComponent<Image>();
            image.raycastTarget = false;
            var fitter = imgGo.GetComponent<AspectRatioFitter>();
            fitter.aspectMode = AspectRatioFitter.AspectMode.EnvelopeParent;
            return holder;
        }

        private static void Apply(Image image, Sprite sprite)
        {
            image.sprite = sprite;
            image.preserveAspect = false;
            image.GetComponent<AspectRatioFitter>().aspectRatio = sprite.rect.width / Mathf.Max(1f, sprite.rect.height);
        }

        public void SetKey(string newKey, bool instant = false)
        {
            if (newKey == key) return;
            key = newKey;
            var sprite = BackgroundLibrary.Get(newKey);

            if (instant)
            {
                Apply(front, sprite);
                front.color = Color.white;
                back.color = new Color(1f, 1f, 1f, 0f);
                fade = 1f;
                return;
            }

            // Old front becomes the back layer and fades out while the new image fades in.
            Apply(back, front.sprite);
            back.color = Color.white;
            Apply(front, sprite);
            front.color = new Color(1f, 1f, 1f, 0f);
            fade = 0f;
        }

        private void Update()
        {
            if (fade < 1f)
            {
                fade = Mathf.Min(1f, fade + Time.unscaledDeltaTime / FadeSeconds);
                front.color = new Color(1f, 1f, 1f, fade);
                if (fade >= 1f) back.color = new Color(1f, 1f, 1f, 0f);
            }

            // Slow drift: slightly over-scaled so edges never show.
            float t = Time.unscaledTime;
            float scale = 1.06f + Mathf.Sin(t * 0.11f) * 0.015f;
            var rt = front.rectTransform;
            rt.localScale = new Vector3(scale, scale, 1f);
            rt.anchoredPosition = new Vector2(Mathf.Sin(t * 0.07f) * 14f, Mathf.Cos(t * 0.05f) * 8f);
            back.rectTransform.localScale = rt.localScale;
            back.rectTransform.anchoredPosition = rt.anchoredPosition;
        }

        private void OnDestroy()
        {
            if (bgCamera != null) Destroy(bgCamera.gameObject);
        }
    }
}
