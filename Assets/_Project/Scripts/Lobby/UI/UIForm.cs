using UnityEngine;
using UnityEngine.Events;
using UnityEngine.UI;

namespace Noxy.Lobby
{
    /// <summary>Input fields, toggles and other form controls built in code.</summary>
    public static class UIForm
    {
        public static InputField NewInput(Transform parent, string name, string placeholder, InputField.ContentType type, int maxLength = 64)
        {
            var bg = UIKit.NewRounded(parent, name, new Color(1f, 1f, 1f, 0.09f), true);
            var field = bg.gameObject.AddComponent<InputField>();

            var text = UIKit.NewText(bg.transform, "", UITheme.FontBody, UITheme.Text, TextAnchor.MiddleLeft, FontStyle.Normal, "Text");
            UIKit.Stretch(text.rectTransform, 16f, 4f, 16f, 4f);
            text.supportRichText = false;
            text.horizontalOverflow = HorizontalWrapMode.Overflow;
            text.resizeTextForBestFit = false;

            var hint = UIKit.NewText(bg.transform, placeholder, UITheme.FontBody, UITheme.TextDim, TextAnchor.MiddleLeft, FontStyle.Italic, "Placeholder");
            UIKit.Stretch(hint.rectTransform, 16f, 4f, 16f, 4f);
            hint.horizontalOverflow = HorizontalWrapMode.Overflow;

            field.targetGraphic = bg;
            field.textComponent = text;
            field.placeholder = hint;
            field.lineType = InputField.LineType.SingleLine;
            field.contentType = type;
            field.characterLimit = maxLength;
            return field;
        }

        /// <summary>ON/OFF button that flips a bool through the supplied getter/setter.</summary>
        public static ButtonView NewToggle(Transform parent, System.Func<bool> get, System.Action<bool> set)
        {
            ButtonView button = null;
            button = UIKit.NewButton(parent, "Toggle", get() ? "ON" : "OFF", get() ? UITheme.Accent : UITheme.Disabled, () =>
            {
                bool next = !get();
                set(next);
                button.SetLabel(next ? "ON" : "OFF");
                button.SetColor(next ? UITheme.Accent : UITheme.Disabled);
            }, UITheme.FontBody);
            UIKit.SetLayout(button.gameObject, preferredWidth: 120f, preferredHeight: 56f, flexibleWidth: 0f);
            return button;
        }

        /// <summary>Slider with a live numeric value to its right. Returns the slider.</summary>
        public static Slider NewValueSlider(Transform row, float min, float max, float value, bool wholeNumbers, System.Func<float, string> format, UnityAction<float> onChanged)
        {
            var slider = UIKit.NewSlider(row, "Slider", 0f, null);
            slider.minValue = min;
            slider.maxValue = max;
            slider.wholeNumbers = wholeNumbers;
            UIKit.SetLayout(slider.gameObject, preferredWidth: 300f, preferredHeight: 48f, flexibleWidth: 0f);

            var label = UIKit.NewText(row, format(value), UITheme.FontBody, UITheme.Accent, TextAnchor.MiddleRight, FontStyle.Bold, "Value");
            UIKit.SetLayout(label.gameObject, preferredWidth: 90f, flexibleWidth: 0f);
            UIKit.FitText(label, 16);

            slider.SetValueWithoutNotify(value);
            slider.onValueChanged.AddListener(v =>
            {
                label.text = format(v);
                onChanged(v);
            });
            return slider;
        }
    }

    /// <summary>Modal confirm / password dialog shared by all screens.</summary>
    public sealed class DialogOverlay : MonoBehaviour
    {
        private UIFader fader;
        private Text title, body;
        private InputField input;
        private ButtonView confirm, cancel;
        private System.Action<string> onConfirm;

        public bool IsVisible => fader != null && fader.gameObject.activeSelf && fader.IsVisible;

        public static DialogOverlay Create(Transform parent)
        {
            var go = new GameObject("DialogOverlay", typeof(RectTransform), typeof(CanvasGroup));
            go.transform.SetParent(parent, false);
            UIKit.Stretch((RectTransform)go.transform);
            go.AddComponent<UIFader>();
            var d = go.AddComponent<DialogOverlay>();
            d.Build();
            return d;
        }

