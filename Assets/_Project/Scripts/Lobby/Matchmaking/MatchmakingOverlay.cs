using System;
using Noxy.Core;
using UnityEngine;
using UnityEngine.UI;

namespace Noxy.Lobby
{
    /// <summary>Full-screen status card for the PLAY flow: searching, loading, errors, cancel/retry.</summary>
    public sealed class MatchmakingOverlay : MonoBehaviour
    {
        private Text title, modeLabel, message, timer;
        private Image spinner, progressFill;
        private ButtonView cancel, retry, close;
        private UIFader fader;
        private bool spinning;

        public event Action CancelClicked;
        public event Action RetryClicked;
        public event Action CloseClicked;

        public bool IsVisible => fader != null && fader.gameObject.activeSelf && fader.IsVisible;

        public static MatchmakingOverlay Create(Transform parent)
        {
            var go = new GameObject("MatchmakingOverlay", typeof(RectTransform), typeof(CanvasGroup));
            go.transform.SetParent(parent, false);
            UIKit.Stretch((RectTransform)go.transform);
            go.AddComponent<UIFader>();
            var overlay = go.AddComponent<MatchmakingOverlay>();
            overlay.Build();
            return overlay;
        }

        private void Build()
        {
            fader = GetComponent<UIFader>();

            var dim = UIKit.NewImage(transform, "Dim", new Color(0.01f, 0.02f, 0.04f, 0.82f), null, true);
            UIKit.Stretch(dim.rectTransform);

            var card = UIKit.NewRounded(transform, "Card", UITheme.Panel, true);
            UIKit.Anchor(card.rectTransform, new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), Vector2.zero, new Vector2(640f, 520f));

            var bar = UIKit.NewImage(card.transform, "AccentBar", UITheme.Accent);
            UIKit.Anchor(bar.rectTransform, new Vector2(0f, 1f), new Vector2(1f, 1f), new Vector2(0.5f, 1f), Vector2.zero, new Vector2(-48f, 5f));

            title = TopText(card.transform, UITheme.FontTitle, FontStyle.Bold, UITheme.Text, -24f, 56f);
            modeLabel = TopText(card.transform, UITheme.FontBody, FontStyle.Normal, UITheme.TextDim, -82f, 40f);

            spinner = UIKit.NewImage(card.transform, "Spinner", UITheme.Accent, UISprites.Spinner);
            UIKit.Anchor(spinner.rectTransform, new Vector2(0.5f, 1f), new Vector2(0.5f, 1f), new Vector2(0.5f, 1f), new Vector2(0f, -132f), new Vector2(110f, 110f));

            message = TopText(card.transform, UITheme.FontBody, FontStyle.Normal, UITheme.Text, -256f, 96f);
            message.rectTransform.offsetMin = new Vector2(32f, message.rectTransform.offsetMin.y);
            message.rectTransform.offsetMax = new Vector2(-32f, message.rectTransform.offsetMax.y);

            var track = UIKit.NewRounded(card.transform, "ProgressTrack", new Color(1f, 1f, 1f, 0.12f));
            UIKit.Anchor(track.rectTransform, new Vector2(0.5f, 1f), new Vector2(0.5f, 1f), new Vector2(0.5f, 1f), new Vector2(0f, -364f), new Vector2(480f, 14f));
            progressFill = UIKit.NewRounded(track.transform, "Fill", UITheme.Accent);
            progressFill.rectTransform.anchorMin = Vector2.zero;
            progressFill.rectTransform.anchorMax = new Vector2(0f, 1f);
            progressFill.rectTransform.offsetMin = Vector2.zero;
            progressFill.rectTransform.offsetMax = Vector2.zero;

            timer = TopText(card.transform, UITheme.FontCaption, FontStyle.Normal, UITheme.TextDim, -386f, 34f);

            cancel = UIKit.NewButton(card.transform, "Cancel", "CANCEL", UITheme.Button, () => CancelClicked?.Invoke());
            UIKit.Anchor(cancel.Background.rectTransform, new Vector2(0.5f, 0f), new Vector2(0.5f, 0f), new Vector2(0.5f, 0f), new Vector2(0f, 28f), new Vector2(260f, 72f));

            retry = UIKit.NewButton(card.transform, "Retry", "RETRY", UITheme.Accent, () => RetryClicked?.Invoke());
            UIKit.Anchor(retry.Background.rectTransform, new Vector2(0.5f, 0f), new Vector2(0.5f, 0f), new Vector2(1f, 0f), new Vector2(-10f, 28f), new Vector2(230f, 72f));

            close = UIKit.NewButton(card.transform, "Close", "CLOSE", UITheme.Button, () => CloseClicked?.Invoke());
            UIKit.Anchor(close.Background.rectTransform, new Vector2(0.5f, 0f), new Vector2(0.5f, 0f), new Vector2(0f, 0f), new Vector2(10f, 28f), new Vector2(230f, 72f));

            fader.Hide(true);
        }

        private static Text TopText(Transform parent, int size, FontStyle style, Color color, float y, float height)
        {
            var t = UIKit.NewText(parent, "", size, color, TextAnchor.UpperCenter, style);
            UIKit.Anchor(t.rectTransform, new Vector2(0f, 1f), new Vector2(1f, 1f), new Vector2(0.5f, 1f), new Vector2(0f, y), new Vector2(0f, height));
            return t;
        }

        public void Present(MatchmakingStatus s)
        {
            fader.Show();
            string modeName = s.Mode != null ? s.Mode.DisplayName.ToUpperInvariant() : "";
            modeLabel.text = modeName;
            message.text = s.Message;
            SetProgress(s.Progress);

            bool active = s.State == MatchmakingState.Searching || s.State == MatchmakingState.Connecting;
            bool failed = s.State == MatchmakingState.Failed;
            bool done = s.State == MatchmakingState.Succeeded;

            spinning = active || done;
            spinner.gameObject.SetActive(!failed);
            spinner.color = UITheme.Accent;
            progressFill.color = failed ? UITheme.Danger : UITheme.Accent;
            progressFill.transform.parent.gameObject.SetActive(!failed);

            cancel.gameObject.SetActive(active);
            retry.gameObject.SetActive(failed);
            close.gameObject.SetActive(failed);

            if (failed)
            {
                title.text = "MATCH FAILED";
                title.color = UITheme.Danger;
                timer.text = s.Error == MatchmakingError.None ? "" : "Error: " + s.Error;
            }
            else
            {
                title.color = UITheme.Text;
                title.text = done ? "MATCH READY" : s.State == MatchmakingState.Connecting ? "CONNECTING" : "PREPARING MATCH";
                timer.text = active ? FormatTime(s.ElapsedSeconds) : "";
            }
        }

        public void SetLoadingProgress(float progress)
        {
            message.text = "Loading... " + Mathf.RoundToInt(Mathf.Clamp01(progress) * 100f) + "%";
            SetProgress(progress);
        }

        private void SetProgress(float p)
        {
            progressFill.rectTransform.anchorMax = new Vector2(Mathf.Clamp01(p), 1f);
        }

        public void Hide() => fader.Hide();

        private static string FormatTime(float seconds)
        {
            int s = Mathf.FloorToInt(seconds);
            return (s / 60).ToString("00") + ":" + (s % 60).ToString("00");
        }

        private void Update()
        {
            if (spinning && spinner != null)
                spinner.rectTransform.Rotate(0f, 0f, -300f * Time.unscaledDeltaTime);
        }
    }
}
