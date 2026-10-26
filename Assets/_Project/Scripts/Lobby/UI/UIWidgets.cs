using UnityEngine;
using UnityEngine.UI;

namespace Noxy.Lobby
{
    /// <summary>Reusable composite widgets used by several screens.</summary>
    public static class UIWidgets
    {
        public static Text SectionTitle(Transform parent, string text)
        {
            var t = UIKit.NewText(parent, text.ToUpperInvariant(), UITheme.FontBody, UITheme.Accent, TextAnchor.LowerLeft, FontStyle.Bold, "Section");
            UIKit.SetLayout(t.gameObject, preferredHeight: 48f);
            return t;
        }

        public static Text Paragraph(Transform parent, string text, Color color, int size = UITheme.FontCaption)
        {
            var t = UIKit.NewText(parent, text, size, color, TextAnchor.UpperLeft, FontStyle.Normal, "Paragraph");
            t.verticalOverflow = VerticalWrapMode.Overflow;
            return t;
        }

        public static RectTransform Grid(Transform parent, float minCellWidth, float cellHeight, float spacing = 10f, int maxColumns = 6)
        {
            var rt = UIKit.NewRect("Grid", parent);
            UIKit.NewGrid(rt.gameObject, spacing);
            var rg = rt.gameObject.AddComponent<ResponsiveGrid>();
            rg.MinCellWidth = minCellWidth;
            rg.CellHeight = cellHeight;
            rg.MaxColumns = maxColumns;
            return rt;
        }

        public static void StatTile(Transform grid, string label, string value, Color valueColor)
        {
            var tile = UIKit.NewRounded(grid, "Stat_" + label, UITheme.PanelLight);
            var v = UIKit.NewText(tile.transform, value, 36, valueColor, TextAnchor.LowerCenter, FontStyle.Bold, "Value");
            UIKit.Anchor(v.rectTransform, new Vector2(0f, 0.5f), Vector2.one, new Vector2(0.5f, 0.5f), Vector2.zero, Vector2.zero);
            v.rectTransform.offsetMin = new Vector2(6f, 0f);
            v.rectTransform.offsetMax = new Vector2(-6f, 0f);
            UIKit.FitText(v, 22);
            var l = UIKit.NewText(tile.transform, label, UITheme.FontCaption, UITheme.TextDim, TextAnchor.UpperCenter, FontStyle.Normal, "Label");
            UIKit.Anchor(l.rectTransform, Vector2.zero, new Vector2(1f, 0.5f), new Vector2(0.5f, 0.5f), Vector2.zero, Vector2.zero);
            l.rectTransform.offsetMin = new Vector2(6f, 0f);
            l.rectTransform.offsetMax = new Vector2(-6f, -4f);
            UIKit.FitText(l, 18);
        }

        /// <summary>Two-column row: dim label left, value right (value may be colored).</summary>
        public static void KeyValueRow(Transform parent, string key, string value, Color valueColor)
        {
            var row = UIKit.NewRounded(parent, "Row_" + key, UITheme.PanelLight);
            UIKit.SetLayout(row.gameObject, preferredHeight: 64f);

            var k = UIKit.NewText(row.transform, key, UITheme.FontBody, UITheme.TextDim, TextAnchor.MiddleLeft, FontStyle.Normal, "Key");
            UIKit.Anchor(k.rectTransform, Vector2.zero, new Vector2(0.38f, 1f), new Vector2(0f, 0.5f), Vector2.zero, Vector2.zero);
            k.rectTransform.offsetMin = new Vector2(18f, 0f);
            k.rectTransform.offsetMax = Vector2.zero;
            UIKit.FitText(k, 18);

            var v = UIKit.NewText(row.transform, value, UITheme.FontBody, valueColor, TextAnchor.MiddleRight, FontStyle.Bold, "Value");
            UIKit.Anchor(v.rectTransform, new Vector2(0.38f, 0f), Vector2.one, new Vector2(1f, 0.5f), Vector2.zero, Vector2.zero);
            v.rectTransform.offsetMin = Vector2.zero;
            v.rectTransform.offsetMax = new Vector2(-18f, 0f);
            UIKit.FitText(v, 18);
        }

        public static Image ProgressBar(Transform parent, float height, float value, Color fill)
        {
            var track = UIKit.NewRounded(parent, "Progress", new Color(1f, 1f, 1f, 0.12f));
            UIKit.SetLayout(track.gameObject, preferredHeight: height);
            var bar = UIKit.NewRounded(track.transform, "Fill", fill);
            bar.rectTransform.anchorMin = Vector2.zero;
            bar.rectTransform.anchorMax = new Vector2(Mathf.Clamp01(value), 1f);
            bar.rectTransform.offsetMin = Vector2.zero;
            bar.rectTransform.offsetMax = Vector2.zero;
            return bar;
        }
    }
}
