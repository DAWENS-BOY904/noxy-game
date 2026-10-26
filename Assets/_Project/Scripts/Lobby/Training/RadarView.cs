using System;
using System.Collections.Generic;
using Noxy.Core;
using UnityEngine;
using UnityEngine.UI;

namespace Noxy.Lobby
{
    public struct RadarMarker
    {
        public Vector3 Position;
        public RadarMarkerKind Kind;
    }

    /// <summary>
    /// Circular radar. Rotates with the player or stays north-up (Settings > Gameplay), can expand, and only draws
    /// marker kinds that RadarPolicy allows for the current mode. Marker images are pooled, so updates allocate nothing.
    /// </summary>
    public sealed class RadarView : MonoBehaviour
    {
        private const int MaxBlips = 32;

        private RectTransform root, field;
        private Image arrowBody, arrowNose;
        private Text northLabel;
        private readonly List<Image> pool = new List<Image>(MaxBlips);
        private readonly List<RadarMarker> scratch = new List<RadarMarker>(MaxBlips);
        private Func<List<RadarMarker>, int> fill;
        private Transform player;
        private string modeId = GameModeIds.Training;
        private bool expanded;

        public bool Expanded => expanded;

        public static RadarView Create(RectTransform parent)
        {
            var rt = UIKit.NewRect("Radar", parent);
            var view = rt.gameObject.AddComponent<RadarView>();
            view.Build(rt);
            return view;
        }

        public void Bind(Transform playerTransform, string mode, Func<List<RadarMarker>, int> markerSource)
        {
            player = playerTransform;
            modeId = mode;
            fill = markerSource;
        }

        public void ToggleExpanded() => SetExpanded(!expanded);

        public void SetExpanded(bool value)
        {
            expanded = value;
            var host = root.parent as RectTransform;
            // Expanded: draw bigger and above other HUD pieces; collapsed: fill the host rect again.
            root.localScale = Vector3.one * (value ? 2f : 1f);
            root.SetAsLastSibling();
            if (host != null) host.SetAsLastSibling();
        }

        /// <summary>Fills the given host rect (the HUD layout's Minimap element on touch devices).</summary>
        public void AttachTo(RectTransform host)
        {
            root.SetParent(host, false);
            UIKit.Stretch(root);
            root.localScale = Vector3.one * (expanded ? 2f : 1f);
        }

        /// <summary>Places the radar in a screen corner of the safe area (PC, where there are no touch controls).</summary>
        public void PlaceInCorner(RectTransform safeRoot, RadarCorner corner, float sizeScale)
        {
            root.SetParent(safeRoot, false);
            float size = 240f * sizeScale;
            bool right = corner == RadarCorner.TopRight || corner == RadarCorner.BottomRight;
            bool top = corner == RadarCorner.TopLeft || corner == RadarCorner.TopRight;
            var anchor = new Vector2(right ? 1f : 0f, top ? 1f : 0f);
            UIKit.Anchor(root, anchor, anchor, anchor, new Vector2(right ? -20f : 20f, top ? -20f : 20f), new Vector2(size, size));
            root.localScale = Vector3.one * (expanded ? 2f : 1f);
        }

