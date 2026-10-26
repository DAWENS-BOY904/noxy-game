using System;
using System.Collections.Generic;
using Noxy.Core;
using UnityEngine;
using UnityEngine.UI;

namespace Noxy.Lobby
{
    /// <summary>
    /// Browse cosmetics by category, try them on the 3D character (including locked ones), rotate/inspect, then
    /// confirm to equip. Ownership comes from the profile (server-validated when signed in). Nothing is saved
    /// until CONFIRM; leaving the screen discards the draft. Purchasing is not implemented and says so.
    /// </summary>
    public sealed class VaultScreen : LobbyScreen
    {
        private static readonly string[] TabLabels = { "OUTFIT", "SHOES", "MASK", "HEAD", "BAG", "WEAPON", "EMOTE" };
        private static readonly CosmeticSlot[] TabSlots =
        {
            CosmeticSlot.Outfit, CosmeticSlot.Shoes, CosmeticSlot.Mask, CosmeticSlot.Headgear,
            CosmeticSlot.Backpack, CosmeticSlot.WeaponSkin, CosmeticSlot.Emote
        };
        private static readonly string[] SortLabels = { "RARITY", "NAME", "OWNED" };
        private static readonly string[] FilterLabels = { "ALL", "OWNED", "LOCKED" };

        private Loadout draft;
        private CosmeticSlot activeSlot = CosmeticSlot.Outfit;
        private TabBar tabs;
        private Text info;
        private InputField search;
        private ButtonView sortButton, filterButton, confirm, revert;
        private RectTransform gridContent;
        private int sortMode, filterMode;

        public override LobbyScreenId Id => LobbyScreenId.Vault;
        public override ScreenStyle Style => ScreenStyle.SidePanel;
        public override string Title => "Vault";

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
            tabs = TabBar.Create(parent, TabLabels, index => SelectSlot(TabSlots[index]), 18);
            UIKit.Anchor(tabs.Root, new Vector2(0f, 1f), Vector2.one, new Vector2(0.5f, 1f), Vector2.zero, new Vector2(0f, 56f));

            var row = UIKit.NewRect("SearchRow", parent);
            UIKit.Anchor(row, new Vector2(0f, 1f), Vector2.one, new Vector2(0.5f, 1f), new Vector2(0f, -64f), new Vector2(0f, 56f));
            UIKit.HStack(row.gameObject, 8f);
            search = UIForm.NewInput(row, "Search", "Search items", InputField.ContentType.Standard, 24);
            UIKit.SetLayout(search.gameObject, minWidth: 0f, flexibleWidth: 1f);
            search.onValueChanged.AddListener(_ => RebuildTiles());
            sortButton = UIKit.NewButton(row, "Sort", "SORT: RARITY", UITheme.Button, () => { sortMode = (sortMode + 1) % SortLabels.Length; RebuildTiles(); }, 18);
            UIKit.SetLayout(sortButton.gameObject, preferredWidth: 170f, flexibleWidth: 0f);
            filterButton = UIKit.NewButton(row, "Filter", "SHOW: ALL", UITheme.Button, () => { filterMode = (filterMode + 1) % FilterLabels.Length; RebuildTiles(); }, 18);
            UIKit.SetLayout(filterButton.gameObject, preferredWidth: 150f, flexibleWidth: 0f);

            var scroll = UIKit.NewScroll(parent, "Items", out gridContent);
            UIKit.Stretch((RectTransform)scroll.transform, 0f, 172f, 0f, 130f);
            UIKit.NewGrid(gridContent.gameObject, 10f);
            var rg = gridContent.gameObject.AddComponent<ResponsiveGrid>();
            rg.MinCellWidth = 140f;
            rg.CellHeight = 160f;
            rg.MaxColumns = 4;

            info = UIKit.NewText(parent, "", UITheme.FontCaption, UITheme.TextDim, TextAnchor.MiddleLeft, FontStyle.Normal, "Info");
            UIKit.Anchor(info.rectTransform, new Vector2(0f, 0f), new Vector2(1f, 0f), new Vector2(0.5f, 0f), new Vector2(0f, 84f), new Vector2(0f, 80f));
            UIKit.FitText(info, 18);

            var buttons = UIKit.NewRect("Buttons", parent);
            UIKit.Anchor(buttons, new Vector2(0f, 0f), new Vector2(1f, 0f), new Vector2(0.5f, 0f), Vector2.zero, new Vector2(0f, 72f));
            UIKit.HStack(buttons.gameObject, 8f).childForceExpandWidth = true;

            var reset = UIKit.NewButton(buttons, "ResetView", "RESET VIEW", UITheme.Button, () => Ctx.Preview.ResetView(), UITheme.FontCaption + 2);
            UIKit.SetLayout(reset.gameObject, minWidth: 0f, flexibleWidth: 1f);
            revert = UIKit.NewButton(buttons, "Revert", "REVERT", UITheme.Button, Revert, UITheme.FontCaption + 2);
            UIKit.SetLayout(revert.gameObject, minWidth: 0f, flexibleWidth: 1f);
            confirm = UIKit.NewButton(buttons, "Confirm", "EQUIP", UITheme.Accent, Confirm, UITheme.FontButton);
            UIKit.SetLayout(confirm.gameObject, minWidth: 0f, flexibleWidth: 1.4f);

