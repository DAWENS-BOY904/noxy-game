using System;
using Noxy.Core;
using UnityEngine;
using UnityEngine.UI;

namespace Noxy.Lobby
{
    /// <summary>
    /// Try on cosmetics (including ones not owned), rotate the character, then confirm to equip.
    /// Nothing is saved until CONFIRM; leaving the screen discards the draft.
    /// </summary>
    public sealed class WardrobeScreen : LobbyScreen
    {
        private static readonly string[] TabLabels = { "OUTFIT", "SHOES", "MASK", "HEAD", "BAG" };
        private static readonly CosmeticSlot[] TabSlots =
            { CosmeticSlot.Outfit, CosmeticSlot.Shoes, CosmeticSlot.Mask, CosmeticSlot.Headgear, CosmeticSlot.Backpack };

        private Loadout draft;
        private CosmeticSlot activeSlot = CosmeticSlot.Outfit;
        private TabBar tabs;
        private Text slotHeader, info;
        private RectTransform gridContent;
        private ButtonView confirm, revert;

        public override LobbyScreenId Id => LobbyScreenId.Wardrobe;
        public override ScreenStyle Style => ScreenStyle.SidePanel;
        public override string Title => "Wardrobe";

        public override CameraDetail Detail
        {
            get
            {
                switch (activeSlot)
                {
                    case CosmeticSlot.Mask:
                    case CosmeticSlot.Headgear: return CameraDetail.Head;
                    case CosmeticSlot.Shoes: return CameraDetail.Feet;
                    default: return CameraDetail.Full;
                }
            }
        }

        protected override void BuildContent(RectTransform parent)
        {
            tabs = TabBar.Create(parent, TabLabels, index => SelectSlot(TabSlots[index]));
            UIKit.Anchor(tabs.Root, new Vector2(0f, 1f), Vector2.one, new Vector2(0.5f, 1f), Vector2.zero, new Vector2(0f, 60f));

            slotHeader = UIKit.NewText(parent, "", UITheme.FontBody, UITheme.Accent, TextAnchor.MiddleLeft, FontStyle.Bold, "SlotHeader");
            UIKit.Anchor(slotHeader.rectTransform, new Vector2(0f, 1f), Vector2.one, new Vector2(0.5f, 1f), new Vector2(0f, -64f), new Vector2(0f, 40f));

            var scroll = UIKit.NewScroll(parent, "Items", out gridContent);
            UIKit.Stretch((RectTransform)scroll.transform, 0f, 172f, 0f, 108f);
            UIKit.NewGrid(gridContent.gameObject, 10f);
            var rg = gridContent.gameObject.AddComponent<ResponsiveGrid>();
            rg.MinCellWidth = 140f;
            rg.CellHeight = 160f;
            rg.MaxColumns = 4;

            info = UIKit.NewText(parent, "", UITheme.FontCaption, UITheme.TextDim, TextAnchor.MiddleLeft, FontStyle.Normal, "Info");
            UIKit.Anchor(info.rectTransform, new Vector2(0f, 0f), new Vector2(1f, 0f), new Vector2(0.5f, 0f), new Vector2(0f, 84f), new Vector2(0f, 80f));
            UIKit.FitText(info, 18);

            var row = UIKit.NewRect("Buttons", parent);
            UIKit.Anchor(row, new Vector2(0f, 0f), new Vector2(1f, 0f), new Vector2(0.5f, 0f), Vector2.zero, new Vector2(0f, 72f));
            var layout = UIKit.HStack(row.gameObject, 8f);
            layout.childForceExpandWidth = true;

            var reset = UIKit.NewButton(row, "ResetView", "RESET VIEW", UITheme.Button, () => Ctx.Preview.ResetView(), UITheme.FontCaption + 2);
            UIKit.SetLayout(reset.gameObject, minWidth: 0f, flexibleWidth: 1f);
            revert = UIKit.NewButton(row, "Revert", "REVERT", UITheme.Button, Revert, UITheme.FontCaption + 2);
            UIKit.SetLayout(revert.gameObject, minWidth: 0f, flexibleWidth: 1f);
            confirm = UIKit.NewButton(row, "Confirm", "CONFIRM", UITheme.Accent, Confirm, UITheme.FontButton);
            UIKit.SetLayout(confirm.gameObject, minWidth: 0f, flexibleWidth: 1.4f);
        }