        private void Build(RectTransform rt)
        {
            root = rt;
            var ring = UIKit.NewImage(root, "Ring", new Color(1f, 0.56f, 0.12f, 0.9f), UISprites.Circle);
            UIKit.Stretch(ring.rectTransform);
            var back = UIKit.NewImage(root, "Back", new Color(0.03f, 0.06f, 0.1f, 0.82f), UISprites.Circle);
            UIKit.Stretch(back.rectTransform, 4f, 4f, 4f, 4f);

            // Cross-hair lines give the radar a sense of scale.
            var h = UIKit.NewImage(root, "LineH", new Color(1f, 1f, 1f, 0.08f));
            UIKit.Anchor(h.rectTransform, new Vector2(0.1f, 0.5f), new Vector2(0.9f, 0.5f), new Vector2(0.5f, 0.5f), Vector2.zero, new Vector2(0f, 2f));
            var v = UIKit.NewImage(root, "LineV", new Color(1f, 1f, 1f, 0.08f));
            UIKit.Anchor(v.rectTransform, new Vector2(0.5f, 0.1f), new Vector2(0.5f, 0.9f), new Vector2(0.5f, 0.5f), Vector2.zero, new Vector2(2f, 0f));

            field = UIKit.NewRect("Field", root);
            UIKit.Stretch(field, 12f, 12f, 12f, 12f);

            for (int i = 0; i < MaxBlips; i++)
            {
                var blip = UIKit.NewImage(field, "Blip" + i, Color.white, UISprites.Circle);
                blip.rectTransform.sizeDelta = new Vector2(16f, 16f);
                blip.gameObject.SetActive(false);
                pool.Add(blip);
            }

            arrowBody = UIKit.NewImage(field, "Player", UITheme.Accent, UISprites.Circle);
            arrowBody.rectTransform.sizeDelta = new Vector2(18f, 18f);
            arrowNose = UIKit.NewImage(arrowBody.rectTransform, "Nose", Color.white, UISprites.Circle);
            UIKit.Anchor(arrowNose.rectTransform, new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), new Vector2(0f, 14f), new Vector2(9f, 12f));

            northLabel = UIKit.NewText(field, "N", 22, UITheme.Text, TextAnchor.MiddleCenter, FontStyle.Bold, "North");
            northLabel.rectTransform.sizeDelta = new Vector2(30f, 30f);
            northLabel.raycastTarget = false;
        }

        private void LateUpdate()
        {
            if (player == null || fill == null) return;

            bool northUp = AppServices.IsInitialized && AppServices.Config.Settings.radarNorthUp;
            float yaw = player.eulerAngles.y;
            float range = RadarPolicy.Range(modeId, expanded);
            float radius = Mathf.Max(1f, field.rect.width * 0.5f - 8f);

            scratch.Clear();
            int count = Mathf.Min(fill(scratch), MaxBlips);

            int used = 0;
            for (int i = 0; i < count && used < MaxBlips; i++)
            {
                var m = scratch[i];
                if (!RadarPolicy.Allows(modeId, m.Kind)) continue; // enforced here too, not only by the data source

                var b = RadarMath.ToRadar(m.Position.x - player.position.x, m.Position.z - player.position.z, yaw, range, northUp);
                var img = pool[used++];
                img.gameObject.SetActive(true);
                img.rectTransform.anchoredPosition = new Vector2(b.X, b.Y) * radius;
                Style(img, m.Kind, b.InRange);
            }
            for (int i = used; i < pool.Count; i++) pool[i].gameObject.SetActive(false);

            arrowBody.rectTransform.localRotation = Quaternion.Euler(0f, 0f, -RadarMath.PlayerArrowAngle(yaw, northUp));

            // North marker sits on the rim in the direction of north.
            var n = RadarMath.ToRadar(0f, 1f, yaw, 1f, northUp);
            northLabel.rectTransform.anchoredPosition = new Vector2(n.X, n.Y) * (radius + 2f);
        }

        private static void Style(Image img, RadarMarkerKind kind, bool inRange)
        {
            float size = 16f;
            Color color;
            float rotation = 0f;
            switch (kind)
            {
                case RadarMarkerKind.Teammate: color = new Color(0.36f, 0.86f, 0.52f); size = 18f; rotation = 45f; break;
                case RadarMarkerKind.Objective: color = UITheme.Cyan; size = 20f; rotation = 45f; break;
                case RadarMarkerKind.Mission: color = UITheme.Warning; size = 18f; break;
                case RadarMarkerKind.SafeZone: color = new Color(0.4f, 0.7f, 1f); size = 18f; break;
                default: color = new Color(0.96f, 0.32f, 0.34f); break;
            }
            if (!inRange) { color.a = 0.55f; size *= 0.75f; }
            img.color = color;
            img.rectTransform.sizeDelta = new Vector2(size, size);
            img.rectTransform.localRotation = Quaternion.Euler(0f, 0f, rotation);
        }
    }
}
