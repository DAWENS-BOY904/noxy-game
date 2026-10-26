using System;
using System.Collections.Generic;
using System.Text;
using Noxy.Core;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace Noxy.Lobby
{
    /// <summary>Minimap and full map drawn from the island texture the builder produced. North is +Z (up on the map).</summary>
    public sealed class BrMapView : MonoBehaviour, IPointerClickHandler
    {
        public const float MiniWindowMeters = 260f;

        private IslandLayout layout;
        private RawImage image;
        private RectTransform markerLayer, playerMarker, planeMarker, waypointMarker;
        private Image routeLine;
        private readonly List<RectTransform> poiLabels = new List<RectTransform>();
        private Text distanceText;
        private bool expanded;
        private Vector2? waypoint;

        public bool Expanded => expanded;
        public Vector2? Waypoint => waypoint;

        public static BrMapView Create(RectTransform parent, IslandLayout layout, Texture2D texture)
        {
            var root = UIKit.NewRect("BrMap", parent);
            var v = root.gameObject.AddComponent<BrMapView>();
            v.layout = layout;
            var bg = UIKit.NewRounded(root, "Frame", new Color(0.02f, 0.04f, 0.08f, 0.85f), true);
            UIKit.Stretch(bg.rectTransform);
            var maskRect = UIKit.NewRect("Clip", root);
            UIKit.Stretch(maskRect, 4f, 4f, 4f, 4f);
            maskRect.gameObject.AddComponent<RectMask2D>();
            var imgRect = UIKit.NewRect("Image", maskRect);
            UIKit.Stretch(imgRect);
            v.image = imgRect.gameObject.AddComponent<RawImage>();
            v.image.texture = texture;
            v.image.raycastTarget = true;
            v.markerLayer = UIKit.NewRect("Markers", maskRect);
            UIKit.Stretch(v.markerLayer);

            v.routeLine = UIKit.NewImage(v.markerLayer, "Route", new Color(1f, 1f, 1f, 0.5f));
            v.routeLine.rectTransform.pivot = new Vector2(0f, 0.5f);
            v.planeMarker = Dot(v.markerLayer, "Plane", new Color(0.18f, 0.88f, 1f), 16f);
            v.playerMarker = Dot(v.markerLayer, "Player", UITheme.Accent, 14f);
            var heading = UIKit.NewImage(v.playerMarker, "Heading", UITheme.Accent);
            UIKit.Anchor(heading.rectTransform, new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0f), new Vector2(0f, 4f), new Vector2(4f, 14f));
            v.waypointMarker = Dot(v.markerLayer, "Waypoint", UITheme.Warning, 14f);
            v.waypointMarker.gameObject.SetActive(false);

            foreach (var poi in layout.Pois)
            {
                var t = UIKit.NewText(v.markerLayer, poi.Name, 15, new Color(1f, 1f, 1f, 0.95f), TextAnchor.MiddleCenter, FontStyle.Bold, "Poi_" + poi.Id);
                UIKit.Anchor(t.rectTransform, new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), Vector2.zero, new Vector2(150f, 22f));
                var outline = t.gameObject.AddComponent<Outline>(); outline.effectColor = new Color(0f, 0f, 0f, 0.9f);
                v.poiLabels.Add(t.rectTransform);
                t.gameObject.name = "Poi_" + poi.Id;
            }
            v.distanceText = UIKit.NewText(root, "", 18, UITheme.Warning, TextAnchor.LowerCenter, FontStyle.Bold, "WaypointDistance");
            UIKit.Stretch(v.distanceText.rectTransform, 0f, 8f, 0f, 0f);
            return v;
        }

        private static RectTransform Dot(Transform parent, string name, Color c, float size)
        {
            var i = UIKit.NewImage(parent, name, c, UISprites.Circle);
            UIKit.Anchor(i.rectTransform, new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), Vector2.zero, new Vector2(size, size));
            return i.rectTransform;
        }

        public void SetExpanded(bool value) { expanded = value; }

        public void OnPointerClick(PointerEventData e)
        {
            if (!expanded) return;
            if (!RectTransformUtility.ScreenPointToLocalPointInRectangle((RectTransform)image.transform, e.position, e.pressEventCamera, out var local)) return;
            var r = ((RectTransform)image.transform).rect;
            float u = (local.x - r.x) / r.width, v = (local.y - r.y) / r.height;
            if (u < 0f || u > 1f || v < 0f || v > 1f) return;
            var w = MapProjection.ToWorld(layout.WorldSize, u, v);
            waypoint = new Vector2(w.X, w.Z);
        }

        public void ClearWaypoint() { waypoint = null; }

        /// <summary>Places markers. Positions are world metres; the map never decides anything.</summary>
        public void Refresh(Vector3 player, float yawDegrees, Vector3? plane, BrRouteDto route)
        {
            float world = layout.WorldSize;
            // visible window in UV space
            float win = expanded ? 1f : MiniWindowMeters / world;
            float cu = expanded ? 0.5f : player.x / world + 0.5f, cv = expanded ? 0.5f : player.z / world + 0.5f;
            float u0 = cu - win * 0.5f, v0 = cv - win * 0.5f;
            image.uvRect = new Rect(u0, v0, win, win);
            var size = ((RectTransform)image.transform).rect.size;

            Vector2 ToLocal(float x, float z)
            {
                float u = (x / world + 0.5f - u0) / win, v = (z / world + 0.5f - v0) / win;
                return new Vector2((u - 0.5f) * size.x, (v - 0.5f) * size.y);
            }

            playerMarker.anchoredPosition = ToLocal(player.x, player.z);
            playerMarker.localRotation = Quaternion.Euler(0f, 0f, -yawDegrees);

            bool showPlane = plane.HasValue;
            planeMarker.gameObject.SetActive(showPlane);
            routeLine.gameObject.SetActive(showPlane && route != null);
            if (showPlane)
            {
                planeMarker.anchoredPosition = ToLocal(plane.Value.x, plane.Value.z);
                if (route != null)
                {
                    var a = ToLocal(route.ax, route.az); var b = ToLocal(route.bx, route.bz);
                    var d = b - a;
                    routeLine.rectTransform.anchoredPosition = a;
                    routeLine.rectTransform.sizeDelta = new Vector2(d.magnitude, 2f);
                    routeLine.rectTransform.localRotation = Quaternion.Euler(0f, 0f, Mathf.Atan2(d.y, d.x) * Mathf.Rad2Deg);
                }
            }

            for (int i = 0; i < poiLabels.Count; i++)
            {
                var p = layout.Pois[i];
                poiLabels[i].gameObject.SetActive(expanded || Mathf.Abs(p.X - player.x) < MiniWindowMeters * 0.5f && Mathf.Abs(p.Z - player.z) < MiniWindowMeters * 0.5f);
                poiLabels[i].anchoredPosition = ToLocal(p.X, p.Z);
            }

            waypointMarker.gameObject.SetActive(waypoint.HasValue);
            if (waypoint.HasValue)
            {
                waypointMarker.anchoredPosition = ToLocal(waypoint.Value.x, waypoint.Value.y);
                float dx = waypoint.Value.x - player.x, dz = waypoint.Value.y - player.z;
                distanceText.text = Mathf.RoundToInt(Mathf.Sqrt(dx * dx + dz * dz)) + " m to marker";
            }
            else distanceText.text = "";
        }
    }

    /// <summary>All Battle Royale screen furniture: loading, countdown, jump/parachute, vitals, kill feed, maps and results.</summary>
    public sealed class BrHud : MonoBehaviour
    {
        public event Action CancelLoading, ReturnToLobby, JumpClicked, DeployClicked, RetryClicked, PickupClicked, HealClicked, ResumeClicked, LeaveClicked;

        private RectTransform root, hudLayer;
        private GameObject loadingPanel, resultsPanel, pausePanel;
        private Text loadingTitle, loadingMessage, loadingPercent, stageList, loadingTip;
        private Image loadingFill;
        private ButtonView cancelButton, retryButton, loadingBackButton;
        private LoadingProgress progress;

        private Text banner, flightInfo, vitalsText, ammoText, feedText, toastText, resultsText;
        private Image healthFill, armorFill, hitMarker;
        private ButtonView jumpButton, deployButton, pickupButton, healButton;
        private BrMapView miniMap, fullMap;
        private readonly List<string> feed = new List<string>();
        private float toastTimer, hitTimer;
        private int lastFeedHash;

        public BrMapView MiniMap => miniMap;
        public BrMapView FullMap => fullMap;
        public bool FullMapOpen => fullMap != null && fullMap.gameObject.activeSelf;

        public static BrHud Create(RectTransform safeRoot)
        {
            var go = UIKit.NewRect("BattleRoyaleHud", safeRoot);
            UIKit.Stretch(go);
            var h = go.gameObject.AddComponent<BrHud>();
            h.root = go;
            h.hudLayer = UIKit.NewRect("Layer", go);
            UIKit.Stretch(h.hudLayer);
            h.BuildHud();
            h.BuildLoading();
            h.BuildResults();
            h.BuildPause();
            h.hudLayer.gameObject.SetActive(false);
            return h;
        }

        // ---------------------------------------------------------------- loading
        private void BuildLoading()
        {
            loadingPanel = UIKit.NewImage(root, "Loading", new Color(0.02f, 0.04f, 0.08f, 0.98f), null, true).gameObject;
            UIKit.Stretch((RectTransform)loadingPanel.transform);
            loadingTitle = UIKit.NewText(loadingPanel.transform, "BATTLE ROYALE", 44, UITheme.Accent, TextAnchor.MiddleCenter, FontStyle.Bold, "Title");
            UIKit.Anchor(loadingTitle.rectTransform, new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), new Vector2(0f, 190f), new Vector2(900f, 70f));
            UIKit.FitText(loadingTitle, 24);
            loadingMessage = UIKit.NewText(loadingPanel.transform, "", 28, UITheme.Text, TextAnchor.MiddleCenter, FontStyle.Bold, "Message");
            UIKit.Anchor(loadingMessage.rectTransform, new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), new Vector2(0f, 110f), new Vector2(900f, 80f));
            UIKit.FitText(loadingMessage, 16);
            var track = UIKit.NewRounded(loadingPanel.transform, "Track", new Color(1f, 1f, 1f, 0.12f));
            UIKit.Anchor(track.rectTransform, new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), new Vector2(0f, 50f), new Vector2(720f, 18f));
            loadingFill = UIKit.NewRounded(track.transform, "Fill", UITheme.Cyan);
            loadingFill.rectTransform.anchorMin = Vector2.zero; loadingFill.rectTransform.anchorMax = new Vector2(0f, 1f);
            loadingFill.rectTransform.offsetMin = loadingFill.rectTransform.offsetMax = Vector2.zero;
            loadingPercent = UIKit.NewText(loadingPanel.transform, "0%", 26, UITheme.TextDim, TextAnchor.MiddleCenter, FontStyle.Bold, "Percent");
            UIKit.Anchor(loadingPercent.rectTransform, new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), new Vector2(0f, 12f), new Vector2(300f, 40f));
            stageList = UIKit.NewText(loadingPanel.transform, "", 20, UITheme.TextDim, TextAnchor.UpperCenter, FontStyle.Normal, "Stages");
            UIKit.Anchor(stageList.rectTransform, new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), new Vector2(0.5f, 1f), new Vector2(0f, -20f), new Vector2(720f, 150f));
            UIKit.FitText(stageList, 13);
            loadingTip = UIKit.NewText(loadingPanel.transform, "", 18, UITheme.TextDim, TextAnchor.LowerCenter, FontStyle.Italic, "Tip");
            UIKit.Anchor(loadingTip.rectTransform, new Vector2(0.5f, 0f), new Vector2(0.5f, 0f), new Vector2(0.5f, 0f), new Vector2(0f, 20f), new Vector2(900f, 50f));
            UIKit.FitText(loadingTip, 12);
            cancelButton = UIKit.NewButton(loadingPanel.transform, "Cancel", "CANCEL", UITheme.Danger, () => CancelLoading?.Invoke(), 26);
            UIKit.Anchor(cancelButton.Background.rectTransform, new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), new Vector2(0f, -250f), new Vector2(280f, 70f));
            retryButton = UIKit.NewButton(loadingPanel.transform, "Retry", "TRY AGAIN", UITheme.Accent, () => RetryClicked?.Invoke(), 26);
            UIKit.Anchor(retryButton.Background.rectTransform, new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), new Vector2(-150f, -250f), new Vector2(260f, 70f));
            loadingBackButton = UIKit.NewButton(loadingPanel.transform, "Back", "LOBBY", UITheme.Button, () => ReturnToLobby?.Invoke(), 26);
            UIKit.Anchor(loadingBackButton.Background.rectTransform, new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), new Vector2(150f, -250f), new Vector2(260f, 70f));
            retryButton.gameObject.SetActive(false); loadingBackButton.gameObject.SetActive(false);
            loadingTip.text = "Tip: the server decides hits and landings. Your device only proposes positions.";
        }

        public void BindLoading(LoadingProgress p) { progress = p; loadingPanel.SetActive(true); hudLayer.gameObject.SetActive(false); }

        public void HideLoading() { loadingPanel.SetActive(false); hudLayer.gameObject.SetActive(true); }

        public void ShowLoadingFailure(string message)
        {
            loadingPanel.SetActive(true);
            loadingMessage.text = message; loadingMessage.color = UITheme.Danger;
            cancelButton.gameObject.SetActive(false);
            retryButton.gameObject.SetActive(true); loadingBackButton.gameObject.SetActive(true);
        }

        private void UpdateLoading()
        {
            if (progress == null || !loadingPanel.activeSelf) return;
            if (!progress.Failed)
            {
                loadingMessage.color = progress.Slow ? UITheme.Warning : UITheme.Text;
                loadingMessage.text = progress.Message;
            }
            loadingFill.rectTransform.anchorMax = new Vector2(progress.Overall, 1f);
            loadingPercent.text = progress.Percent + "%";
            var sb = new StringBuilder();
            for (int i = 0; i < 5; i++)
            {
                var s = (LoadStage)i; float p = progress.StageProgress(s);
                sb.Append(p >= 1f ? "<color=#5CDB85>done</color>  " : (progress.Current == s && !progress.Failed ? "<color=#FFB11F>" + Mathf.RoundToInt(p * 100f) + "%</color>  " : "...  "));
                sb.AppendLine(LoadingProgress.StageMessage(s));
            }
            stageList.text = sb.ToString();
        }

        // ---------------------------------------------------------------- in-match HUD
        private void BuildHud()
        {
            banner = UIKit.NewText(hudLayer, "", 40, UITheme.Text, TextAnchor.MiddleCenter, FontStyle.Bold, "Banner");
            UIKit.Anchor(banner.rectTransform, new Vector2(0.5f, 1f), new Vector2(0.5f, 1f), new Vector2(0.5f, 1f), new Vector2(0f, -90f), new Vector2(900f, 90f));
            UIKit.FitText(banner, 20);
            var o = banner.gameObject.AddComponent<Outline>(); o.effectColor = new Color(0f, 0f, 0f, 0.9f);

            flightInfo = UIKit.NewText(hudLayer, "", 24, UITheme.Cyan, TextAnchor.MiddleCenter, FontStyle.Bold, "FlightInfo");
            UIKit.Anchor(flightInfo.rectTransform, new Vector2(0.5f, 0f), new Vector2(0.5f, 0f), new Vector2(0.5f, 0f), new Vector2(0f, 250f), new Vector2(700f, 44f));
            UIKit.FitText(flightInfo, 14);

            jumpButton = UIKit.NewButton(hudLayer, "Jump", "JUMP", UITheme.Accent, () => JumpClicked?.Invoke(), 40);
            UIKit.Anchor(jumpButton.Background.rectTransform, new Vector2(0.5f, 0f), new Vector2(0.5f, 0f), new Vector2(0.5f, 0f), new Vector2(0f, 120f), new Vector2(360f, 100f));
            deployButton = UIKit.NewButton(hudLayer, "Deploy", "PARACHUTE", UITheme.Cyan, () => DeployClicked?.Invoke(), 32);
            UIKit.Anchor(deployButton.Background.rectTransform, new Vector2(0.5f, 0f), new Vector2(0.5f, 0f), new Vector2(0.5f, 0f), new Vector2(0f, 120f), new Vector2(360f, 90f));
            jumpButton.gameObject.SetActive(false); deployButton.gameObject.SetActive(false);

            pickupButton = UIKit.NewButton(hudLayer, "Pickup", "PICK UP", UITheme.Success, () => PickupClicked?.Invoke(), 28);
            UIKit.Anchor(pickupButton.Background.rectTransform, new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), new Vector2(0f, -60f), new Vector2(340f, 72f));
            pickupButton.gameObject.SetActive(false);
            healButton = UIKit.NewButton(hudLayer, "Heal", "MEDKIT", UITheme.Button, () => HealClicked?.Invoke(), 22);
            UIKit.Anchor(healButton.Background.rectTransform, new Vector2(0.5f, 0f), new Vector2(0.5f, 0f), new Vector2(0f, 0f), new Vector2(226f, 14f), new Vector2(150f, 56f));
            healButton.gameObject.SetActive(false);

            var vitals = UIKit.NewRounded(hudLayer, "Vitals", new Color(0.03f, 0.05f, 0.09f, 0.7f));
            UIKit.Anchor(vitals.rectTransform, new Vector2(0.5f, 0f), new Vector2(0.5f, 0f), new Vector2(0.5f, 0f), new Vector2(0f, 14f), new Vector2(420f, 84f));
            var hTrack = UIKit.NewRounded(vitals.transform, "HealthTrack", new Color(1f, 1f, 1f, 0.12f));
            UIKit.Anchor(hTrack.rectTransform, new Vector2(0f, 1f), new Vector2(1f, 1f), new Vector2(0.5f, 1f), new Vector2(0f, -10f), new Vector2(-24f, 16f));
            healthFill = UIKit.NewRounded(hTrack.transform, "Fill", UITheme.Success);
            healthFill.rectTransform.anchorMin = Vector2.zero; healthFill.rectTransform.anchorMax = Vector2.one; healthFill.rectTransform.offsetMin = healthFill.rectTransform.offsetMax = Vector2.zero;
            var aTrack = UIKit.NewRounded(vitals.transform, "ArmorTrack", new Color(1f, 1f, 1f, 0.12f));
            UIKit.Anchor(aTrack.rectTransform, new Vector2(0f, 1f), new Vector2(1f, 1f), new Vector2(0.5f, 1f), new Vector2(0f, -30f), new Vector2(-24f, 8f));
            armorFill = UIKit.NewRounded(aTrack.transform, "Fill", UITheme.Cyan);
            armorFill.rectTransform.anchorMin = Vector2.zero; armorFill.rectTransform.anchorMax = new Vector2(0f, 1f); armorFill.rectTransform.offsetMin = armorFill.rectTransform.offsetMax = Vector2.zero;
            vitalsText = UIKit.NewText(vitals.transform, "", 22, UITheme.Text, TextAnchor.LowerLeft, FontStyle.Bold, "VitalsText");
            UIKit.Stretch(vitalsText.rectTransform, 14f, 6f, 150f, 36f);
            ammoText = UIKit.NewText(vitals.transform, "", 30, UITheme.Accent, TextAnchor.LowerRight, FontStyle.Bold, "Ammo");
            UIKit.Stretch(ammoText.rectTransform, 150f, 6f, 14f, 36f);
            UIKit.FitText(vitalsText, 12); UIKit.FitText(ammoText, 14);

            feedText = UIKit.NewText(hudLayer, "", 20, UITheme.Text, TextAnchor.UpperLeft, FontStyle.Bold, "KillFeed");
            UIKit.Anchor(feedText.rectTransform, new Vector2(0f, 1f), new Vector2(0f, 1f), new Vector2(0f, 1f), new Vector2(14f, -190f), new Vector2(480f, 160f));
            var fo = feedText.gameObject.AddComponent<Outline>(); fo.effectColor = new Color(0f, 0f, 0f, 0.9f);
            UIKit.FitText(feedText, 12);

            toastText = UIKit.NewText(hudLayer, "", UITheme.FontBody, UITheme.Warning, TextAnchor.MiddleCenter, FontStyle.Bold, "Toast");
            UIKit.Anchor(toastText.rectTransform, new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), new Vector2(0f, -120f), new Vector2(800f, 50f));
            var to = toastText.gameObject.AddComponent<Outline>(); to.effectColor = new Color(0f, 0f, 0f, 0.9f);

            hitMarker = UIKit.NewImage(hudLayer, "HitMarker", new Color(1f, 0.3f, 0.3f, 0f), UISprites.Circle);
            UIKit.Anchor(hitMarker.rectTransform, new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), Vector2.zero, new Vector2(34f, 34f));
            var cross = UIKit.NewImage(hudLayer, "Crosshair", new Color(1f, 1f, 1f, 0.85f), UISprites.Circle);
            UIKit.Anchor(cross.rectTransform, new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), Vector2.zero, new Vector2(5f, 5f));
        }

        public void BindMaps(IslandLayout layout, Texture2D texture)
        {
            miniMap = BrMapView.Create(hudLayer, layout, texture);
            UIKit.Anchor(miniMap.GetComponent<RectTransform>(), new Vector2(1f, 1f), new Vector2(1f, 1f), new Vector2(1f, 1f), new Vector2(-14f, -90f), new Vector2(210f, 210f));
            fullMap = BrMapView.Create(hudLayer, layout, texture);
            var fr = fullMap.GetComponent<RectTransform>();
            UIKit.Anchor(fr, new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), Vector2.zero, new Vector2(640f, 640f));
            fullMap.SetExpanded(true);
            fullMap.gameObject.SetActive(false);
        }

        /// <summary>Sizes the full map to the safe area so it never runs under notches or off small screens.</summary>
        public void FitFullMap(Vector2 safeSize)
        {
            if (fullMap == null) return;
            float side = Mathf.Max(220f, Mathf.Min(safeSize.x - 40f, safeSize.y - 140f));
            ((RectTransform)fullMap.transform).sizeDelta = new Vector2(side, side);
        }

        public void ToggleFullMap() { if (fullMap != null) fullMap.gameObject.SetActive(!fullMap.gameObject.activeSelf); }
        public void CloseFullMap() { if (fullMap != null) fullMap.gameObject.SetActive(false); }

        public void RefreshMaps(Vector3 player, float yaw, Vector3? plane, BrRouteDto route)
        {
            if (miniMap != null) miniMap.Refresh(player, yaw, plane, route);
            if (fullMap != null && fullMap.gameObject.activeSelf) fullMap.Refresh(player, yaw, plane, route);
        }

        public void SetPickup(string label, bool visible)
        {
            if (pickupButton == null) return;
            pickupButton.gameObject.SetActive(visible);
            if (visible) pickupButton.SetLabel(label);
        }
        public void SetHealVisible(bool visible) { if (healButton != null) healButton.gameObject.SetActive(visible); }

        public void SetBanner(string text) { if (banner != null) banner.text = text ?? ""; }
        public void SetFlightInfo(string text) { if (flightInfo != null) flightInfo.text = text ?? ""; }
        public void SetJump(bool visible) { if (jumpButton != null) jumpButton.gameObject.SetActive(visible); }
        public void SetDeploy(bool visible, bool enabled)
        {
            if (deployButton == null) return;
            deployButton.gameObject.SetActive(visible);
            deployButton.Background.color = enabled ? UITheme.Cyan : UITheme.Disabled;
        }

        public void SetVitals(BrYouDto you, string weaponLine, string ammoLine)
        {
            float hp = Mathf.Clamp01(you.health / 100f);
            healthFill.rectTransform.anchorMax = new Vector2(hp, 1f);
            healthFill.color = hp > 0.5f ? UITheme.Success : hp > 0.25f ? UITheme.Warning : UITheme.Danger;
            armorFill.rectTransform.anchorMax = new Vector2(Mathf.Clamp01(you.armor / 100f), 1f);
            vitalsText.text = "HP " + you.health + "  AR " + you.armor + "  MED " + you.medkits + "\n" + weaponLine;
            ammoText.text = ammoLine;
        }

        public void ShowHit(bool head) { hitTimer = 0.2f; hitMarker.color = head ? new Color(1f, 0.85f, 0.2f, 0.9f) : new Color(1f, 0.3f, 0.3f, 0.9f); }

        public void AddFeed(string line)
        {
            feed.Add(line);
            while (feed.Count > 5) feed.RemoveAt(0);
            feedText.text = string.Join("\n", feed.ToArray());
            lastFeedHash = Time.frameCount;
        }

        public void Toast(string message, float seconds = 3f) { toastText.text = message; toastTimer = seconds; }

        // ---------------------------------------------------------------- results
        private void BuildResults()
        {
            resultsPanel = UIKit.NewImage(root, "Results", new Color(0.02f, 0.04f, 0.08f, 0.94f), null, true).gameObject;
            UIKit.Stretch((RectTransform)resultsPanel.transform);
            resultsText = UIKit.NewText(resultsPanel.transform, "", 30, UITheme.Text, TextAnchor.MiddleCenter, FontStyle.Bold, "Text");
            UIKit.Anchor(resultsText.rectTransform, new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), new Vector2(0f, 40f), new Vector2(900f, 560f));
            UIKit.FitText(resultsText, 14);
            var back = UIKit.NewButton(resultsPanel.transform, "ToLobby", "BACK TO LOBBY", UITheme.Accent, () => ReturnToLobby?.Invoke(), 30);
            UIKit.Anchor(back.Background.rectTransform, new Vector2(0.5f, 0f), new Vector2(0.5f, 0f), new Vector2(0.5f, 0f), new Vector2(0f, 40f), new Vector2(380f, 80f));
            resultsPanel.SetActive(false);
        }

        private void BuildPause()
        {
            pausePanel = UIKit.NewImage(root, "Pause", new Color(0.02f, 0.04f, 0.08f, 0.88f), null, true).gameObject;
            UIKit.Stretch((RectTransform)pausePanel.transform);
            var t = UIKit.NewText(pausePanel.transform, "PAUSED\nThe match keeps running on the server. Leaving now counts as abandoning it.", 28, UITheme.Text, TextAnchor.MiddleCenter, FontStyle.Bold, "Text");
            UIKit.Anchor(t.rectTransform, new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), new Vector2(0f, 90f), new Vector2(860f, 200f));
            UIKit.FitText(t, 14);
            var resume = UIKit.NewButton(pausePanel.transform, "Resume", "RESUME", UITheme.Accent, () => ResumeClicked?.Invoke(), 30);
            UIKit.Anchor(resume.Background.rectTransform, new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), new Vector2(-170f, -90f), new Vector2(320f, 80f));
            var leave = UIKit.NewButton(pausePanel.transform, "Leave", "LEAVE MATCH", UITheme.Danger, () => LeaveClicked?.Invoke(), 30);
            UIKit.Anchor(leave.Background.rectTransform, new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), new Vector2(170f, -90f), new Vector2(320f, 80f));
            pausePanel.SetActive(false);
        }

        public void ShowPause(bool visible) { if (pausePanel != null) pausePanel.SetActive(visible); }

        public void ShowResults(string text)
        {
            resultsText.text = text;
            resultsPanel.SetActive(true);
        }

        public bool ResultsVisible => resultsPanel != null && resultsPanel.activeSelf;

        private void Update()
        {
            UpdateLoading();
            if (toastTimer > 0f) { toastTimer -= Time.unscaledDeltaTime; if (toastTimer <= 0f) toastText.text = ""; }
            if (hitTimer > 0f)
            {
                hitTimer -= Time.unscaledDeltaTime;
                if (hitTimer <= 0f) hitMarker.color = new Color(1f, 0.3f, 0.3f, 0f);
            }
        }
    }
}
