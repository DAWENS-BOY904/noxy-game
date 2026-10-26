using System;

namespace Noxy.Core
{
    /// <summary>
    /// Handles modes that run locally (no server). It only simulates a short "preparing" delay so the
    /// lobby flow, progress UI and cancellation behave like the real thing; it does NOT do networking.
    /// </summary>
    public sealed class OfflineMatchmakingService : IMatchmakingService
    {
        private const float SearchSeconds = 1.2f;
        private const float ConnectSeconds = 0.9f;

        private MatchmakingState state = MatchmakingState.Idle;
        private GameModeDefinition mode;
        private float timer;

        /// <summary>Debug switch so the failure UI can be exercised.</summary>
        public bool SimulateFailure;

        public event Action<MatchmakingStatus> StatusChanged;

        public bool CanHandle(GameModeDefinition m)
        {
            return m != null && m.IsPlayable && !m.RequiresDedicatedServer && !string.IsNullOrEmpty(m.OfflineSceneName);
        }

        public void Begin(GameModeDefinition m, MatchmakingRequest request)
        {
            mode = m;
            timer = 0f;
            state = MatchmakingState.Searching;
            Emit(state, 0f, "Preparing " + m.DisplayName + "...");
        }

        public void Cancel()
        {
            if (state != MatchmakingState.Searching && state != MatchmakingState.Connecting) return;
            state = MatchmakingState.Cancelled;
            Emit(state, 0f, "Cancelled.");
        }

        public void Tick(float dt)
        {
            if (state == MatchmakingState.Searching)
            {
                timer += dt;
                if (SimulateFailure && timer >= 0.8f)
                {
                    state = MatchmakingState.Failed;
                    Emit(state, 0f, "Simulated matchmaking failure (debug option in Settings).", MatchmakingError.Simulated);
                    return;
                }
                if (timer >= SearchSeconds)
                {
                    state = MatchmakingState.Connecting;
                    timer = 0f;
                    Emit(state, 0.5f, "Loading " + mode.DisplayName + "...");
                    return;
                }
                Emit(state, 0.5f * (timer / SearchSeconds), "Preparing " + mode.DisplayName + "...");
            }
            else if (state == MatchmakingState.Connecting)
            {
                timer += dt;
                if (timer >= ConnectSeconds)
                {
                    state = MatchmakingState.Succeeded;
                    StatusChanged?.Invoke(new MatchmakingStatus
                    {
                        State = state, Progress = 1f, Mode = mode, Message = "Match ready.",
                        Assignment = new MatchAssignment { ModeId = mode.Id, SceneName = mode.OfflineSceneName }
                    });
                    return;
                }
                Emit(state, 0.5f + 0.5f * (timer / ConnectSeconds), "Loading " + mode.DisplayName + "...");
            }
        }

        private void Emit(MatchmakingState s, float progress, string message, MatchmakingError error = MatchmakingError.None)
        {
            StatusChanged?.Invoke(new MatchmakingStatus
            {
                State = s, Progress = progress, Message = message, Error = error, Mode = mode
            });
        }
    }

    /// <summary>
    /// Catch-all registered last. Any mode that needs a dedicated server fails with an honest error
    /// until a real DedicatedServer matchmaking service exists.
    /// </summary>
    public sealed class UnavailableMatchmakingService : IMatchmakingService
    {
        public event Action<MatchmakingStatus> StatusChanged;

        public bool CanHandle(GameModeDefinition mode) => true;

        public void Begin(GameModeDefinition mode, MatchmakingRequest request)
        {
            StatusChanged?.Invoke(new MatchmakingStatus
            {
                State = MatchmakingState.Failed,
                Error = MatchmakingError.ServerUnavailable,
                Mode = mode,
                Message = "Online matchmaking is not connected yet. The dedicated server is not available."
            });
        }

        public void Cancel() { }
        public void Tick(float deltaSeconds) { }
    }
}
