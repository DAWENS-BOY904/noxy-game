using System;
using Noxy.Core;
using UnityEngine;
using UnityEngine.UI;

namespace Noxy.Lobby
{
    /// <summary>Device preferences. Volume, quality and lobby options are real; the debug switch exercises the PLAY error UI.</summary>
    public sealed class SettingsScreen : LobbyScreen
    {
        private RectTransform content;

        public override LobbyScreenId Id => LobbyScreenId.Settings;
        public override string Title => "Settings";

        protected override void BuildContent(RectTransform parent)
        {
            var scroll = UIKit.NewScroll(parent, "Scroll", out content);
            UIKit.Stretch((RectTransform)scroll.transform);
            UIKit.VStack(content.gameObject, 10f);

            UIWidgets.SectionTitle(content, "Audio");
            SliderRow("Master volume", LobbySettings.MasterVolume, v => { LobbySettings.MasterVolume = v; });

            UIWidgets.SectionTitle(content, "Graphics");
            QualityRow();

            UIWidgets.SectionTitle(content, "Lobby");
            ToggleRow("Rotate character automatically", () => LobbySettings.AutoRotateCharacter, v => LobbySettings.AutoRotateCharacter = v);

            UIWidgets.SectionTitle(content, "Developer");
            ToggleRow("Simulate matchmaking error", () => LobbySettings.SimulateMatchmakingError, v => LobbySettings.SimulateMatchmakingError = v);
            UIWidgets.Paragraph(content, "When ON, pressing PLAY for Training fails on purpose so the error and retry UI can be tested.", UITheme.TextDim);

            UIWidgets.SectionTitle(content, "About");
            UIWidgets.Paragraph(content,
                "Version " + Application.version + "  |  " + Application.platform + "\n" +
                "Profile: local demo profile (offline)\n" +
                "Online services: not connected",
                UITheme.TextDim);
        }

        protected override void OnHidden() => LobbySettings.Save();

        private Row NewRow(float height = 76f)
        {
            var card = UIKit.NewRounded(content, "Setting", UITheme.PanelLight);
            UIKit.SetLayout(card.gameObject, preferredHeight: height);
            UIKit.HStack(card.gameObject, 12f, 14, TextAnchor.MiddleLeft);
            return new Row { Root = card.rectTransform };
        }

        private struct Row
        {
            public RectTransform Root;
        }

        private static Text RowLabel(RectTransform row, string text)
        {
            var label = UIKit.NewText(row, text, UITheme.FontBody, UITheme.Text, TextAnchor.MiddleLeft);
            UIKit.SetLayout(label.gameObject, minWidth: 120f, flexibleWidth: 1f);
            UIKit.FitText(label, 18);
            return label;
        }

        private void SliderRow(string label, float value, UnityEngine.Events.UnityAction<float> onChanged)
        {
            var row = NewRow();
            RowLabel(row.Root, label);
            var slider = UIKit.NewSlider(row.Root, "Slider", value, onChanged);
            UIKit.SetLayout(slider.gameObject, preferredWidth: 320f, preferredHeight: 48f, flexibleWidth: 0f);
        }

        private void ToggleRow(string label, Func<bool> get, Action<bool> set)
        {
            var row = NewRow();
            RowLabel(row.Root, label);
            ButtonView button = null;
            button = UIKit.NewButton(row.Root, "Toggle", get() ? "ON" : "OFF", get() ? UITheme.Accent : UITheme.Disabled, () =>
            {
                bool next = !get();
                set(next);
                button.SetLabel(next ? "ON" : "OFF");
                button.SetColor(next ? UITheme.Accent : UITheme.Disabled);
            }, UITheme.FontBody);
            UIKit.SetLayout(button.gameObject, preferredWidth: 120f, preferredHeight: 56f, flexibleWidth: 0f);
        }

        private void QualityRow()
        {
            var row = NewRow();
            RowLabel(row.Root, "Graphics quality");
            string[] names = QualitySettings.names;
            ButtonView button = null;
            button = UIKit.NewButton(row.Root, "Quality", names[LobbySettings.QualityLevel], UITheme.Button, () =>
            {
                int next = (LobbySettings.QualityLevel + 1) % names.Length;
                LobbySettings.QualityLevel = next;
                button.SetLabel(names[next]);
            }, UITheme.FontBody);
            UIKit.SetLayout(button.gameObject, preferredWidth: 220f, preferredHeight: 56f, flexibleWidth: 0f);
        }
    }
}
