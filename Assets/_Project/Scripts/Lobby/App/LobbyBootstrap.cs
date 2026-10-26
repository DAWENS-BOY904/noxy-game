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
            var background = LobbyBackground.Create(cam, BackgroundLibrary.Lobby);

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
            ctx.Dialog = DialogOverlay.Create(canvasRoot);

            var screens = new GameObject("ScreenManager").AddComponent<ScreenManager>();
            ctx.Screens = screens;
            screens.Initialize(ctx);
            screens.BackInterceptor = () =>
            {
                if (ctx.Dialog.IsVisible) { ctx.Dialog.Close(); return true; }
                return presenter.HandleBack();
            };

            screens.Register(LobbyScreen.Create<LobbyHud>(ctx, safeRoot));
            screens.Register(LobbyScreen.Create<ProfileScreen>(ctx, safeRoot));
            screens.Register(LobbyScreen.Create<PlayerProfileScreen>(ctx, safeRoot));
            screens.Register(LobbyScreen.Create<VaultScreen>(ctx, safeRoot));
            screens.Register(LobbyScreen.Create<InventoryScreen>(ctx, safeRoot));
            screens.Register(LobbyScreen.Create<FriendsScreen>(ctx, safeRoot));
            screens.Register(LobbyScreen.Create<PartyScreen>(ctx, safeRoot));
            screens.Register(LobbyScreen.Create<InvitationsScreen>(ctx, safeRoot));
            screens.Register(LobbyScreen.Create<MissionsScreen>(ctx, safeRoot));
            screens.Register(LobbyScreen.Create<RewardsScreen>(ctx, safeRoot));
            screens.Register(LobbyScreen.Create<PerformanceScreen>(ctx, safeRoot));
            screens.Register(LobbyScreen.Create<ShopScreen>(ctx, safeRoot));
            screens.Register(LobbyScreen.Create<SettingsScreen>(ctx, safeRoot));
            screens.Register(LobbyScreen.Create<HudEditorScreen>(ctx, safeRoot));
            screens.Register(LobbyScreen.Create<NotificationsScreen>(ctx, safeRoot));
        screens.Register(LobbyScreen.Create<MatchResultsScreen>(ctx, safeRoot));
            screens.Register(LobbyScreen.Create<ClansScreen>(ctx, safeRoot));
            screens.Register(LobbyScreen.Create<ClanWarsScreen>(ctx, safeRoot));
            screens.Register(LobbyScreen.Create<CompanionsScreen>(ctx, safeRoot));
            screens.Register(LobbyScreen.Create<ReputationScreen>(ctx, safeRoot));
            screens.Register(LobbyScreen.Create<ServersScreen>(ctx, safeRoot));
            screens.ShowInitial();
            screens.Changed += (from, to) => background.SetKey(to == LobbyScreenId.Lobby || to == LobbyScreenId.Vault || to == LobbyScreenId.Profile
                ? BackgroundLibrary.Lobby : BackgroundLibrary.Menu);

            // Dialog and matchmaking overlays must render above every screen.
            overlay.transform.SetAsLastSibling();

            // The sign-in gate covers the lobby until a session is verified or the player chooses offline.
            var gate = AuthGate.Create(canvasRoot, background);
            ctx.Dialog.transform.SetAsLastSibling();
            if (AppServices.Auth.State == Noxy.Core.AuthState.SignedIn) gate.gameObject.SetActive(false);
            else AuthGate.Show();
            ctx.Dialog.transform.SetAsLastSibling();
            AppServices.Auth.Restore();
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
