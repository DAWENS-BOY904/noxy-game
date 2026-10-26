using System;
using Noxy.Core;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.UI;

namespace Noxy.Lobby
{
    /// <summary>Horizontal row of equally sized tab buttons with one selected.</summary>
    public sealed class TabBar
    {
        private readonly ButtonView[] buttons;

        public RectTransform Root { get; private set; }

        private TabBar(int count) { buttons = new ButtonView[count]; }

        public static TabBar Create(Transform parent, string[] labels, Action<int> onSelect, int fontSize = 22)
        {
            var bar = new TabBar(labels.Length);
            bar.Root = UIKit.NewRect("Tabs", parent);
            var layout = UIKit.HStack(bar.Root.gameObject, 6f);
            layout.childForceExpandWidth = true;

            for (int i = 0; i < labels.Length; i++)
            {
                int index = i;
                var b = UIKit.NewButton(bar.Root, "Tab_" + labels[i], labels[i], UITheme.Button, () => onSelect(index), fontSize);
                UIKit.SetLayout(b.gameObject, minWidth: 0f, flexibleWidth: 1f);
                bar.buttons[i] = b;
            }
            return bar;
        }

        public void SetSelected(int index)
        {
            for (int i = 0; i < buttons.Length; i++)
                buttons[i].SetColor(i == index ? UITheme.AccentDark : UITheme.Button);
        }
    }

    /// <summary>Grid tile for a cosmetic item (or an empty "None" slot) used by Wardrobe and Inventory.</summary>
    public static class CosmeticTile
    {
        private static readonly Color SelectedBackground = new Color(0.30f, 0.17f, 0.06f, 0.96f);

        public static ButtonView Create(Transform parent, CosmeticItem item, string state, Color stateColor, bool selected, UnityAction onClick)
        {
            var tile = UIKit.NewButton(parent, item != null ? "Item_" + item.Id : "Item_None", "", selected ? SelectedBackground : UITheme.PanelLight, onClick);
            UnityEngine.Object.Destroy(tile.Label.gameObject);

            Color rarity = item != null ? UITheme.Rarity(item.Rarity) : UITheme.TextDim;
            var bar = UIKit.NewImage(tile.transform, "RarityBar", rarity);
            UIKit.Anchor(bar.rectTransform, new Vector2(0f, 1f), Vector2.one, new Vector2(0.5f, 1f), Vector2.zero, new Vector2(-28f, 5f));

            if (item != null)
            {
                var a = UIKit.NewImage(tile.transform, "SwatchA", item.PrimaryColor, UISprites.Circle);
                UIKit.Anchor(a.rectTransform, new Vector2(0.5f, 1f), new Vector2(0.5f, 1f), new Vector2(0.5f, 1f), new Vector2(-20f, -16f), new Vector2(40f, 40f));
                var b = UIKit.NewImage(tile.transform, "SwatchB", item.SecondaryColor, UISprites.Circle);
                UIKit.Anchor(b.rectTransform, new Vector2(0.5f, 1f), new Vector2(0.5f, 1f), new Vector2(0.5f, 1f), new Vector2(20f, -16f), new Vector2(40f, 40f));
            }

            var name = UIKit.NewText(tile.transform, item != null ? item.DisplayName : "None", UITheme.FontCaption, UITheme.Text, TextAnchor.MiddleCenter, FontStyle.Bold, "Name");
            UIKit.Anchor(name.rectTransform, new Vector2(0f, 0f), new Vector2(1f, 1f), new Vector2(0.5f, 0.5f), Vector2.zero, Vector2.zero);
            name.rectTransform.offsetMin = new Vector2(8f, 36f);
            name.rectTransform.offsetMax = new Vector2(-8f, -64f);
            UIKit.FitText(name, 16);

            var st = UIKit.NewText(tile.transform, state, 20, stateColor, TextAnchor.MiddleCenter, FontStyle.Bold, "State");
            UIKit.Anchor(st.rectTransform, new Vector2(0f, 0f), new Vector2(1f, 0f), new Vector2(0.5f, 0f), new Vector2(0f, 4f), new Vector2(-8f, 32f));
            UIKit.FitText(st, 14);
            return tile;
        }
    }
}
