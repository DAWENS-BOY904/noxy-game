using System.Collections.Generic;
using Noxy.Core;
using UnityEngine;
using UnityEngine.Rendering;

namespace Noxy.Lobby
{
    public static class BrLayers
    {
        /// <summary>The exterior aircraft lives on this layer so the interior camera can leave it out of view during the cabin sequence.</summary>
        public const int PlaneExterior = 22;
    }

    /// <summary>
    /// Original placeholder cabin interior. Local axes follow the server's contract: +X is the plane's forward direction and
    /// the walkable floor is the BrCabinCfg rectangle (x in [-halfLength, halfLength], z in [-halfWidth, halfWidth]).
    /// The side walls have window openings so the real island is visible below. Replace with the supplied cabin model only
    /// after its rights are confirmed (see Core/LandmarkAssets.cs).
    /// </summary>
    public sealed class BrCabinView : MonoBehaviour
    {
        private readonly List<Renderer> renderers = new List<Renderer>();
        public BrCabinCfg Cfg { get; private set; }

        public static BrCabinView Create(Transform parent, BrCabinCfg cfg)
        {
            var go = new GameObject("Cabin");
            go.transform.SetParent(parent, false);
            var v = go.AddComponent<BrCabinView>();
            v.Cfg = cfg;
            v.Build();
            return v;
        }

        private GameObject Box(string name, Vector3 pos, Vector3 size, Material m)
        {
            var g = GameObject.CreatePrimitive(PrimitiveType.Cube);
            g.name = name; g.transform.SetParent(transform, false);
            g.transform.localPosition = pos; g.transform.localScale = size;
            var r = g.GetComponent<Renderer>(); r.sharedMaterial = m; r.shadowCastingMode = ShadowCastingMode.Off;
            renderers.Add(r);
            return g;
        }

        private void Build()
        {
            float hl = Cfg.halfLength + 1.5f, hw = Cfg.halfWidth + 0.8f, h = 3.2f;
            var floor = MaterialFactory.Lit(new Color(0.32f, 0.33f, 0.36f), 0f, 0.1f);
            var wall = MaterialFactory.Lit(new Color(0.72f, 0.74f, 0.78f), 0f, 0.1f);
            var accent = MaterialFactory.Lit(new Color(0.12f, 0.55f, 0.75f), 0f, 0.1f);
            var door = MaterialFactory.Unlit(new Color(0.2f, 0.9f, 0.5f));

            Box("Floor", new Vector3(0f, -0.1f, 0f), new Vector3(hl * 2f, 0.2f, hw * 2f), floor);
            Box("Ceiling", new Vector3(0f, h, 0f), new Vector3(hl * 2f, 0.2f, hw * 2f), wall);
            Box("EndFront", new Vector3(hl, h * 0.5f, 0f), new Vector3(0.3f, h, hw * 2f), wall);
            Box("EndBack", new Vector3(-hl, h * 0.5f, 0f), new Vector3(0.3f, h, hw * 2f), wall);
            foreach (float side in new[] { -1f, 1f })
            {
                float z = side * hw;
                Box("SideLow", new Vector3(0f, 0.5f, z), new Vector3(hl * 2f, 1f, 0.25f), wall);
                Box("SideHigh", new Vector3(0f, 2.75f, z), new Vector3(hl * 2f, 0.9f, 0.25f), wall);
                for (float x = -hl + 0.5f; x <= hl - 0.5f; x += 3.2f) Box("Pillar", new Vector3(x, h * 0.5f, z), new Vector3(0.5f, h, 0.3f), accent);
                // benches along the wall
                Box("Bench", new Vector3(0f, 0.3f, side * (hw - 0.7f)), new Vector3(hl * 1.7f, 0.15f, 0.5f), accent);
            }
            Box("JumpDoorMarker", new Vector3(0f, 1.4f, hw - 0.05f), new Vector3(2.4f, 2.4f, 0.08f), door);
            for (int i = -1; i <= 1; i++)
            {
                var l = new GameObject("CabinLight").AddComponent<Light>();
                l.transform.SetParent(transform, false); l.transform.localPosition = new Vector3(i * hl * 0.6f, h - 0.5f, 0f);
                l.type = LightType.Point; l.range = hl * 0.8f; l.intensity = 0.8f; l.shadows = LightShadows.None;
            }
        }

