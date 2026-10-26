using System.Collections;
using Noxy.Core;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Noxy.Lobby
{
    /// <summary>
    /// Connects the lobby UI to MatchmakingController: starts the PLAY flow, mirrors status into the
    /// overlay and notifications, and loads the assigned scene. No networking lives here.
    /// </summary>
    public sealed class MatchmakingPresenter : MonoBehaviour
    {
        private MatchmakingController controller;
        private MatchmakingOverlay overlay;
        private bool loadingScene;

        public bool IsOverlayVisible => overlay != null && overlay.IsVisible;

        public void Initialize(MatchmakingOverlay overlay)
        {
            this.overlay = overlay;
            controller = AppServices.Matchmaking;
            if (controller == null || overlay == null)
            {
                Debug.LogError("[Noxy] Matchmaking presenter could not initialize because its controller or overlay is missing.", this);
                enabled = false;
                return;
            }

            controller.Reset();
            controller.StatusChanged += OnStatus;
            this.overlay.CancelClicked += OnCancel;
            this.overlay.RetryClicked += OnRetry;
            this.overlay.CloseClicked += OnClose;
        }

        private void OnDestroy()
        {
            if (controller != null) controller.StatusChanged -= OnStatus;
            if (overlay != null)
            {
                overlay.CancelClicked -= OnCancel;
                overlay.RetryClicked -= OnRetry;
                overlay.CloseClicked -= OnClose;
            }
        }

        private void Update()
        {
            if (this == null || controller == null) return;
            controller.Tick(Time.unscaledDeltaTime);
        }

        public void Play()
        {
            if (AppServices.Modes == null || AppServices.Profile == null || AppServices.Profile.Profile == null)
            {
                AppServices.Notifications?.Post("Matchmaking unavailable", "Player or mode data is not initialized.", NotificationSeverity.Error);
                return;
            }
            var mode = AppServices.Modes.Selected;
            if (mode == null)
            {
                AppServices.Notifications?.Post("Matchmaking unavailable", "Select a game mode before playing.", NotificationSeverity.Warning);
                return;
            }
            loadingScene = false;

            var social = AppServices.Social;
            var snap = social != null ? social.Snapshot : null;
            if (social != null && social.IsAvailable && snap != null && snap.InParty && snap.Party != null && snap.Party.members.Count > mode.MaxPartySize)
            {
                AppServices.Notifications.Post("Can't start match",
                    mode.DisplayName + " is a solo mode. Leave your party first.", NotificationSeverity.Warning);
                return;
            }

            if (controller == null)
            {
                AppServices.Notifications.Post("Matchmaking unavailable", "The matchmaking controller is not initialized.", NotificationSeverity.Error);
                return;
            }
            if (!controller.TryStart(mode, AppServices.Profile.Profile.playerId, out string reason))
                AppServices.Notifications.Post("Can't start match", reason, NotificationSeverity.Warning);
        }

        /// <summary>Back/Esc while the overlay is open: cancel a search or close an error. Returns true if handled.</summary>
        public bool HandleBack()
        {
            if (!IsOverlayVisible) return false;
            if (controller == null) return false;
            var status = controller.Status;
            if (status.IsActive) OnCancel();
            else if (status.State == MatchmakingState.Failed) OnClose();
            return true;
        }

        private void OnCancel() => controller?.Cancel();

        private void OnRetry()
        {
            controller?.Reset();
            Play();
        }

        private void OnClose()
        {
            controller?.Reset();
            if (overlay != null) overlay.Hide();
        }

        private void OnStatus(MatchmakingStatus status)
        {
            switch (status.State)
            {
                case MatchmakingState.Searching:
                case MatchmakingState.Connecting:
                    if (overlay != null) overlay.Present(status);
                    break;

                case MatchmakingState.Succeeded:
                    if (overlay != null) overlay.Present(status);
                    if (!loadingScene && status.Assignment != null) StartCoroutine(LoadAssignedScene(status.Assignment));
                    break;

                case MatchmakingState.Failed:
                    loadingScene = false;
                    if (overlay != null) overlay.Present(status);
                    AppServices.Notifications?.Post("Matchmaking failed", status.Message, NotificationSeverity.Error, false);
                    break;

                case MatchmakingState.Cancelled:
                    if (overlay != null) overlay.Hide();
                    AppServices.Notifications?.Post("Matchmaking cancelled", "You left the queue.", NotificationSeverity.Info);
                    break;

                default:
                    if (overlay != null) overlay.Hide();
                    break;
            }
        }

        private IEnumerator LoadAssignedScene(MatchAssignment assignment)
        {
            loadingScene = true;
            yield return null;

            if (string.IsNullOrEmpty(assignment.SceneName) || !Application.CanStreamedLevelBeLoaded(assignment.SceneName))
            {
                AppServices.Matchmaking.Fail(MatchmakingError.SceneLoadFailed, $"Scene '{assignment.SceneName}' is not available in this build.");
                yield break;
            }

            var op = SceneManager.LoadSceneAsync(assignment.SceneName);
            if (op == null)
            {
                loadingScene = false;
                AppServices.Matchmaking.Fail(MatchmakingError.SceneLoadFailed, $"Unity could not begin loading scene '{assignment.SceneName}'.");
                return;
            }
            while (op != null && !op.isDone)
            {
                overlay.SetLoadingProgress(op.progress / 0.9f);
                yield return null;
            }
        }
    }
}
