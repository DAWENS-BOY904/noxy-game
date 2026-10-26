using UnityEngine;
using UnityEngine.UI;

namespace Noxy.Lobby
{
    /// <summary>
    /// Entry point of the MainLobby scene. Builds the stage, character, UI and navigation at startup.
    /// The scene itself only needs a camera and this component, so it is easy to regenerate.
    /// </summary>
    public sealed class LobbyBootstrap : MonoBehaviour
    {
        private void Awake()
        {
            AppServices.EnsureInitialized();
            EventSystemFactory.Ensure();
            ConfigurePlatform();

            var cam = Camera.main;
            if (cam == null)
            {
                var camGo = new GameObject("Main Camera", typeof(Camera), typeof(AudioListener));
                camGo.tag = "MainCamera";
                cam = camGo.GetComponent<Camera>();
            }
            var rig = cam.gameObject.AddComponent<CameraRig>();
            rig.Initialize(cam);

            LobbyStage.Create();

            var previewGo = new GameObject("CharacterPreview");
            var preview = previewGo.AddComponent<CharacterPreview>();
            preview.Initialize(AppServices.Catalog);
            preview.ShowLoadout(AppServices.Profile.Profile.loadout);

            LobbyCanvas.Create("LobbyCanvas", 0, out var responsive, out var safeRoot);
            var canvasRoot = responsive.transform;

            // Behind the HUD: dragging any empty area rotates the character (mouse or touch).
            var drag = UIKit.NewImage(canvasRoot, "CharacterDragArea", new Color(0f, 0f, 0f, 0f), null, true);
            UIKit.Stretch(drag.rectTransform);
            drag.transform.SetAsFirstSibling();
            drag.gameObject.AddComponent<CharacterDragArea>().Preview = preview;

            var ctx = new LobbyContext
            {
                Canvas = responsive,
                SafeRoot = safeRoot,
                Preview = preview,
                Camera = rig
            };

            var overlay = MatchmakingOverlay.Create(canvasRoot);
            var presenter = new GameObject("MatchmakingPresenter").AddComponent<MatchmakingPresenter>();
            presenter.Initialize(overlay);
            ctx.Matchmaking = presenter;

            var screens = new GameObject("ScreenManager").AddComponent<ScreenManager>();
            ctx.Screens = screens;
            screens.Initialize(ctx);
            screens.BackInterceptor = presenter.HandleBack;

            screens.Register(LobbyScreen.Create<LobbyHud>(ctx, safeRoot));
            screens.Register(LobbyScreen.Create<ProfileScreen>(ctx, safeRoot));
            screens.Register(LobbyScreen.Create<WardrobeScreen>(ctx, safeRoot));
            screens.Register(LobbyScreen.Create<InventoryScreen>(ctx, safeRoot));
            screens.Register(LobbyScreen.Create<FriendsScreen>(ctx, safeRoot));
            screens.Register(LobbyScreen.Create<PartyScreen>(ctx, safeRoot));
            screens.Register(LobbyScreen.Create<MissionsScreen>(ctx, safeRoot));
            screens.Register(LobbyScreen.Create<RewardsScreen>(ctx, safeRoot));
            screens.Register(LobbyScreen.Create<ShopScreen>(ctx, safeRoot));
            screens.Register(LobbyScreen.Create<SettingsScreen>(ctx, safeRoot));
            screens.Register(LobbyScreen.Create<NotificationsScreen>(ctx, safeRoot));
            screens.ShowInitial();

            // Matchmaking overlay must render above every screen.
            overlay.transform.SetAsLastSibling();
        }

        private static void ConfigurePlatform()
        {
            Screen.sleepTimeout = SleepTimeout.NeverSleep;
            Screen.autorotateToPortrait = true;
            Screen.autorotateToLandscapeLeft = true;
            Screen.autorotateToLandscapeRight = true;
            Screen.autorotateToPortraitUpsideDown = false;
            Screen.orientation = ScreenOrientation.AutoRotation;
            if (Application.isMobilePlatform)
            {
                QualitySettings.vSyncCount = 0;
                Application.targetFrameRate = 60;
            }
        }
    }
}
