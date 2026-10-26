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
        private MatchmakingOverlay overlay;
        private bool loadingScene;

        public bool IsOverlayVisible => overlay != null && overlay.IsVisible;

        public void Initialize(MatchmakingOverlay overlay)
        {
            this.overlay = overlay;
            AppServices.Matchmaking.Reset();
            AppServices.Matchmaking.StatusChanged += OnStatus;
            overlay.CancelClicked += OnCancel;
            overlay.RetryClicked += OnRetry;
            overlay.CloseClicked += OnClose;
        }

        private void OnDestroy()
        {
            if (AppServices.Matchmaking != null) AppServices.Matchmaking.StatusChanged -= OnStatus;
        }

        private void Update()
        {
            if (AppServices.Matchmaking != null) AppServices.Matchmaking.Tick(Time.unscaledDeltaTime);
        }

        public void Play()
        {
            var mode = AppServices.Modes.Selected;
            loadingScene = false;

            var snap = AppServices.Social.Snapshot;
            if (AppServices.Social.IsAvailable && snap.InParty && snap.Party.members.Count > mode.MaxPartySize)
            {
                AppServices.Notifications.Post("Can't start match",
                    mode.DisplayName + " is a solo mode. Leave your party first.", NotificationSeverity.Warning);
                return;
            }

            if (!AppServices.Matchmaking.TryStart(mode, AppServices.Profile.Profile.playerId, out string reason))
                AppServices.Notifications.Post("Can't start match", reason, NotificationSeverity.Warning);
        }

        /// <summary>Back/Esc while the overlay is open: cancel a search or close an error. Returns true if handled.</summary>
        public bool HandleBack()
        {
            if (!IsOverlayVisible) return false;
            var status = AppServices.Matchmaking.Status;
            if (status.IsActive) OnCancel();
            else if (status.State == MatchmakingState.Failed) OnClose();
            return true;
        }

        private void OnCancel() => AppServices.Matchmaking.Cancel();

        private void OnRetry()
        {
            AppServices.Matchmaking.Reset();
            Play();
        }

        private void OnClose()
        {
            AppServices.Matchmaking.Reset();
            overlay.Hide();
        }

        private void OnStatus(MatchmakingStatus status)
        {
            switch (status.State)
            {
                case MatchmakingState.Searching:
                case MatchmakingState.Connecting:
                    overlay.Present(status);
                    break;

                case MatchmakingState.Succeeded:
                    overlay.Present(status);
                    if (!loadingScene && status.Assignment != null) StartCoroutine(LoadAssignedScene(status.Assignment));
                    break;

                case MatchmakingState.Failed:
                    loadingScene = false;
                    overlay.Present(status);
                    AppServices.Notifications.Post("Matchmaking failed", status.Message, NotificationSeverity.Error, false);
                    break;

                case MatchmakingState.Cancelled:
                    overlay.Hide();
                    AppServices.Notifications.Post("Matchmaking cancelled", "You left the queue.", NotificationSeverity.Info);
                    break;

                default:
                    overlay.Hide();
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
            while (op != null && !op.isDone)
            {
                overlay.SetLoadingProgress(op.progress / 0.9f);
                yield return null;
            }
        }
    }
}
