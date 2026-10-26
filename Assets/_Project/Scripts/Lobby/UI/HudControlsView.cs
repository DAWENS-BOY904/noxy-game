using System;
using System.Collections.Generic;
using Noxy.Core;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace Noxy.Lobby
{
    /// <summary>Touch-control state read by gameplay each frame. Written by HudControlsView in play mode.</summary>
    public sealed class TouchControlState
    {
        public Vector2 Move;
        public bool Fire, Aim, Sprint, CrouchToggled;
        public bool JumpPressed, ReloadPressed, MapPressed;
        public readonly bool[] SlotPressed = new bool[3];

        private Vector2 lookPixels;

        public void AddLook(Vector2 pixels) => lookPixels += pixels;

        public Vector2 ConsumeLook()
        {
            var v = lookPixels;
            lookPixels = Vector2.zero;
            return v;
        }

        public void ClearPresses()
        {
            JumpPressed = ReloadPressed = false;
            SlotPressed[0] = SlotPressed[1] = SlotPressed[2] = false;
        }

        /// <summary>Map is consumed by the HUD owner (not the player controller), so it is not cleared by ClearPresses.</summary>
        public bool ConsumeMap()
        {
            bool v = MapPressed;
            MapPressed = false;
            return v;
        }
    }

    /// <summary>
    /// Builds the on-screen controls from a HudLayout. EditMode: controls are draggable previews (nothing triggers
    /// gameplay). Play mode: controls drive TouchControlState. Positions are normalized to the safe area.
    /// </summary>
    public sealed class HudControlsView : MonoBehaviour
    {
        private sealed class Entry
        {
            public HudElementId Id;
            public RectTransform Rect;
            public Image Background;
            public Image Outline;
            public Text Label;
            public RectTransform Knob;
            public CanvasGroup Group;
        }

        private readonly Dictionary<HudElementId, Entry> entries = new Dictionary<HudElementId, Entry>();
        private RectTransform safeRoot;
        private Vector2 safeSize = new Vector2(1600f, 900f);

        public bool EditMode { get; private set; }
        public HudLayout Layout { get; private set; }
        public TouchControlState State { get; } = new TouchControlState();
        public HudElementId? SelectedId { get; private set; }

        public event Action<HudElementId?> SelectionChanged;
        public event Action LayoutEdited;

        public static HudControlsView Create(RectTransform parent, HudLayout layout, bool editMode)
        {
            var rt = UIKit.NewRect("HudControls", parent);
            UIKit.Stretch(rt);
            var view = rt.gameObject.AddComponent<HudControlsView>();
            view.safeRoot = rt;
            view.EditMode = editMode;
            view.Layout = layout;
            view.Build();
            return view;
        }

        public RectTransform GetRect(HudElementId id) => entries.TryGetValue(id, out var e) ? e.Rect : null;

        private void Build()
        {
            foreach (HudElementId id in Enum.GetValues(typeof(HudElementId)))
            {
                if (!HudElementInfo.IsSupported(id)) continue;
                entries[id] = BuildEntry(id);
            }
            // The look area must sit behind the buttons so they win touches.
            if (entries.TryGetValue(HudElementId.Look, out var look)) look.Rect.SetAsFirstSibling();
            Relayout(safeSize);
        }

        private Entry BuildEntry(HudElementId id)
        {
            var e = new Entry { Id = id };
            bool circle = id != HudElementId.Look && id != HudElementId.Minimap;
            var bg = circle ? UIKit.NewImage(transform, "HUD_" + id, new Color(0.1f, 0.14f, 0.2f, 0.8f), UISprites.Circle, true)
                            : UIKit.NewRounded(transform, "HUD_" + id, new Color(0.1f, 0.14f, 0.2f, 0.8f), true);
            e.Rect = bg.rectTransform;
            e.Background = bg;
            e.Group = bg.gameObject.AddComponent<CanvasGroup>();

            if (id == HudElementId.Fire) bg.color = new Color(0.9f, 0.4f, 0.1f, 0.85f);
            if (id == HudElementId.Look) bg.color = EditMode ? new Color(0.2f, 0.7f, 1f, 0.12f) : new Color(0f, 0f, 0f, 0f);
            if (id == HudElementId.Minimap) bg.color = EditMode ? new Color(0.05f, 0.2f, 0.25f, 0.7f) : new Color(0f, 0f, 0f, 0f);
            bg.raycastTarget = !(id == HudElementId.Minimap && !EditMode);

            var outline = UIKit.NewImage(e.Rect, "Outline", UITheme.Accent, circle ? UISprites.Circle : UISprites.Rounded);
            UIKit.Stretch(outline.rectTransform, -6f, -6f, -6f, -6f);
            outline.transform.SetAsFirstSibling();
            outline.gameObject.SetActive(false);
            e.Outline = outline;

            string label = id == HudElementId.Look ? (EditMode ? "LOOK AREA" : "") : HudElementInfo.Label(id);
            if (id == HudElementId.Minimap && !EditMode) label = "";
            e.Label = UIKit.NewText(e.Rect, label, id == HudElementId.MoveStick ? 26 : 24, UITheme.Text, TextAnchor.MiddleCenter, FontStyle.Bold, "Label");
            UIKit.FitText(e.Label, 14);

            if (id == HudElementId.MoveStick)
            {
                var knob = UIKit.NewImage(e.Rect, "Knob", new Color(1f, 1f, 1f, 0.35f), UISprites.Circle);
                knob.rectTransform.sizeDelta = new Vector2(100f, 100f);
                e.Knob = knob.rectTransform;
            }

            var handle = bg.gameObject.AddComponent<HudHandle>();
            handle.Init(this, e.Id, e.Rect);
            return e;
        }

        /// <summary>Applies sizes/positions/opacity for the current layout. Call again whenever the safe area changes.</summary>
        public void Relayout(Vector2 newSafeSize)
        {
            safeSize = newSafeSize;
            Layout.Sanitize(safeSize);
            foreach (var kv in entries)
            {
                var s = Layout.Get(kv.Key);
                var e = kv.Value;
                if (s == null) continue;

                Vector2 size = HudElementInfo.BaseSize(kv.Key) * s.scale;
                e.Rect.anchorMin = e.Rect.anchorMax = new Vector2(s.x, s.y);
                e.Rect.pivot = new Vector2(0.5f, 0.5f);
                e.Rect.sizeDelta = size;
                e.Rect.anchoredPosition = Vector2.zero;

                float alpha = kv.Key == HudElementId.Look || kv.Key == HudElementId.Minimap ? 1f : s.opacity;
                if (e.Group == null) e.Group = e.Rect.gameObject.AddComponent<CanvasGroup>();
                e.Group.alpha = alpha;
                e.Outline.gameObject.SetActive(EditMode && SelectedId == kv.Key);
            }
        }

        public void SetLayout(HudLayout layout) => SetLayout(layout, safeSize);

        /// <summary>Use this overload when the safe area just changed (e.g. orientation) so the new layout is clamped against the right size.</summary>
        public void SetLayout(HudLayout layout, Vector2 newSafeSize)
        {
            Layout = layout;
            Relayout(newSafeSize);
        }

        public void SetSelected(HudElementId? id)
        {
            SelectedId = id;
            foreach (var kv in entries) kv.Value.Outline.gameObject.SetActive(EditMode && id == kv.Key);
            SelectionChanged?.Invoke(id);
        }

        /// <summary>Called by handles while dragging in edit mode.</summary>
        internal void MoveElement(HudElementId id, Vector2 screenPoint, Camera eventCamera)
        {
            if (!EditMode || !HudElementInfo.IsMovable(id)) return;
            if (!RectTransformUtility.ScreenPointToLocalPointInRectangle(safeRoot, screenPoint, eventCamera, out Vector2 local)) return;

            var rect = safeRoot.rect;
            var s = Layout.Get(id);
            s.x = (local.x - rect.xMin) / Mathf.Max(1f, rect.width);
            s.y = (local.y - rect.yMin) / Mathf.Max(1f, rect.height);
            HudLayout.ClampInside(s, id, safeSize);
            Relayout(safeSize);
            LayoutEdited?.Invoke();
        }

        internal void EditorTouch(HudElementId id) => SetSelected(id);

        internal void Joystick(Vector2 normalized)
        {
            State.Move = normalized;
            if (entries.TryGetValue(HudElementId.MoveStick, out var e) && e.Knob != null)
                e.Knob.anchoredPosition = normalized * (e.Rect.rect.width * 0.35f);
        }

        internal void Button(HudElementId id, bool down)
        {
            switch (id)
            {
                case HudElementId.Fire: State.Fire = down; break;
                case HudElementId.Aim: State.Aim = down; break;
                case HudElementId.Sprint: State.Sprint = down; break;
                case HudElementId.Crouch: if (down) State.CrouchToggled = !State.CrouchToggled; break;
                case HudElementId.Jump: if (down) State.JumpPressed = true; break;
                case HudElementId.Reload: if (down) State.ReloadPressed = true; break;
                case HudElementId.Map: if (down) State.MapPressed = true; break;
                case HudElementId.Slot1: if (down) State.SlotPressed[0] = true; break;
                case HudElementId.Slot2: if (down) State.SlotPressed[1] = true; break;
                case HudElementId.Slot3: if (down) State.SlotPressed[2] = true; break;
            }
        }

        /// <summary>Highlights a button as active (e.g. selected weapon slot, crouch toggled).</summary>
        public void SetHighlight(HudElementId id, bool on)
        {
            if (!entries.TryGetValue(id, out var e) || id == HudElementId.Fire) return;
            e.Background.color = on ? new Color(0.9f, 0.45f, 0.1f, 0.9f) : new Color(0.1f, 0.14f, 0.2f, 0.8f);
        }

        public void SetLabel(HudElementId id, string text)
        {
            if (entries.TryGetValue(id, out var e)) e.Label.text = text;
        }
    }

    /// <summary>Pointer handler attached to every control. Behaves as a drag handle in edit mode and as input in play mode.</summary>
    public sealed class HudHandle : MonoBehaviour, IPointerDownHandler, IPointerUpHandler, IDragHandler
    {
        private HudControlsView view;
        private HudElementId id;
        private RectTransform rect;

        public void Init(HudControlsView owner, HudElementId elementId, RectTransform r)
        {
            view = owner;
            id = elementId;
            rect = r;
        }

        public void OnPointerDown(PointerEventData e)
        {
            if (view.EditMode) { view.EditorTouch(id); return; }
            if (id == HudElementId.MoveStick) UpdateStick(e);
            else if (id != HudElementId.Look && id != HudElementId.Minimap) view.Button(id, true);
        }

        public void OnPointerUp(PointerEventData e)
        {
            if (view.EditMode) return;
            if (id == HudElementId.MoveStick) view.Joystick(Vector2.zero);
            else if (id != HudElementId.Look && id != HudElementId.Minimap) view.Button(id, false);
        }

        public void OnDrag(PointerEventData e)
        {
            if (view.EditMode) { view.MoveElement(id, e.position, e.pressEventCamera); return; }
            if (id == HudElementId.MoveStick) UpdateStick(e);
            else if (id == HudElementId.Look) view.State.AddLook(e.delta);
        }

        private void UpdateStick(PointerEventData e)
        {
            if (!RectTransformUtility.ScreenPointToLocalPointInRectangle(rect, e.position, e.pressEventCamera, out Vector2 local)) return;
            float radius = Mathf.Max(1f, rect.rect.width * 0.5f);
            view.Joystick(Vector2.ClampMagnitude(local / radius, 1f));
        }

        private void OnDisable()
        {
            if (view != null && !view.EditMode)
            {
                if (id == HudElementId.MoveStick) view.Joystick(Vector2.zero);
                else if (id != HudElementId.Look && id != HudElementId.Minimap) view.Button(id, false);
            }
        }
    }
}
