using UnityEngine;
using UnityEngine.Events;
using UnityEngine.UI;

namespace Noxy.Lobby
{
    /// <summary>Code-driven uGUI factory. Keeps screen scripts short and visually consistent.</summary>
    public static class UIKit
    {
        private static Font font;

        public static Font Font
        {
            get
            {
                if (font == null)
                {
                    font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
                    if (font == null) font = Font.CreateDynamicFontFromOSFont("Arial", 16);
                }
                return font;
            }
        }

        public static RectTransform NewRect(string name, Transform parent)
        {
            var go = new GameObject(name, typeof(RectTransform));
            var rt = (RectTransform)go.transform;
            rt.SetParent(parent, false);
            return rt;
        }

        public static void Stretch(RectTransform rt, float left = 0f, float bottom = 0f, float right = 0f, float top = 0f)
        {
            rt.anchorMin = Vector2.zero;
            rt.anchorMax = Vector2.one;
            rt.pivot = new Vector2(0.5f, 0.5f);
            rt.offsetMin = new Vector2(left, bottom);
            rt.offsetMax = new Vector2(-right, -top);
        }

        public static void Anchor(RectTransform rt, Vector2 anchorMin, Vector2 anchorMax, Vector2 pivot, Vector2 position, Vector2 size)
        {
            rt.anchorMin = anchorMin;
            rt.anchorMax = anchorMax;
            rt.pivot = pivot;
            rt.anchoredPosition = position;
            rt.sizeDelta = size;
        }

        public static Image NewImage(Transform parent, string name, Color color, Sprite sprite = null, bool raycast = false)
        {
            var rt = NewRect(name, parent);
            var img = rt.gameObject.AddComponent<Image>();
            img.sprite = sprite;
            img.type = sprite != null && sprite.border != Vector4.zero ? Image.Type.Sliced : Image.Type.Simple;
            img.color = color;
            img.raycastTarget = raycast;
            return img;
        }

        public static Image NewRounded(Transform parent, string name, Color color, bool raycast = false)
        {
            return NewImage(parent, name, color, UISprites.Rounded, raycast);
        }

        public static Text NewText(Transform parent, string text, int size, Color color,
            TextAnchor align = TextAnchor.MiddleCenter, FontStyle style = FontStyle.Normal, string name = "Text")
        {
            var rt = NewRect(name, parent);
            Stretch(rt);
            var t = rt.gameObject.AddComponent<Text>();
            t.font = Font;
            t.text = text;
            t.fontSize = size;
            t.color = color;
            t.alignment = align;
            t.fontStyle = style;
            t.horizontalOverflow = HorizontalWrapMode.Wrap;
            t.verticalOverflow = VerticalWrapMode.Truncate;
            t.supportRichText = true;
            t.raycastTarget = false;
            return t;
        }

        /// <summary>Shrinks text to fit its box instead of overflowing on narrow screens.</summary>
        public static Text FitText(Text t, int minSize)
        {
            t.resizeTextForBestFit = true;
            t.resizeTextMinSize = minSize;
            t.resizeTextMaxSize = t.fontSize;
            return t;
        }

        public static ButtonView NewButton(Transform parent, string name, string label, Color color,
            UnityAction onClick, int fontSize = UITheme.FontButton)
        {
            var img = NewRounded(parent, name, color, true);
            var button = img.gameObject.AddComponent<Button>();
            button.targetGraphic = img;
            button.transition = Selectable.Transition.ColorTint;
            button.navigation = new Navigation { mode = Navigation.Mode.None };
            var colors = button.colors;
            colors.normalColor = Color.white;
            colors.highlightedColor = new Color(1.15f, 1.15f, 1.15f, 1f);
            colors.pressedColor = new Color(0.78f, 0.78f, 0.78f, 1f);
            colors.selectedColor = Color.white;
            colors.disabledColor = new Color(0.55f, 0.55f, 0.55f, 0.85f);
            colors.fadeDuration = 0.08f;
            button.colors = colors;

            var text = NewText(img.transform, label, fontSize, UITheme.Text, TextAnchor.MiddleCenter, FontStyle.Bold, "Label");
            Stretch(text.rectTransform, 10f, 4f, 10f, 4f);
            FitText(text, Mathf.RoundToInt(fontSize * 0.65f));

            var view = img.gameObject.AddComponent<ButtonView>();
            view.Init(button, img, text);
            if (onClick != null) button.onClick.AddListener(onClick);
            return view;
        }

        public static LayoutElement SetLayout(GameObject go, float minWidth = -1f, float minHeight = -1f,
            float preferredWidth = -1f, float preferredHeight = -1f, float flexibleWidth = -1f, float flexibleHeight = -1f)
        {
            var le = go.GetComponent<LayoutElement>();
            if (le == null) le = go.AddComponent<LayoutElement>();
            le.minWidth = minWidth;
            le.minHeight = minHeight;
            le.preferredWidth = preferredWidth;
            le.preferredHeight = preferredHeight;
            le.flexibleWidth = flexibleWidth;
            le.flexibleHeight = flexibleHeight;
            return le;
        }

