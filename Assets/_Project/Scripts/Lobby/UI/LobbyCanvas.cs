using UnityEngine;
using UnityEngine.UI;

namespace Noxy.Lobby
{
    /// <summary>Creates a screen-space canvas with responsive scaling and a safe-area root.</summary>
    public static class LobbyCanvas
    {
        public static Canvas Create(string name, int sortingOrder, out ResponsiveCanvas responsive, out RectTransform safeRoot)
        {
            var go = new GameObject(name, typeof(RectTransform), typeof(Canvas), typeof(CanvasScaler), typeof(GraphicRaycaster));
            var canvas = go.GetComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = sortingOrder;

            safeRoot = UIKit.NewRect("SafeArea", go.transform);
            responsive = go.AddComponent<ResponsiveCanvas>();
            responsive.Initialize(go.GetComponent<CanvasScaler>(), safeRoot);
            return canvas;
        }
    }
}
