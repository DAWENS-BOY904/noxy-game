using System.IO;
using Noxy.Core;
using UnityEngine;

namespace Noxy.Lobby
{
    /// <summary>
    /// Composition root for client services. Swap the local mock implementations here for
    /// backend-backed ones (IProfileService, IMatchmakingService) when they exist.
    /// Static so state survives lobby <-> game scene changes.
    /// </summary>
    public static class AppServices
    {
        public static ICosmeticCatalog Catalog { get; private set; }
        public static IProfileService Profile { get; private set; }
        public static GameModeSelection Modes { get; private set; }
        public static MatchmakingController Matchmaking { get; private set; }
        public static OfflineMatchmakingService OfflineMatchmaking { get; private set; }
        public static NotificationService Notifications { get; private set; }

        public static bool IsInitialized => Profile != null;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetStatics()
        {
            Catalog = null;
            Profile = null;
            Modes = null;
            Matchmaking = null;
            OfflineMatchmaking = null;
            Notifications = null;
        }

        public static void EnsureInitialized()
        {
            if (IsInitialized) return;

            Catalog = new DefaultCosmeticCatalog();
            Profile = new LocalProfileService(Catalog, Path.Combine(Application.persistentDataPath, "local_profile.json"));
            Notifications = new NotificationService();

            var modes = DefaultGameModes.Create();
            foreach (var mode in modes)
            {
                // A mode that needs a scene must not claim to work if that scene isn't in the build.
                if (!string.IsNullOrEmpty(mode.OfflineSceneName) && !Application.CanStreamedLevelBeLoaded(mode.OfflineSceneName))
                {
                    mode.Availability = ModeAvailability.ComingSoon;
                    mode.StatusNote = $"Coming Soon - scene '{mode.OfflineSceneName}' is missing from the build.";
                }
            }

            Modes = new GameModeSelection(modes, Profile.Profile.selectedModeId);
            Modes.SelectionChanged += mode => Profile.SetSelectedMode(mode.Id);

            OfflineMatchmaking = new OfflineMatchmakingService();
            Matchmaking = new MatchmakingController(new IMatchmakingService[]
            {
                OfflineMatchmaking,
                new UnavailableMatchmakingService()
            });

            LobbySettings.Apply();
            OfflineMatchmaking.SimulateFailure = LobbySettings.SimulateMatchmakingError;

            Notifications.Post("Welcome to Noxy", "Local demo profile loaded. Online services are not connected.", NotificationSeverity.Info, false);
            if (Profile.CanClaimDailyReward)
                Notifications.Post("Daily reward ready", "Open Rewards to claim it.", NotificationSeverity.Success, false);
        }
    }
}
