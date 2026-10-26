using UnityEngine;

namespace Noxy.Lobby
{
    /// <summary>
    /// Creates runtime materials with whichever render pipeline's shader is available (Built-in or URP).
    /// The shaders are added to "Always Included Shaders" by the editor setup so device builds keep them.
    /// </summary>
    public static class MaterialFactory
    {
        private static Shader Find(params string[] names)
        {
            foreach (var n in names)
            {
                var s = Shader.Find(n);
                if (s != null) return s;
            }
            return Shader.Find("Sprites/Default");
        }

        public static Material Lit(Color color, float metallic = 0f, float smoothness = 0.35f)
        {
            var m = new Material(Find("Universal Render Pipeline/Lit", "Standard"));
            SetColor(m, color);
            if (m.HasProperty("_Metallic")) m.SetFloat("_Metallic", metallic);
            if (m.HasProperty("_Glossiness")) m.SetFloat("_Glossiness", smoothness);
            if (m.HasProperty("_Smoothness")) m.SetFloat("_Smoothness", smoothness);
            return m;
        }

        public static Material Unlit(Color color)
        {
            var m = new Material(Find("Universal Render Pipeline/Unlit", "Unlit/Color"));
            SetColor(m, color);
            return m;
        }

        public static Material UnlitTexture(Texture texture)
        {
            var m = new Material(Find("Universal Render Pipeline/Unlit", "Unlit/Texture"));
            m.mainTexture = texture;
            SetColor(m, Color.white);
            return m;
        }

        /// <summary>Alpha-blended or additive textured material for glows, beams and particles.</summary>
        public static Material Glow(Texture texture, Color tint, bool additive = true)
        {
            var shader = additive
                ? Find("Legacy Shaders/Particles/Additive", "Mobile/Particles/Additive", "Sprites/Default")
                : Find("Sprites/Default");
            var m = new Material(shader);
            m.mainTexture = texture;
            SetColor(m, tint);
            m.renderQueue = 3000;
            return m;
        }

        public static void SetColor(Material m, Color c)
        {
            if (m.HasProperty("_BaseColor")) m.SetColor("_BaseColor", c);
            if (m.HasProperty("_Color")) m.SetColor("_Color", c);
            if (m.HasProperty("_TintColor")) m.SetColor("_TintColor", c);
        }

        public static GameObject Primitive(PrimitiveType type, string name, Transform parent, Vector3 localPos, Vector3 localScale, Material material)
        {
            var go = GameObject.CreatePrimitive(type);
            go.name = name;
            var collider = go.GetComponent<Collider>();
            if (collider != null) Object.Destroy(collider);
            go.transform.SetParent(parent, false);
            go.transform.localPosition = localPos;
            go.transform.localScale = localScale;
            var r = go.GetComponent<Renderer>();
            r.sharedMaterial = material;
            r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.On;
            r.receiveShadows = true;
            return go;
        }
    }

    /// <summary>Procedurally generated textures for lobby effects.</summary>
    public static class ProceduralTextures
    {
        private static Texture2D radial;
        private static Texture2D beam;
        private static Texture2D sky;

        public static Texture2D RadialGlow
        {
            get
            {
                if (radial == null)
                {
                    radial = New(128, 128);
                    var px = new Color32[128 * 128];
                    for (int y = 0; y < 128; y++)
                        for (int x = 0; x < 128; x++)
                        {
                            float d = Mathf.Clamp01(new Vector2(x - 63.5f, y - 63.5f).magnitude / 64f);
                            float a = Mathf.Pow(1f - d, 2.2f);
                            byte v = (byte)(a * 255f);
                            px[y * 128 + x] = new Color32(v, v, v, v);
                        }
                    radial.SetPixels32(px);
                    radial.Apply(false, true);
                }
                return radial;
            }
        }

        /// <summary>Soft vertical light beam: bright core, faded edges, fades toward the bottom.</summary>
        public static Texture2D Beam
        {
            get
            {
                if (beam == null)
                {
                    beam = New(32, 128);
                    var px = new Color32[32 * 128];
                    for (int y = 0; y < 128; y++)
                        for (int x = 0; x < 32; x++)
                        {
                            float edge = 1f - Mathf.Abs((x - 15.5f) / 16f);
                            float a = edge * edge * Mathf.Clamp01(y / 127f);
                            byte v = (byte)(a * 255f);
                            px[y * 32 + x] = new Color32(v, v, v, v);
                        }
                    beam.SetPixels32(px);
                    beam.Apply(false, true);
                }
                return beam;
            }
        }

        public static Texture2D Sky
        {
            get
            {
                if (sky == null)
                {
                    const int h = 256;
                    sky = New(2, h);
                    var top = new Color(0.015f, 0.03f, 0.07f);
                    var mid = new Color(0.04f, 0.09f, 0.17f);
                    var horizon = new Color(0.10f, 0.20f, 0.30f);
                    var px = new Color[2 * h];
                    for (int y = 0; y < h; y++)
                    {
                        float t = y / (h - 1f); // 0 bottom, 1 top
                        Color c = t < 0.45f ? Color.Lerp(horizon, mid, t / 0.45f)
                                            : Color.Lerp(mid, top, (t - 0.45f) / 0.55f);
                        px[y * 2] = c;
                        px[y * 2 + 1] = c;
                    }
                    sky.SetPixels(px);
                    sky.Apply(false, true);
                }
                return sky;
            }
        }

        private static Texture2D New(int w, int h)
        {
            return new Texture2D(w, h, TextureFormat.RGBA32, false)
            {
                hideFlags = HideFlags.HideAndDontSave,
                wrapMode = TextureWrapMode.Clamp,
                filterMode = FilterMode.Bilinear
            };
        }
    }
}