        public Vector3 ToWorld(float lx, float lz) => transform.TransformPoint(new Vector3(lx, 0.05f, lz));
        public void SetVisible(bool visible) { foreach (var r in renderers) if (r != null) r.enabled = visible; }
    }

    /// <summary>
    /// The aircraft: exterior placeholder (hidden from the interior camera) plus the cabin as a child, so passengers ride with
    /// it. Its position comes from the server's route and clock, so every client sees the same plane at the same moment.
    /// </summary>
    public sealed class BrPlaneRig : MonoBehaviour
    {
        public BrCabinView Cabin { get; private set; }
        public GameObject Exterior { get; private set; }

        public static BrPlaneRig Create(BrCabinCfg cfg)
        {
            var go = new GameObject("PlaneRig");
            var rig = go.AddComponent<BrPlaneRig>();
            rig.Cabin = BrCabinView.Create(go.transform, cfg);
            rig.Exterior = rig.BuildExterior(go.transform);
            return rig;
        }

        private GameObject BuildExterior(Transform parent)
        {
            var root = new GameObject("Exterior").transform;
            root.SetParent(parent, false);
            root.localPosition = new Vector3(0f, 0f, 0f);
            var body = MaterialFactory.Lit(new Color(0.85f, 0.87f, 0.9f), 0.2f, 0.4f);
            var accent = MaterialFactory.Lit(new Color(0.12f, 0.55f, 0.75f), 0.1f, 0.4f);
            Part(root, PrimitiveType.Capsule, "Fuselage", new Vector3(0f, 0.2f, 0f), new Vector3(5f, 17f, 5f), Quaternion.Euler(0f, 0f, 90f), body);
            Part(root, PrimitiveType.Cube, "Wings", new Vector3(1f, 0f, 0f), new Vector3(5f, 0.4f, 38f), Quaternion.identity, body);
            Part(root, PrimitiveType.Cube, "TailWing", new Vector3(-15f, 1f, 0f), new Vector3(3f, 0.3f, 12f), Quaternion.identity, accent);
            Part(root, PrimitiveType.Cube, "Fin", new Vector3(-15f, 4f, 0f), new Vector3(4f, 6f, 0.4f), Quaternion.identity, accent);
            // keep the exterior out of the interior camera and out of colliders: it is scenery
            foreach (var t in root.GetComponentsInChildren<Transform>(true)) t.gameObject.layer = BrLayers.PlaneExterior;
            foreach (var c in root.GetComponentsInChildren<Collider>(true)) Destroy(c);
            return root.gameObject;
        }

        private static void Part(Transform parent, PrimitiveType type, string name, Vector3 pos, Vector3 size, Quaternion rot, Material m)
        {
            var g = GameObject.CreatePrimitive(type);
            g.name = name; g.transform.SetParent(parent, false);
            g.transform.localPosition = pos; g.transform.localRotation = rot; g.transform.localScale = size;
            g.GetComponent<Renderer>().sharedMaterial = m;
        }

        /// <summary>Places the plane where the server says it is at the given server time.</summary>
        public void Pose(BrRouteDto route, BrTimesDto times, long serverNowMs)
        {
            float span = Mathf.Max(1f, times.flightEndsAt - times.flightStartsAt);
            float f = Mathf.Clamp01((serverNowMs - times.flightStartsAt) / span);
            transform.position = new Vector3(Mathf.Lerp(route.ax, route.bx, f), route.altitude, Mathf.Lerp(route.az, route.bz, f));
            // local +X must point along the flight direction (see the server's exit-point formula)
            transform.rotation = Quaternion.Euler(0f, route.headingDeg - 90f, 0f);
        }
    }