            AppServices.Profile.ProfileChanged += OnProfileChanged;
        }

        private void OnDestroy()
        {
            if (AppServices.Profile != null) AppServices.Profile.ProfileChanged -= OnProfileChanged;
        }

        // Server data can arrive while the Vault is open (sign in, or an equip the server rejected).
        private void OnProfileChanged()
        {
            if (draft == null) return;
            draft = AppServices.Profile.Profile.loadout.Clone();
            Ctx.Preview.ShowLoadout(draft);
            RebuildTiles();
            RefreshState();
        }

        protected override void OnShown()
        {
            draft = AppServices.Profile.Profile.loadout.Clone();
            Ctx.Preview.ShowLoadout(draft);
            search.SetTextWithoutNotify("");
            SelectSlot(activeSlot);
        }

        protected override void OnHidden()
        {
            if (draft == null) return;

            bool hadChanges = !draft.SameAs(AppServices.Profile.Profile.loadout);
            draft = null;
            Ctx.Preview.ShowLoadout(AppServices.Profile.Profile.loadout);
            if (hadChanges)
                AppServices.Notifications.Post("Vault", "Unconfirmed changes were discarded.", NotificationSeverity.Info);
        }

        private void SelectSlot(CosmeticSlot slot)
        {
            activeSlot = slot;
            tabs.SetSelected(Array.IndexOf(TabSlots, slot));
            RebuildTiles();
            RefreshState();
            Ctx.Screens.Reframe();
        }

        private List<CosmeticItem> VisibleItems()
        {
            var list = new List<CosmeticItem>();
            string q = search != null ? search.text.Trim() : "";
            foreach (var item in AppServices.Catalog.InSlot(activeSlot))
            {
                bool owned = AppServices.Profile.Owns(item.Id);
                if (filterMode == 1 && !owned) continue;
                if (filterMode == 2 && owned) continue;
                if (q.Length > 0 && item.DisplayName.IndexOf(q, StringComparison.OrdinalIgnoreCase) < 0) continue;
                list.Add(item);
            }

            list.Sort((a, b) =>
            {
                int c = 0;
                if (sortMode == 0) c = ((int)b.Rarity).CompareTo((int)a.Rarity);
                else if (sortMode == 2) c = AppServices.Profile.Owns(b.Id).CompareTo(AppServices.Profile.Owns(a.Id));
                return c != 0 ? c : string.Compare(a.DisplayName, b.DisplayName, StringComparison.OrdinalIgnoreCase);
            });
            return list;
        }

        private void RebuildTiles()
        {
            if (gridContent == null) return;
            sortButton.SetLabel("SORT: " + SortLabels[sortMode]);
            filterButton.SetLabel("SHOW: " + FilterLabels[filterMode]);

            UIKit.DestroyChildren(gridContent);
            if (draft == null) return;

            bool searching = search.text.Trim().Length > 0 || filterMode != 0;
            if (activeSlot.IsOptional() && !searching)
            {
                bool none = string.IsNullOrEmpty(draft.Get(activeSlot));
                CosmeticTile.Create(gridContent, null, none ? "SELECTED" : "UNEQUIP", none ? UITheme.Accent : UITheme.TextDim, none, () => Choose(""));
            }

            string equipped = AppServices.Profile.Profile.loadout.Get(activeSlot);
            var items = VisibleItems();
            foreach (var item in items)
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

            if (items.Count == 0)
            {
                var empty = UIKit.NewText(gridContent, "No items match.", UITheme.FontBody, UITheme.TextDim, TextAnchor.MiddleCenter);
                UIKit.SetLayout(empty.gameObject, preferredHeight: 100f);
            }
        }

        private void Choose(string itemId)
        {
            if (draft == null) return;
            draft.Set(activeSlot, itemId);
            Ctx.Preview.ShowLoadout(draft);
            if (activeSlot == CosmeticSlot.Emote && !string.IsNullOrEmpty(itemId)) Ctx.Preview.PlayEmote(itemId);
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

            string online = AppServices.Profile.IsServerBound ? "" : "  (offline profile)";
            if (!owned)
                info.text = "<color=#FFC742>" + missing + " is locked.</color> You can preview it, but only owned items can be equipped. Purchasing isn't available yet (no store backend)." + online;
            else if (changed)
                info.text = "Previewing changes. Drag the character to inspect, then press EQUIP." + online;
            else
                info.text = "Pick an item to try it on. Drag the character to rotate it." + online;
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
                string note = AppServices.Profile.IsServerBound ? "Saved to your account." : "Saved on this device (offline profile).";
                AppServices.Notifications.Post("Loadout updated", note, NotificationSeverity.Success);
                draft = AppServices.Profile.Profile.loadout.Clone();
                RebuildTiles();
                RefreshState();
                info.text = "<color=#5CDB85>Equipped.</color> " + note;
            }
            else
            {
                AppServices.Notifications.Post("Couldn't equip", result.Error, NotificationSeverity.Error);
                info.text = "<color=#F55257>" + result.Error + "</color>";
            }
        }
    }
}
