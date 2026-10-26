using System;
using System.Collections.Generic;

namespace Noxy.Core
{
    public enum MatchmakingState
    {
        Idle,
        Searching,
        Connecting,
        Succeeded,
        Failed,
        Cancelled
    }

    public enum MatchmakingError
    {
        None,
        ModeUnavailable,
        ServerUnavailable,
        Timeout,
        SceneLoadFailed,
        Simulated,
        NotEnoughPlayers
    }

    public sealed class MatchmakingRequest
    {
        public string PlayerId;
        public string ModeId;
        public string PartyId;
    }

    /// <summary>
    /// What a successful match resolves to. Offline modes fill SceneName; a future dedicated-server
    /// service fills ServerAddress/ServerPort/SessionToken (short-lived, issued by the backend).
    /// </summary>
    public sealed class MatchAssignment
    {
        public string ModeId;
        public string SceneName;
        public string ServerAddress;
        public int ServerPort;
        public string SessionToken;
    }

    public sealed class MatchmakingStatus
    {
        public MatchmakingState State;
        public MatchmakingError Error;
        public string Message = "";
        public float Progress;
        public float ElapsedSeconds;
        public GameModeDefinition Mode;
        public MatchAssignment Assignment;

        public bool IsActive => State == MatchmakingState.Searching || State == MatchmakingState.Connecting;
    }

    /// <summary>
    /// One way of getting a player into a match. Implement this against the real backend later
    /// (e.g. DedicatedServerMatchmakingService) and register it with MatchmakingController.
    /// </summary>
    public interface IMatchmakingService
    {
        event Action<MatchmakingStatus> StatusChanged;
        bool CanHandle(GameModeDefinition mode);
        void Begin(GameModeDefinition mode, MatchmakingRequest request);
        void Cancel();
        void Tick(float deltaSeconds);
    }

    /// <summary>Single entry point for the UI. Picks the right service, tracks elapsed time and timeouts.</summary>
    public sealed class MatchmakingController
    {
        private readonly List<IMatchmakingService> services;
        private IMatchmakingService active;

        public MatchmakingController(IEnumerable<IMatchmakingService> services)
        {
            this.services = new List<IMatchmakingService>(services);
            foreach (var s in this.services)
            {
                var captured = s;
                s.StatusChanged += status => OnServiceStatus(captured, status);
            }
            Status = new MatchmakingStatus { State = MatchmakingState.Idle };
        }

        public float SearchTimeoutSeconds = 60f;

        public MatchmakingStatus Status { get; private set; }
        public bool IsBusy => Status.IsActive;

        public event Action<MatchmakingStatus> StatusChanged;

        public bool TryStart(GameModeDefinition mode, string playerId, out string reason)
        {
            reason = null;
            if (IsBusy)
            {
                reason = "Already searching for a match.";
                return false;
            }
            if (mode == null)
            {
                reason = "No game mode selected.";
                return false;
            }
            if (!mode.IsPlayable)
            {
                reason = mode.DisplayName + " is coming soon.";
                return false;
            }

            active = null;
            foreach (var s in services)
            {
                if (s.CanHandle(mode)) { active = s; break; }
            }
            if (active == null)
            {
                reason = "No matchmaking service available for " + mode.DisplayName + ".";
                return false;
            }

            Publish(new MatchmakingStatus { State = MatchmakingState.Searching, Mode = mode, Message = "Starting..." });
            active.Begin(mode, new MatchmakingRequest { PlayerId = playerId, ModeId = mode.Id });
            return true;
        }

        public void Cancel()
        {
            if (IsBusy && active != null) active.Cancel();
        }

        /// <summary>Report a failure that happens after matchmaking succeeded (e.g. scene load).</summary>
        public void Fail(MatchmakingError error, string message)
        {
            Publish(new MatchmakingStatus
            {
                State = MatchmakingState.Failed, Error = error, Message = message,
                Mode = Status.Mode, ElapsedSeconds = Status.ElapsedSeconds
            });
        }

        public void Reset()
        {
            if (IsBusy) return;
            active = null;
            Publish(new MatchmakingStatus { State = MatchmakingState.Idle });
        }

        public void Tick(float deltaSeconds)
        {
            if (!IsBusy || active == null) return;

            Status.ElapsedSeconds += deltaSeconds;
            active.Tick(deltaSeconds);

            if (IsBusy && Status.ElapsedSeconds > SearchTimeoutSeconds)
            {
                var svc = active;
                active = null; // ignore the service's own "cancelled" status; we report a timeout instead
                svc.Cancel();
                Fail(MatchmakingError.Timeout, "Matchmaking timed out. Please try again.");
            }
        }

        private void OnServiceStatus(IMatchmakingService source, MatchmakingStatus incoming)
        {
            if (source != active) return;
            incoming.ElapsedSeconds = Status.ElapsedSeconds;
            if (incoming.Mode == null) incoming.Mode = Status.Mode;
            Publish(incoming);
        }

        private void Publish(MatchmakingStatus status)
        {
            Status = status;
            StatusChanged?.Invoke(status);
        }
    }
}