    public sealed class BrParachuteView : MonoBehaviour
    {
        public static BrParachuteView Attach(Transform player)
        {
            var go = new GameObject("Parachute");
            go.transform.SetParent(player, false);
            var cloth = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            cloth.name = "Canopy"; cloth.transform.SetParent(go.transform, false);
            cloth.transform.localPosition = new Vector3(0f, 3.4f, 0f); cloth.transform.localScale = new Vector3(4.2f, 1.7f, 4.2f);
            cloth.GetComponent<Renderer>().sharedMaterial = MaterialFactory.Lit(new Color(0.95f, 0.5f, 0.15f), 0f, 0.2f);
            Destroy(cloth.GetComponent<Collider>());
            for (int i = 0; i < 4; i++)
            {
                var line = GameObject.CreatePrimitive(PrimitiveType.Cube);
                line.name = "Line" + i; line.transform.SetParent(go.transform, false);
                float a = i * Mathf.PI / 2f + Mathf.PI / 4f;
                line.transform.localPosition = new Vector3(Mathf.Cos(a) * 0.9f, 1.8f, Mathf.Sin(a) * 0.9f);
                line.transform.localScale = new Vector3(0.03f, 3.2f, 0.03f);
                line.transform.localRotation = Quaternion.Euler(Mathf.Sin(a) * 14f, 0f, -Mathf.Cos(a) * 14f);
                line.GetComponent<Renderer>().sharedMaterial = MaterialFactory.Unlit(Color.white);
                Destroy(line.GetComponent<Collider>());
            }
            return go.AddComponent<BrParachuteView>();
        }
    }

    /// <summary>One other real player, driven only by what the server reports. It smooths between snapshots; it never decides anything.</summary>
    public sealed class BrRemoteAvatar : MonoBehaviour
    {
        private ICharacterView view;
        private TextMesh label;
        private BrParachuteView chute;
        private Vector3 targetLocal, velocity;
        private float targetYaw;
        private string state = "";
        private bool inCabin;

        public string PublicId { get; private set; } = "";

        public static BrRemoteAvatar Create(string publicId, string username)
        {
            var go = new GameObject("Player_" + username);
            var a = go.AddComponent<BrRemoteAvatar>();
            a.PublicId = publicId;
            a.view = CharacterViewFactory.Create(go.transform);
            a.view.Apply(new Loadout(), AppServices.Catalog);
            var labelGo = new GameObject("Name");
            labelGo.transform.SetParent(go.transform, false);
            labelGo.transform.localPosition = new Vector3(0f, 2.3f, 0f);
            a.label = labelGo.AddComponent<TextMesh>();
            a.label.text = username; a.label.characterSize = 0.12f; a.label.fontSize = 48; a.label.anchor = TextAnchor.LowerCenter; a.label.alignment = TextAlignment.Center;
            a.label.color = Color.white;
            return a;
        }

        public void Apply(BrPlayerDto p, Transform cabinRoot, BrCabinView cabin)
        {
            bool nowCabin = p.space == "cabin" && p.state == "aircraft";
            if (nowCabin != inCabin || transform.parent == null && nowCabin)
            {
                inCabin = nowCabin;
                transform.SetParent(nowCabin ? cabin.transform : null, false);
                if (!nowCabin) targetLocal = transform.position;
            }
            if (p.visible)
            {
                targetLocal = nowCabin ? new Vector3(p.x, 0.05f, p.z) : new Vector3(p.x, p.y, p.z);
                targetYaw = p.yaw;
            }
            gameObject.SetActive(p.alive && (p.visible || nowCabin));
            if (state != p.state)
            {
                state = p.state;
                bool air = state == "parachute";
                if (air && chute == null) chute = BrParachuteView.Attach(transform);
                if (chute != null) chute.gameObject.SetActive(air);
            }
            if (label != null) label.text = p.username;
        }

        private void Update()
        {
            if (inCabin) transform.localPosition = Vector3.SmoothDamp(transform.localPosition, targetLocal, ref velocity, 0.12f);
            else transform.position = Vector3.SmoothDamp(transform.position, targetLocal, ref velocity, 0.15f);
            // inside the cabin the yaw is cabin-local (the plane itself is rotated); outside it is a world yaw
            var want = Quaternion.Euler(state == "freefall" ? 80f : 0f, targetYaw, 0f);
            float k = 1f - Mathf.Exp(-10f * Time.deltaTime);
            if (inCabin) transform.localRotation = Quaternion.Slerp(transform.localRotation, want, k);
            else transform.rotation = Quaternion.Slerp(transform.rotation, want, k);
            if (label != null && Camera.main != null) label.transform.rotation = Camera.main.transform.rotation;
        }
    }
}
