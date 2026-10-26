using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace Noxy.Lobby
{
    /// <summary>Button wrapper with press feedback, optional idle pulse and a notification badge.</summary>
    public sealed class ButtonView : MonoBehaviour, IPointerDownHandler, IPointerUpHandler, IPointerExitHandler
    {
        public Button Button { get; private set; }
        public Image Background { get; private set; }
        public Text Label { get; private set; }

        /// <summary>0 disables. Otherwise scale oscillates by this fraction while interactable.</summary>
        public float PulseAmount;

        private float pressScale = 1f;
        private Image badge;
        private Text badgeText;

        public void Init(Button button, Image background, Text label)
        {
            Button = button;
            Background = background;
            Label = label;
        }

        public void SetColor(Color color) => Background.color = color;
        public void SetLabel(string text) { if (Label != null) Label.text = text; }
        public void SetInteractable(bool value) => Button.interactable = value;

        public void SetBadge(int count)
        {
            if (count <= 0)
            {
                if (badge != null) badge.gameObject.SetActive(false);
                return;
            }
            if (badge == null)
            {
                badge = UIKit.NewImage(transform, "Badge", UITheme.Danger, UISprites.Circle);
                UIKit.Anchor(badge.rectTransform, Vector2.one, Vector2.one, Vector2.one, new Vector2(8f, 8f), new Vector2(40f, 40f));
                badgeText = UIKit.NewText(badge.transform, "", 22, Color.white, TextAnchor.MiddleCenter, FontStyle.Bold, "Count");
            }
            badge.gameObject.SetActive(true);
            badgeText.text = count > 9 ? "9+" : count.ToString();
        }

        private void Update()
        {
            float pulse = 1f;
            if (PulseAmount > 0f && Button != null && Button.interactable)
                pulse += Mathf.Sin(Time.unscaledTime * 2.4f) * PulseAmount;

            float target = pressScale * pulse;
            float s = transform.localScale.x;
            s = Mathf.Lerp(s, target, 1f - Mathf.Exp(-24f * Time.unscaledDeltaTime));
            transform.localScale = new Vector3(s, s, 1f);
        }

        public void OnPointerDown(PointerEventData eventData)
        {
            if (Button != null && Button.interactable) pressScale = 0.95f;
        }

        public void OnPointerUp(PointerEventData eventData) => pressScale = 1f;
        public void OnPointerExit(PointerEventData eventData) => pressScale = 1f;

        private void OnDisable() => pressScale = 1f;
    }

    /// <summary>Raises an event when its RectTransform changes size (UI is laid out after Start).</summary>
    public sealed class RectWatcher : UIBehaviour
    {
        public event System.Action<Rect> Resized;

        protected override void OnRectTransformDimensionsChange()
        {
            base.OnRectTransformDimensionsChange();
            Notify();
        }

        protected override void OnEnable()
        {
            base.OnEnable();
            Notify();
        }

        private void Notify()
        {
            var rt = transform as RectTransform;
            if (rt != null && rt.rect.width > 1f && rt.rect.height > 1f) Resized?.Invoke(rt.rect);
        }
    }

    /// <summary>Sizes grid cells so columns always fill the available width.</summary>
    [RequireComponent(typeof(GridLayoutGroup))]
    public sealed class ResponsiveGrid : UIBehaviour
    {
        public float MinCellWidth = 200f;
        public float CellHeight = 120f;
        public int MaxColumns = 6;

        protected override void OnRectTransformDimensionsChange()
        {
            base.OnRectTransformDimensionsChange();
            Recalculate();
        }

        protected override void OnEnable()
        {
            base.OnEnable();
            Recalculate();
        }

        public void Recalculate()
        {
            var grid = GetComponent<GridLayoutGroup>();
            var rt = transform as RectTransform;
            if (grid == null || rt == null) return;

            float available = rt.rect.width - grid.padding.left - grid.padding.right;
            if (available < 10f) return;

            int cols = Mathf.Clamp(Mathf.FloorToInt((available + grid.spacing.x) / (MinCellWidth + grid.spacing.x)), 1, MaxColumns);
            float width = (available - grid.spacing.x * (cols - 1)) / cols;

            grid.constraint = GridLayoutGroup.Constraint.FixedColumnCount;
            grid.constraintCount = cols;
            grid.cellSize = new Vector2(Mathf.Floor(width), CellHeight);
        }
    }

    /// <summary>Smooth show/hide for panels.</summary>
    [RequireComponent(typeof(CanvasGroup))]
    public sealed class UIFader : MonoBehaviour
    {
        private CanvasGroup group;
        private float target = 1f;
        public float Speed = 9f;

        public bool IsVisible => target > 0.5f;

        private void Awake() => group = GetComponent<CanvasGroup>();

        public void Show(bool instant = false)
        {
            if (group == null) group = GetComponent<CanvasGroup>();
            if (!gameObject.activeSelf) group.alpha = instant ? 1f : 0f;
            gameObject.SetActive(true);
            target = 1f;
            group.interactable = true;
            group.blocksRaycasts = true;
            if (instant) group.alpha = 1f;
        }

        public void Hide(bool instant = false)
        {
            if (group == null) group = GetComponent<CanvasGroup>();
            target = 0f;
            group.interactable = false;
            group.blocksRaycasts = false;
            if (instant)
            {
                group.alpha = 0f;
                gameObject.SetActive(false);
            }
        }

        private void Update()
        {
            if (group.alpha == target) return;
            group.alpha = Mathf.MoveTowards(group.alpha, target, Speed * Time.unscaledDeltaTime);
            if (target <= 0f && group.alpha <= 0f) gameObject.SetActive(false);
        }
    }
}
