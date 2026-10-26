using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.EventSystems;

namespace Noxy.Lobby
{
    public enum LobbyScreenId
    {
        Lobby,
        Profile,
        PlayerProfile,
        Vault,
        Inventory,
        Friends,
        Party,
        Invitations,
        Missions,
        Rewards,
        Shop,
        Settings,
        HudEditor,
        Notifications
    }

    /// <summary>Shared references handed to every screen. Avoids singletons inside UI code.</summary>
    public sealed class LobbyContext
    {
        public ResponsiveCanvas Canvas;
        public RectTransform SafeRoot;
        public ScreenManager Screens;
        public CharacterPreview Preview;
        public CameraRig Camera;
        public MatchmakingPresenter Matchmaking;
        public DialogOverlay Dialog;

        public bool Portrait => Canvas.IsPortrait;
        public Vector2 Size => Canvas.SafeSize;

        /// <summary>Opens another player's public profile (from search results, friends or the party).</summary>
        public void ViewPlayer(string publicId)
        {
            if (Screens.TryGet(LobbyScreenId.PlayerProfile, out var screen) && screen is PlayerProfileScreen p)
            {
                p.Target = publicId;
                Screens.Open(LobbyScreenId.PlayerProfile);
            }
        }

        /// <summary>Asks for confirmation unless the player turned confirmations off (destructive actions always ask).</summary>
        public void Confirm(string title, string message, string confirmLabel, System.Action yes, bool destructive = false)
        {
            if (!destructive && !AppServices.Config.Settings.confirmDialogs) { yes?.Invoke(); return; }
            Dialog.Ask(title, message, confirmLabel, yes, destructive);
        }
    }

    /// <summary>Navigation between the lobby HUD and full screens (stack based, supports Back / Esc / Android back).</summary>
    public sealed class ScreenManager : MonoBehaviour
    {
        private readonly Dictionary<LobbyScreenId, LobbyScreen> screens = new Dictionary<LobbyScreenId, LobbyScreen>();
        private readonly Stack<LobbyScreenId> history = new Stack<LobbyScreenId>();
        private LobbyContext ctx;

        public LobbyScreenId Current { get; private set; } = LobbyScreenId.Lobby;
        public event Action<LobbyScreenId, LobbyScreenId> Changed;

        /// <summary>Return true to consume the Back press (e.g. the matchmaking overlay is open).</summary>
        public Func<bool> BackInterceptor;

        public void Initialize(LobbyContext context)
        {
            ctx = context;
            ctx.Canvas.LayoutChanged += Reframe;
        }

        public void Register(LobbyScreen screen) => screens[screen.Id] = screen;

        public bool TryGet(LobbyScreenId id, out LobbyScreen screen) => screens.TryGetValue(id, out screen);

        public void ShowInitial()
        {
            foreach (var kv in screens)
            {
                if (kv.Key == Current) kv.Value.ShowScreen(true);
                else kv.Value.HideScreen(true);
            }
            ctx.Preview.SetInspectMode(false);
            Reframe(true);
        }

        public void Open(LobbyScreenId id)
        {
            if (id == Current || !screens.ContainsKey(id)) return;
            if (id == LobbyScreenId.Lobby)
            {
                history.Clear();
            }
            else
            {
                history.Push(Current);
            }
            Switch(id);
        }

        public bool Back()
        {
            if (Current == LobbyScreenId.Lobby) return false;
            Switch(history.Count > 0 ? history.Pop() : LobbyScreenId.Lobby);
            return true;
        }

        public void Reframe() => Reframe(false);

        public void Reframe(bool instant)
        {
            if (!screens.TryGetValue(Current, out var screen) || ctx.Camera == null) return;
            ctx.Camera.Frame(screen.FreeArea, screen.Detail, instant);
        }

        private void Switch(LobbyScreenId to)
        {
            var from = Current;
            if (screens.TryGetValue(from, out var fromScreen)) fromScreen.HideScreen(false);

            Current = to;
            var toScreen = screens[to];
            toScreen.ShowScreen(false);
            ctx.Preview.SetInspectMode(toScreen.InspectMode);
            Reframe(false);
            Changed?.Invoke(from, to);
        }

        private void Update()
        {
            if (!InputCompat.BackPressed) return;
            if (BackInterceptor != null && BackInterceptor()) return;
            Back();
        }
    }

    /// <summary>Keeps keyboard input working under the legacy Input Manager and compiles under the new Input System.</summary>
    public static class InputCompat
    {
#if ENABLE_LEGACY_INPUT_MANAGER
        public static bool BackPressed => Input.GetKeyDown(KeyCode.Escape);
        public static bool KeyDown(KeyCode key) => Input.GetKeyDown(key);

        public static float RotateAxis
        {
            get
            {
                float v = 0f;
                if (Input.GetKey(KeyCode.A) || Input.GetKey(KeyCode.LeftArrow)) v -= 1f;
                if (Input.GetKey(KeyCode.D) || Input.GetKey(KeyCode.RightArrow)) v += 1f;
                return v;
            }
        }
#else
        public static bool BackPressed => false;
        public static bool KeyDown(KeyCode key) => false;
        public static float RotateAxis => 0f;
#endif
    }

    public static class EventSystemFactory
    {
        public static void Ensure()
        {
            if (UnityEngine.Object.FindFirstObjectByType<EventSystem>() != null) return;

            var go = new GameObject("EventSystem", typeof(EventSystem));
#if ENABLE_INPUT_SYSTEM && !ENABLE_LEGACY_INPUT_MANAGER
            var type = Type.GetType("UnityEngine.InputSystem.UI.InputSystemUIInputModule, Unity.InputSystem");
            if (type != null) go.AddComponent(type);
            else Debug.LogError("[Lobby] Input System package is not installed; UI input will not work.");
#else
            go.AddComponent<StandaloneInputModule>();
#endif
        }
    }
}