        public static VerticalLayoutGroup VStack(GameObject go, float spacing, int padding = 0,
            TextAnchor align = TextAnchor.UpperLeft, bool expandWidth = true)
        {
            var g = go.AddComponent<VerticalLayoutGroup>();
            g.spacing = spacing;
            g.padding = new RectOffset(padding, padding, padding, padding);
            g.childAlignment = align;
            g.childControlWidth = true;
            g.childControlHeight = true;
            g.childForceExpandWidth = expandWidth;
            g.childForceExpandHeight = false;
            return g;
        }

        public static HorizontalLayoutGroup HStack(GameObject go, float spacing, int padding = 0,
            TextAnchor align = TextAnchor.MiddleLeft, bool expandHeight = true)
        {
            var g = go.AddComponent<HorizontalLayoutGroup>();
            g.spacing = spacing;
            g.padding = new RectOffset(padding, padding, padding, padding);
            g.childAlignment = align;
            g.childControlWidth = true;
            g.childControlHeight = true;
            g.childForceExpandWidth = false;
            g.childForceExpandHeight = expandHeight;
            return g;
        }

        public static GridLayoutGroup NewGrid(GameObject go, float spacing, int padding = 0)
        {
            var g = go.AddComponent<GridLayoutGroup>();
            g.spacing = new Vector2(spacing, spacing);
            g.padding = new RectOffset(padding, padding, padding, padding);
            g.startCorner = GridLayoutGroup.Corner.UpperLeft;
            g.startAxis = GridLayoutGroup.Axis.Horizontal;
            g.childAlignment = TextAnchor.UpperLeft;
            g.constraint = GridLayoutGroup.Constraint.FixedColumnCount;
            g.constraintCount = 2;
            g.cellSize = new Vector2(200f, 100f);
            return g;
        }

        /// <summary>Scroll view (touch drag + mouse wheel). Caller adds a layout group to the returned content.</summary>
        public static ScrollRect NewScroll(Transform parent, string name, out RectTransform content)
        {
            var root = NewRect(name, parent);
            var scroll = root.gameObject.AddComponent<ScrollRect>();

            var viewport = NewRect("Viewport", root);
            Stretch(viewport);
            viewport.gameObject.AddComponent<RectMask2D>();
            var hit = viewport.gameObject.AddComponent<Image>();
            hit.color = new Color(0f, 0f, 0f, 0f);

            content = NewRect("Content", viewport);
            content.anchorMin = new Vector2(0f, 1f);
            content.anchorMax = new Vector2(1f, 1f);
            content.pivot = new Vector2(0.5f, 1f);
            content.sizeDelta = Vector2.zero;
            var fitter = content.gameObject.AddComponent<ContentSizeFitter>();
            fitter.verticalFit = ContentSizeFitter.FitMode.PreferredSize;

            scroll.viewport = viewport;
            scroll.content = content;
            scroll.horizontal = false;
            scroll.vertical = true;
            scroll.movementType = ScrollRect.MovementType.Elastic;
            scroll.scrollSensitivity = 40f;
            scroll.inertia = true;
            scroll.decelerationRate = 0.12f;
            return scroll;
        }

        public static Slider NewSlider(Transform parent, string name, float value, UnityAction<float> onChanged)
        {
            var root = NewRect(name, parent);
            var slider = root.gameObject.AddComponent<Slider>();

            var bg = NewRounded(root, "Background", new Color(1f, 1f, 1f, 0.12f));
            bg.rectTransform.anchorMin = new Vector2(0f, 0.5f);
            bg.rectTransform.anchorMax = new Vector2(1f, 0.5f);
            bg.rectTransform.sizeDelta = new Vector2(0f, 16f);

            var fillArea = NewRect("Fill Area", root);
            fillArea.anchorMin = new Vector2(0f, 0.5f);
            fillArea.anchorMax = new Vector2(1f, 0.5f);
            fillArea.sizeDelta = new Vector2(-24f, 16f);
            var fill = NewRounded(fillArea, "Fill", UITheme.Accent);
            fill.rectTransform.sizeDelta = new Vector2(10f, 0f);

            var handleArea = NewRect("Handle Slide Area", root);
            Stretch(handleArea, 22f, 0f, 22f, 0f);
            var handle = NewImage(handleArea, "Handle", Color.white, UISprites.Circle, true);
            handle.rectTransform.sizeDelta = new Vector2(44f, 44f);

            slider.fillRect = fill.rectTransform;
            slider.handleRect = handle.rectTransform;
            slider.targetGraphic = handle;
            slider.direction = Slider.Direction.LeftToRight;
            slider.minValue = 0f;
            slider.maxValue = 1f;
            slider.SetValueWithoutNotify(value);
            if (onChanged != null) slider.onValueChanged.AddListener(onChanged);
            return slider;
        }

        public static void DestroyChildren(Transform parent)
        {
            for (int i = parent.childCount - 1; i >= 0; i--)
            {
                var child = parent.GetChild(i).gameObject;
                child.SetActive(false);
                Object.Destroy(child);
            }
        }
    }
}
