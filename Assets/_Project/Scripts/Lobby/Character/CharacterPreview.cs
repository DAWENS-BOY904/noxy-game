using Noxy.Core;
using UnityEngine;
using UnityEngine.EventSystems;

namespace Noxy.Lobby
{
    /// <summary>
    /// Owns the 3D character shown in the lobby: displays a loadout (equipped or draft), handles drag
    /// rotation, gentle idle sway in the lobby and free inspection in wardrobe/profile.
    /// </summary>
    public sealed class CharacterPreview : MonoBehaviour
    {
        private const float DragDegreesPerPixel = 0.45f;
        private const float ReturnDelaySeconds = 2.5f;

        private ICharacterView view;
        private Transform viewRoot;
        private ICosmeticCatalog catalog;
        private Loadout shown;

        private float userYaw;
        private float idleTimer;
        private bool inspectMode;
        private bool dragging;

        public Loadout ShownLoadout => shown;

        public void Initialize(ICosmeticCatalog catalog)
        {
            this.catalog = catalog;
            view = CharacterViewFactory.Create(transform);
            viewRoot = view != null ? view.Root : null;
        }

        public void ShowLoadout(Loadout loadout)
        {
            if (view == null || catalog == null || loadout == null) return;
            shown = loadout.Clone();
            view.Apply(shown, catalog);
        }

        public void SetInspectMode(bool inspect)
        {
            inspectMode = inspect;
            idleTimer = 0f;
        }

        public void ResetView()
        {
            returningToFront = true;
        }

        public void PlayEmote(string cosmeticId)
        {
            if (view != null && catalog != null && catalog.TryGet(cosmeticId, out var item) && item.Slot == CosmeticSlot.Emote) view.PlayEmote(item.Variant);
        }

        private bool returningToFront;

        public void BeginDrag()
        {
            dragging = true;
            returningToFront = false;
        }

        public void Drag(float deltaPixelsX)
        {
            userYaw -= deltaPixelsX * DragDegreesPerPixel;
            idleTimer = 0f;
        }

        public void EndDrag()
        {
            dragging = false;
            idleTimer = 0f;
        }

        private void Update()
        {
            if (viewRoot == null) return;

            float dt = Time.unscaledDeltaTime;

            float key = InputCompat.RotateAxis;
            if (Mathf.Abs(key) > 0.01f)
            {
                userYaw += key * 120f * dt;
                idleTimer = 0f;
                returningToFront = false;
            }

            idleTimer += dt;

            bool autoRotate = LobbySettings.AutoRotateCharacter;
            if (returningToFront || (!inspectMode && autoRotate && !dragging && idleTimer > ReturnDelaySeconds))
            {
                userYaw = Mathf.MoveTowardsAngle(userYaw, 0f, 80f * dt);
                if (Mathf.Abs(Mathf.DeltaAngle(userYaw, 0f)) < 0.5f) returningToFront = false;
            }

            float sway = (!inspectMode && autoRotate && !dragging) ? Mathf.Sin(Time.unscaledTime * 0.55f) * 22f : 0f;
            float target = userYaw + sway;
            var current = viewRoot.localRotation;
            var desired = Quaternion.Euler(0f, target, 0f);
            viewRoot.localRotation = Quaternion.Slerp(current, desired, 1f - Mathf.Exp(-14f * dt));
        }
    }

    /// <summary>Full-screen transparent layer behind the HUD; drag it (mouse or touch) to rotate the character.</summary>
    public sealed class CharacterDragArea : MonoBehaviour, IBeginDragHandler, IDragHandler, IEndDragHandler
    {
        public CharacterPreview Preview;

        public void OnBeginDrag(PointerEventData eventData) => Preview.BeginDrag();
        public void OnDrag(PointerEventData eventData) => Preview.Drag(eventData.delta.x);
        public void OnEndDrag(PointerEventData eventData) => Preview.EndDrag();
    }
}
