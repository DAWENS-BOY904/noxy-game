using System;
using Noxy.Core;
using UnityEngine;
using UnityEngine.UI;

namespace Noxy.Lobby
{
    /// <summary>
    /// Drag, resize and fade the on-screen controls for the current orientation. Editing never triggers gameplay:
    /// controls are inert previews. Saved locally immediately and mirrored to the account when signed in.
    /// </summary>
    public sealed class HudEditorScreen : LobbyScreen
    {
        private HudControlsView editView, previewView;
        private HudLayout layout, original;
        private RectTransform toolbar, inspector;
        private Text hint, selectedLabel;
        private Slider scaleSlider, opacitySlider;
        private ButtonView previewButton, saveButton;
        private bool previewing;
        private bool suppressSlider;

        public override LobbyScreenId Id => LobbyScreenId.HudEditor;
        public override ScreenStyle Style => ScreenStyle.Hud;
        public override string Title => "HUD Editor";
        public override bool InspectMode => false;

        protected override void BuildContent(RectTransform parent)
        {
            var backdrop = UIKit.NewImage(parent, "Backdrop", new Color(0.02f, 0.03f, 0.06f, 0.88f), null, true);
            UIKit.Stretch(backdrop.rectTransform);

            layout = HudLayout.CreateDefault(false);
            editView = HudControlsView.Create(parent, layout, true);
            previewView = HudControlsView.Create(parent, layout, false);
            previewView.gameObject.SetActive(false);
            editView.SelectionChanged += OnSelectionChanged;
            editView.LayoutEdited += RefreshState;

            BuildToolbar(parent);
            BuildInspector(parent);
        }

        private void BuildToolbar(RectTransform parent)
        {
            var bar = UIKit.NewRounded(parent, "Toolbar", UITheme.Panel, true);
            toolbar = bar.rectTransform;
            UIKit.Anchor(toolbar, new Vector2(0.5f, 1f), new Vector2(0.5f, 1f), new Vector2(0.5f, 1f), new Vector2(0f, -8f), new Vector2(900f, 76f));
            var row = UIKit.NewRect("Row", toolbar);
            UIKit.Stretch(row, 8f, 6f, 8f, 6f);
            UIKit.HStack(row.gameObject, 8f).childForceExpandWidth = true;

            var back = UIKit.NewButton(row, "Back", "BACK", UITheme.Button, () => Ctx.Screens.Back(), 22);
            UIKit.SetLayout(back.gameObject, minWidth: 0f, flexibleWidth: 1f);
            var reset = UIKit.NewButton(row, "Reset", "DEFAULTS", UITheme.Button, ResetToDefaults, 22);
            UIKit.SetLayout(reset.gameObject, minWidth: 0f, flexibleWidth: 1.2f);
            previewButton = UIKit.NewButton(row, "Preview", "PREVIEW", UITheme.Button, TogglePreview, 22);
            UIKit.SetLayout(previewButton.gameObject, minWidth: 0f, flexibleWidth: 1.2f);
            saveButton = UIKit.NewButton(row, "Save", "SAVE", UITheme.Accent, Save, 24);
            UIKit.SetLayout(saveButton.gameObject, minWidth: 0f, flexibleWidth: 1.2f);
        }

        private void BuildInspector(RectTransform parent)
        {
            var panel = UIKit.NewRounded(parent, "Inspector", new Color(0.05f, 0.07f, 0.12f, 0.92f), true);
            inspector = panel.rectTransform;
            UIKit.Anchor(inspector, new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), new Vector2(0f, 20f), new Vector2(560f, 250f));

            hint = UIKit.NewText(inspector, "", UITheme.FontCaption, UITheme.TextDim, TextAnchor.UpperLeft, FontStyle.Normal, "Hint");
            UIKit.Anchor(hint.rectTransform, new Vector2(0f, 1f), new Vector2(1f, 1f), new Vector2(0.5f, 1f), new Vector2(0f, -10f), new Vector2(-32f, 90f));
            UIKit.FitText(hint, 16);

            selectedLabel = UIKit.NewText(inspector, "", UITheme.FontBody, UITheme.Accent, TextAnchor.MiddleLeft, FontStyle.Bold, "Selected");
            UIKit.Anchor(selectedLabel.rectTransform, new Vector2(0f, 1f), new Vector2(1f, 1f), new Vector2(0.5f, 1f), new Vector2(0f, -96f), new Vector2(-32f, 36f));

            scaleSlider = SliderRow(inspector, "SIZE", -140f, HudElementInfo.MinScale, HudElementInfo.MaxScale, v =>
            {
                if (suppressSlider || editView.SelectedId == null) return;
                var s = layout.Get(editView.SelectedId.Value);
                s.scale = v;
                editView.Relayout(Ctx.Size);
                RefreshState();
            });
            opacitySlider = SliderRow(inspector, "OPACITY", -196f, HudElementInfo.MinOpacity, HudElementInfo.MaxOpacity, v =>
            {
                if (suppressSlider || editView.SelectedId == null) return;
                var s = layout.Get(editView.SelectedId.Value);
                s.opacity = v;
                editView.Relayout(Ctx.Size);
                RefreshState();
            });
        }

