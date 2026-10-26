using UnityEngine;
using UnityEngine.UI;

namespace Noxy.Lobby
{
    public enum ScreenStyle
    {
        Hud,
        Modal,
        SidePanel
    }

    /// <summary>
    /// Base for every lobby screen. Subclasses only implement BuildContent; the base creates the
    /// panel chrome (title, Back button), sizes it for the current layout and handles show/hide.
    /// </summary>
    public abstract class LobbyScreen : MonoBehaviour
    {
        protected LobbyContext Ctx { get; private set; }
        protected RectTransform PanelRect { get; private set; }
        protected RectTransform Body { get; private set; }

        private UIFader fader;
        private bool built;

        public abstract LobbyScreenId Id { get; }
        public virtual ScreenStyle Style => ScreenStyle.Modal;
        public virtual string Title => Id.ToString();
        public virtual CameraDetail Detail => CameraDetail.Full;

        /// <summary>Whether the user is inspecting the character (manual rotation, no idle sway).</summary>
        public virtual bool InspectMode => Style == ScreenStyle.SidePanel;

        /// <summary>
        /// Region of the safe area (0-1) that is NOT covered by UI. The 3D character is framed inside it.
        /// </summary>
        public virtual Rect FreeArea
        {
            get
            {
                if (Style != ScreenStyle.SidePanel) return new Rect(0f, 0f, 1f, 1f);
                Vector2 s = Ctx.Size;
                if (Ctx.Portrait)
                {
                    float panel = PanelHeight(s) + 16f;
                    return new Rect(0f, Mathf.Clamp01(panel / s.y), 1f, 1f - Mathf.Clamp01(panel / s.y));
                }
                float w = (PanelWidth(s) + 16f) / s.x;
                return new Rect(0f, 0f, 1f - Mathf.Clamp01(w), 1f);
            }
        }

        public static T Create<T>(LobbyContext ctx, Transform parent) where T : LobbyScreen
        {
            var go = new GameObject(typeof(T).Name, typeof(RectTransform), typeof(CanvasGroup));
            var rt = (RectTransform)go.transform;
            rt.SetParent(parent, false);
            UIKit.Stretch(rt);
            go.AddComponent<UIFader>();
            var screen = go.AddComponent<T>();
            screen.Initialize(ctx);
            return screen;
        }

        private void Initialize(LobbyContext ctx)
        {
            Ctx = ctx;
            fader = GetComponent<UIFader>();

            if (Style == ScreenStyle.Modal)
            {
                var dim = UIKit.NewImage(transform, "Dim", new Color(0f, 0f, 0f, 0.6f), null, true);
                UIKit.Stretch(dim.rectTransform);
                var click = dim.gameObject.AddComponent<Button>();
                click.transition = Selectable.Transition.None;
                click.onClick.AddListener(() => Ctx.Screens.Back());
            }

            if (Style != ScreenStyle.Hud)
            {
                var panel = UIKit.NewRounded(transform, "Panel", UITheme.Panel, true);
                PanelRect = panel.rectTransform;
                BuildChrome();
            }

            BuildContent(Body != null ? Body : (RectTransform)transform);
            built = true;

            Ctx.Canvas.LayoutChanged += ApplyLayout;
            ApplyLayout();
        }

        private void OnDestroy()
        {
            if (Ctx != null && Ctx.Canvas != null) Ctx.Canvas.LayoutChanged -= ApplyLayout;
        }

        private void BuildChrome()
        {
            var accent = UIKit.NewImage(PanelRect, "AccentBar", UITheme.Accent);
            UIKit.Anchor(accent.rectTransform, new Vector2(0f, 1f), new Vector2(1f, 1f), new Vector2(0.5f, 1f), Vector2.zero, new Vector2(-48f, 5f));

            var title = UIKit.NewText(PanelRect, Title.ToUpperInvariant(), UITheme.FontTitle, UITheme.Text, TextAnchor.MiddleLeft, FontStyle.Bold, "Title");
            UIKit.Anchor(title.rectTransform, new Vector2(0f, 1f), new Vector2(1f, 1f), new Vector2(0.5f, 1f), new Vector2(0f, -10f), new Vector2(-220f, 72f));
            title.rectTransform.offsetMin = new Vector2(28f, title.rectTransform.offsetMin.y);
            title.rectTransform.offsetMax = new Vector2(-180f, title.rectTransform.offsetMax.y);
            UIKit.FitText(title, 28);

            var back = UIKit.NewButton(PanelRect, "BackButton", "BACK", UITheme.Button, () => Ctx.Screens.Back(), UITheme.FontBody);
            UIKit.Anchor(back.Background.rectTransform, Vector2.one, Vector2.one, Vector2.one, new Vector2(-18f, -14f), new Vector2(150f, 64f));

            Body = UIKit.NewRect("Body", PanelRect);
            UIKit.Stretch(Body, 20f, 20f, 20f, 92f);
        }

        protected abstract void BuildContent(RectTransform parent);

        public void ShowScreen(bool instant)
        {
            fader.Show(instant);
            ApplyLayout();
            OnShown();
        }

        public void HideScreen(bool instant)
        {
            if (built) OnHidden();
            fader.Hide(instant);
        }

        protected virtual void OnShown() { }
        protected virtual void OnHidden() { }

        protected virtual void OnLayout() { }

        protected float PanelWidth(Vector2 s) => Mathf.Clamp(s.x * 0.4f, 480f, 680f);
        protected float PanelHeight(Vector2 s) => s.y * 0.56f;

        private void ApplyLayout()
        {
            if (!built || Ctx == null) return;
            Vector2 s = Ctx.Size;

            if (Style == ScreenStyle.Modal)
            {
                float w = Mathf.Min(s.x - 32f, 1100f);
                float h = Ctx.Portrait ? Mathf.Min(s.y - 160f, 1200f) : Mathf.Min(s.y - 32f, 780f);
                UIKit.Anchor(PanelRect, new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), Vector2.zero, new Vector2(w, h));
            }
            else if (Style == ScreenStyle.SidePanel)
            {
                if (Ctx.Portrait)
                    UIKit.Anchor(PanelRect, new Vector2(0f, 0f), new Vector2(1f, 0f), new Vector2(0.5f, 0f), new Vector2(0f, 16f), new Vector2(-32f, PanelHeight(s)));
                else
                    UIKit.Anchor(PanelRect, new Vector2(1f, 0f), new Vector2(1f, 1f), new Vector2(1f, 0.5f), new Vector2(-16f, 0f), new Vector2(PanelWidth(s), -32f));
            }

            OnLayout();
        }
    }
}