        protected override void OnShown()
        {
            draft = AppServices.Profile.Profile.loadout.Clone();
            Ctx.Preview.ShowLoadout(draft);
            SelectSlot(activeSlot);
        }

        protected override void OnHidden()
        {
            if (draft == null) return;

            bool hadChanges = !draft.SameAs(AppServices.Profile.Profile.loadout);
            draft = null;
            Ctx.Preview.ShowLoadout(AppServices.Profile.Profile.loadout);
            if (hadChanges)
                AppServices.Notifications.Post("Wardrobe", "Unconfirmed changes were discarded.", NotificationSeverity.Info);
        }

        private void SelectSlot(CosmeticSlot slot)
        {
            activeSlot = slot;
            tabs.SetSelected(Array.IndexOf(TabSlots, slot));
            slotHeader.text = slot.DisplayName().ToUpperInvariant();
            RebuildTiles();
            RefreshState();
            Ctx.Screens.Reframe();
        }

        private void RebuildTiles()
        {
            UIKit.DestroyChildren(gridContent);
            if (draft == null) return;

            if (activeSlot.IsOptional())
            {
                bool none = string.IsNullOrEmpty(draft.Get(activeSlot));
                string state = none ? "SELECTED" : "EMPTY";
                CosmeticTile.Create(gridContent, null, state, none ? UITheme.Accent : UITheme.TextDim, none, () => Choose(""));
            }

            string equipped = AppServices.Profile.Profile.loadout.Get(activeSlot);
            foreach (var item in AppServices.Catalog.InSlot(activeSlot))
            {
                var captured = item;
                bool owned = AppServices.Profile.Owns(item.Id);
                bool selected = draft.Get(activeSlot) == item.Id;

                string state;
                Color color;
                if (equipped == item.Id) { state = "EQUIPPED"; color = UITheme.Success; }
                else if (owned) { state = "OWNED"; color = UITheme.Cyan; }
                else { state = "LOCKED  " + item.PriceCredits.ToString("N0"); color = UITheme.Warning; }

                CosmeticTile.Create(gridContent, item, state, color, selected, () => Choose(captured.Id));
            }
        }

        private void Choose(string itemId)
        {
            if (draft == null) return;
            draft.Set(activeSlot, itemId);
            Ctx.Preview.ShowLoadout(draft);
            RebuildTiles();
            RefreshState();
        }

        private bool DraftOwned(out string missing)
        {
            missing = null;
            foreach (CosmeticSlot slot in Enum.GetValues(typeof(CosmeticSlot)))
            {
                string id = draft.Get(slot);
                if (!string.IsNullOrEmpty(id) && !AppServices.Profile.Owns(id))
                {
                    AppServices.Catalog.TryGet(id, out var item);
                    missing = item != null ? item.DisplayName : id;
                    return false;
                }
            }
            return true;
        }

        private void RefreshState()
        {
            if (draft == null) return;

            bool changed = !draft.SameAs(AppServices.Profile.Profile.loadout);
            bool owned = DraftOwned(out string missing);

            confirm.SetInteractable(changed && owned);
            revert.SetInteractable(changed);

            if (!owned)
            {
                info.text = "<color=#FFC742>" + missing + " is not owned.</color> You can preview it, but it can't be equipped. Purchases aren't available yet (store not connected).";
            }
            else if (changed)
            {
                info.text = "Previewing changes. Drag the character to inspect, then press CONFIRM to equip.";
            }
            else
            {
                info.text = "Pick an item to try it on. Drag the character to rotate it.";
            }
        }

        private void Revert()
        {
            if (draft == null) return;
            draft = AppServices.Profile.Profile.loadout.Clone();
            Ctx.Preview.ShowLoadout(draft);
            RebuildTiles();
            RefreshState();
        }

        private void Confirm()
        {
            if (draft == null) return;
            var result = AppServices.Profile.EquipLoadout(draft);
            if (result.Success)
            {
                AppServices.Notifications.Post("Loadout updated", "Your new look has been equipped.", NotificationSeverity.Success);
                draft = AppServices.Profile.Profile.loadout.Clone();
                RebuildTiles();
                RefreshState();
                info.text = "<color=#5CDB85>Equipped.</color> Your new look is saved to the local profile.";
            }
            else
            {
                AppServices.Notifications.Post("Couldn't equip", result.Error, NotificationSeverity.Error);
                info.text = "<color=#F55257>" + result.Error + "</color>";
            }
        }
    }
}