        private static Slider SliderRow(RectTransform parent, string label, float y, float min, float max, UnityEngine.Events.UnityAction<float> onChanged)
        {
            var row = UIKit.NewRect("Row_" + label, parent);
            UIKit.Anchor(row, new Vector2(0f, 1f), new Vector2(1f, 1f), new Vector2(0.5f, 1f), new Vector2(0f, y), new Vector2(-32f, 50f));
            UIKit.HStack(row.gameObject, 10f);
            var t = UIKit.NewText(row, label, UITheme.FontCaption, UITheme.TextDim, TextAnchor.MiddleLeft, FontStyle.Bold, "Label");
            UIKit.SetLayout(t.gameObject, preferredWidth: 120f, flexibleWidth: 0f);
            var slider = UIKit.NewSlider(row, "Slider", 0f, null);
            slider.minValue = min;
            slider.maxValue = max;
            UIKit.SetLayout(slider.gameObject, minWidth: 0f, preferredHeight: 48f, flexibleWidth: 1f);
            slider.onValueChanged.AddListener(onChanged);
            return slider;
        }

        protected override void OnShown()
        {
            previewing = false;
            LoadLayout();
        }

        protected override void OnHidden()
        {
            // Leaving without SAVE discards edits (they only ever lived in memory).
            if (editView != null) editView.SetSelected(null);
        }

        protected override void OnLayout()
        {
            if (editView == null || layout == null) return;
            if (layout.key != HudLayout.KeyFor(Ctx.Portrait)) { LoadLayout(); return; }
            editView.Relayout(Ctx.Size);
            previewView.Relayout(Ctx.Size);
            UIKit.Anchor(toolbar, new Vector2(0.5f, 1f), new Vector2(0.5f, 1f), new Vector2(0.5f, 1f), new Vector2(0f, -8f), new Vector2(Mathf.Min(900f, Ctx.Size.x - 24f), 76f));
            UIKit.Anchor(inspector, new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), new Vector2(0f, 20f), new Vector2(Mathf.Min(560f, Ctx.Size.x - 24f), 250f));
        }

        private void LoadLayout()
        {
            layout = AppServices.Config.LoadHud(Ctx.Portrait, Ctx.Size);
            original = layout.Clone();
            editView.SetLayout(layout);
            previewView.SetLayout(layout);
            editView.SetSelected(null);
            ApplyMode();
            RefreshState();
        }

        private void ApplyMode()
        {
            editView.gameObject.SetActive(!previewing);
            previewView.gameObject.SetActive(previewing);
            inspector.gameObject.SetActive(!previewing);
            previewButton.SetLabel(previewing ? "EDIT" : "PREVIEW");
        }

        private void TogglePreview()
        {
            previewing = !previewing;
            if (previewing) editView.SetSelected(null);
            ApplyMode();
        }

        private void OnSelectionChanged(HudElementId? id)
        {
            suppressSlider = true;
            if (id == null)
            {
                selectedLabel.text = "";
            }
            else
            {
                var s = layout.Get(id.Value);
                selectedLabel.text = HudElementInfo.Label(id.Value) + (HudElementInfo.IsMovable(id.Value) ? "" : "  (fixed position)");
                scaleSlider.SetValueWithoutNotify(s.scale);
                opacitySlider.SetValueWithoutNotify(id == HudElementId.Look || id == HudElementId.Minimap ? 1f : s.opacity);
                opacitySlider.interactable = id != HudElementId.Look && id != HudElementId.Minimap;
            }
            suppressSlider = false;
            RefreshState();
        }

        private bool Dirty()
        {
            if (layout == null || original == null) return false;
            foreach (HudElementId id in Enum.GetValues(typeof(HudElementId)))
            {
                var a = layout.Get(id);
                var b = original.Get(id);
                if (a == null || b == null) continue;
                if (Mathf.Abs(a.x - b.x) > 1e-4f || Mathf.Abs(a.y - b.y) > 1e-4f || Mathf.Abs(a.scale - b.scale) > 1e-4f || Mathf.Abs(a.opacity - b.opacity) > 1e-4f) return true;
            }
            return false;
        }

        private void RefreshState()
        {
            if (hint == null) return;
            bool dirty = Dirty();
            saveButton.SetInteractable(dirty);
            string orientation = Ctx.Portrait ? "PORTRAIT" : "LANDSCAPE";
            hint.text = "Editing " + orientation + " layout. Drag a control to move it; tap it to change size and opacity. " +
                        "Controls can't leave the safe area. " + (dirty ? "<color=#FFC742>Unsaved changes.</color>" : "");
        }

        private void ResetToDefaults()
        {
            Ctx.Confirm("Restore defaults", "Reset this layout to the default positions and sizes? Press SAVE afterwards to keep it.", "RESTORE", () =>
            {
                layout = HudLayout.CreateDefault(Ctx.Portrait);
                layout.Sanitize(Ctx.Size);
                editView.SetLayout(layout);
                previewView.SetLayout(layout);
                editView.SetSelected(null);
                RefreshState();
            });
        }

        private void Save()
        {
            layout.Sanitize(Ctx.Size);
            AppServices.Config.SaveHud(layout);
            original = layout.Clone();
            RefreshState();

            if (AppServices.Account.State == AccountState.SignedIn)
            {
                AppServices.Account.PushHud(layout, r => AppServices.Notifications.Post(
                    r.Success ? "HUD layout saved" : "Saved on this device only",
                    r.Success ? "Layout synced to your account." : "Couldn't sync to the server: " + r.Error,
                    r.Success ? NotificationSeverity.Success : NotificationSeverity.Warning));
            }
            else
            {
                AppServices.Notifications.Post("HUD layout saved", "Saved on this device.", NotificationSeverity.Success);
            }
        }
    }
}
