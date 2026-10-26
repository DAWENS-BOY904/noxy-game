using Noxy.Core;
using UnityEngine;
using UnityEngine.UI;

namespace Noxy.Lobby
{
    /// <summary>Everything the player owns: cosmetics (equippable) and consumable items.</summary>
    public sealed class InventoryScreen : LobbyScreen
    {
        private static readonly string[] TabLabels = { "ALL", "OUTFIT", "SHOES", "MASK", "HEAD", "BAG", "ITEMS" };
        private const int ItemsTab = 6;

        private TabBar tabs;
        private RectTransform gridContent;
        private Text detail;
        private ButtonView equip;
        private int tab;
        private CosmeticItem selected;

        public override LobbyScreenId Id => LobbyScreenId.Inventory;
        public override string Title => "Inventory";

        protected override void BuildContent(RectTransform parent)
        {
            tabs = TabBar.Create(parent, TabLabels, SelectTab);
            UIKit.Anchor(tabs.Root, new Vector2(0f, 1f), Vector2.one, new Vector2(0.5f, 1f), Vector2.zero, new Vector2(0f, 60f));

            var scroll = UIKit.NewScroll(parent, "Items", out gridContent);
            UIKit.Stretch((RectTransform)scroll.transform, 0f, 100f, 0f, 74f);
            UIKit.NewGrid(gridContent.gameObject, 10f);
            var rg = gridContent.gameObject.AddComponent<ResponsiveGrid>();
            rg.MinCellWidth = 170f;
            rg.CellHeight = 160f;
            rg.MaxColumns = 6;

            detail = UIKit.NewText(parent, "", UITheme.FontBody, UITheme.TextDim, TextAnchor.MiddleLeft, FontStyle.Normal, "Detail");
            UIKit.Anchor(detail.rectTransform,             new Vector2(0f, 0f), new Vector2(1f, 0f), new Vector2(0.5f, 0f), Vector2.zero, new Vector2(0f, 84f));
            detail.rectTransform.offsetMin = new Vector2(4f, 0f);
            detail.rectTransform.offsetMax = new Vector2(-250f, 84f);
            UIKit.FitText(detail, 18);

            equip = UIKit.NewButton(parent, "Equip", "EQUIP", UITheme.Accent, EquipSelected);
            UIKit.Anchor(equip.Background.rectTransform, new Vector2(1f, 0f), new Vector2(1f, 0f), new Vector2(1f, 0f), Vector2.zero, new Vector2(230f, 72f));
        }

        protected override void OnShown()
        {
            selected = null;
            SelectTab(tab);
        }

        private void SelectTab(int index)
        {
            tab = index;
            selected = null;
            tabs.SetSelected(index);
            Rebuild();
            RefreshDetail();
        }

        private void Rebuild()
        {
            UIKit.DestroyChildren(gridContent);
            var profile = AppServices.Profile.Profile;

            if (tab == ItemsTab)
            {
                foreach (var entry in profile.consumables)
                    BuildConsumableTile(entry);
                return;
            }

            foreach (var item in AppServices.Catalog.Items)
            {
                if (!AppServices.Profile.Owns(item.Id)) continue;
                if (tab > 0 && (int)item.Slot != tab - 1) continue;

                var captured = item;
                bool equipped = profile.loadout.Get(item.Slot) == item.Id;
                CosmeticTile.Create(gridContent, item, equipped ? "EQUIPPED" : "OWNED",
                    equipped ? UITheme.Success : UITheme.Cyan, selected == item, () => Select(captured));
            }
        }

        private void BuildConsumableTile(InventoryEntry entry)
        {
            var tile = UIKit.NewRounded(gridContent, "Consumable_" + entry.itemId, UITheme.PanelLight);
            var count = UIKit.NewText(tile.transform, "x" + entry.count, 44, UITheme.Accent, TextAnchor.MiddleCenter, FontStyle.Bold, "Count");
            UIKit.Anchor(count.rectTransform, new Vector2(0f, 0.5f), Vector2.one, new Vector2(0.5f, 0.5f), Vector2.zero, Vector2.zero);
            var name = UIKit.NewText(tile.transform, entry.displayName, UITheme.FontCaption, UITheme.Text, TextAnchor.MiddleCenter, FontStyle.Bold, "Name");
            UIKit.Anchor(name.rectTransform, Vector2.zero, new Vector2(1f, 0.5f), new Vector2(0.5f, 0.5f), Vector2.zero, Vector2.zero);
            name.rectTransform.offsetMin = new Vector2(8f, 28f);
            name.rectTransform.offsetMax = new Vector2(-8f, 0f);
            UIKit.FitText(name, 16);
            var note = UIKit.NewText(tile.transform, "NOT USABLE YET", 18, UITheme.TextDim, TextAnchor.LowerCenter, FontStyle.Normal, "Note");
            UIKit.Anchor(note.rectTransform, Vector2.zero, new Vector2(1f, 0f), new Vector2(0.5f, 0f), new Vector2(0f, 4f), new Vector2(0f, 26f));
        }

        private void Select(CosmeticItem item)
        {
            selected = item;
            Rebuild();
            RefreshDetail();
        }

        private void RefreshDetail()
        {
            if (tab == ItemsTab)
            {
                detail.text = "Consumable items can't be used yet - item usage needs the backend.";
                equip.gameObject.SetActive(false);
                return;
            }

            equip.gameObject.SetActive(true);
            if (selected == null)
            {
                detail.text = "Select an item to see details.";
                equip.SetInteractable(false);
                return;
            }

            bool equipped = AppServices.Profile.Profile.loadout.Get(selected.Slot) == selected.Id;
            detail.text = selected.DisplayName + "  -  " + selected.Rarity + " " + selected.Slot.DisplayName();
            equip.SetLabel(equipped ? "EQUIPPED" : "EQUIP");
            equip.SetInteractable(!equipped);
        }

        private void EquipSelected()
        {
            if (selected == null) return;

            var loadout = AppServices.Profile.Profile.loadout.Clone();
            loadout.Set(selected.Slot, selected.Id);
            var result = AppServices.Profile.EquipLoadout(loadout);
            if (result.Success)
            {
                Ctx.Preview.ShowLoadout(AppServices.Profile.Profile.loadout);
                AppServices.Notifications.Post("Equipped", selected.DisplayName + " equipped.", NotificationSeverity.Success);
                Rebuild();
                RefreshDetail();
            }
            else
            {
                detail.text = "<color=#F55257>" + result.Error + "</color>";
            }
        }
    }
}