        private void Build()
        {
            fader = GetComponent<UIFader>();
            var dim = UIKit.NewImage(transform, "Dim", new Color(0f, 0f, 0f, 0.7f), null, true);
            UIKit.Stretch(dim.rectTransform);

            var card = UIKit.NewRounded(transform, "Card", UITheme.Panel, true);
            UIKit.Anchor(card.rectTransform, new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), Vector2.zero, new Vector2(620f, 460f));
            var bar = UIKit.NewImage(card.transform, "Bar", UITheme.Accent);
            UIKit.Anchor(bar.rectTransform, new Vector2(0f, 1f), new Vector2(1f, 1f), new Vector2(0.5f, 1f), Vector2.zero, new Vector2(-48f, 5f));

            title = UIKit.NewText(card.transform, "", UITheme.FontTitle, UITheme.Text, TextAnchor.MiddleCenter, FontStyle.Bold, "Title");
            UIKit.Anchor(title.rectTransform, new Vector2(0f, 1f), new Vector2(1f, 1f), new Vector2(0.5f, 1f), new Vector2(0f, -20f), new Vector2(-40f, 60f));
            UIKit.FitText(title, 24);

            body = UIKit.NewText(card.transform, "", UITheme.FontBody, UITheme.TextDim, TextAnchor.UpperCenter, FontStyle.Normal, "Body");
            UIKit.Anchor(body.rectTransform, new Vector2(0f, 1f), new Vector2(1f, 1f), new Vector2(0.5f, 1f), new Vector2(0f, -90f), new Vector2(-64f, 170f));
            UIKit.FitText(body, 18);

            input = UIForm.NewInput(card.transform, "Input", "Password", InputField.ContentType.Password, 128);
            UIKit.Anchor(input.GetComponent<RectTransform>(), new Vector2(0.5f, 0f), new Vector2(0.5f, 0f), new Vector2(0.5f, 0f), new Vector2(0f, 120f), new Vector2(480f, 64f));

            cancel = UIKit.NewButton(card.transform, "Cancel", "CANCEL", UITheme.Button, Close, UITheme.FontBody);
            UIKit.Anchor(cancel.Background.rectTransform, new Vector2(0.5f, 0f), new Vector2(0.5f, 0f), new Vector2(1f, 0f), new Vector2(-10f, 28f), new Vector2(240f, 72f));
            confirm = UIKit.NewButton(card.transform, "Confirm", "CONFIRM", UITheme.Accent, Confirm, UITheme.FontBody);
            UIKit.Anchor(confirm.Background.rectTransform, new Vector2(0.5f, 0f), new Vector2(0.5f, 0f), new Vector2(0f, 0f), new Vector2(10f, 28f), new Vector2(240f, 72f));

            fader.Hide(true);
        }

        public void Ask(string titleText, string message, string confirmLabel, System.Action yes, bool danger = false)
        {
            Open(titleText, message, confirmLabel, danger, false, _ => yes?.Invoke());
        }

        public void AskPassword(string titleText, string message, string confirmLabel, System.Action<string> yes, bool danger = true)
        {
            Open(titleText, message, confirmLabel, danger, true, yes);
        }

        private void Open(string titleText, string message, string confirmLabel, bool danger, bool password, System.Action<string> yes)
        {
            title.text = titleText.ToUpperInvariant();
            body.text = message;
            confirm.SetLabel(confirmLabel);
            confirm.SetColor(danger ? UITheme.Danger : UITheme.Accent);
            input.gameObject.SetActive(password);
            input.text = "";
            onConfirm = yes;
            transform.SetAsLastSibling();
            fader.Show();
        }

        private void Confirm()
        {
            var cb = onConfirm;
            string value = input.text;
            Close();
            cb?.Invoke(value);
        }

        public void Close()
        {
            onConfirm = null;
            fader.Hide();
        }
    }
}
