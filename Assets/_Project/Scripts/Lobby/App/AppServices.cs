using System.IO;
using Noxy.Core;
using UnityEngine;

namespace Noxy.Lobby
{
    /// <summary>
    /// Composition root for client services. Static so state survives lobby &lt;-&gt; game scene changes.
    /// Offline is the default: the local mock profile and local settings always work. Signing in (Settings &gt; Account)
    /// binds the same profile object to server data and switches Social to the remote implementation.
    /// </summary>
    public static class AppServices
    {
        public const string DefaultServerUrl = "http://127.0.0.1:8787";

        public static ICosmeticCatalog Catalog { get; private set; }
        public static IProfileService Profile { get; private set; }
        public static LocalConfigStore Config { get; private set; }
        public static GameModeSelection Modes { get; private set; }
        public static MatchmakingController Matchmaking { get; private set; }
        public static OfflineMatchmakingService OfflineMatchmaking { get; private set; }
        public static NotificationService Notifications { get; private set; }
        public static AccountService Account { get; private set; }
        public static ISocialService Social { get; private set; }

        public static bool IsInitialized => Profile != null;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetStatics()
        {
            Catalog = null; Profile = null; Config = null; Modes = null; Matchmaking = null;
            OfflineMatchmaking = null; Notifications = null; Account = null; Social = null;
        }

        public static void EnsureInitialized()
        {
            if (IsInitialized) return;

            string dir = Application.persistentDataPath;
            Catalog = new DefaultCosmeticCatalog();
            Profile = new LocalProfileService(Catalog, Path.Combine(dir, "local_profile.json"));
            Config = new LocalConfigStore(dir, Application.isMobilePlatform);
            Notifications = new NotificationService();

            DisplayApplier.ApplyFirstRunDefaults(Config);
            DisplayApplier.Apply(Config.Settings);
            Config.Changed += OnConfigChanged;

            var tokens = new PrefsTokenStore();
            string url = string.IsNullOrEmpty(tokens.LoadServerUrl()) ? DefaultServerUrl : tokens.LoadServerUrl();
            var api = new ApiClient(new UnityWebRequestTransport(), url);
            var remote = new RemoteSocialService(api);
            Social = remote;
            Account = new AccountService(api, Profile, Config, Notifications, remote, tokens);

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

            OfflineMatchmaking = new OfflineMatchmakingService { SimulateFailure = LobbySettings.SimulateMatchmakingError };
            Matchmaking = new MatchmakingController(new IMatchmakingService[]
            {
                OfflineMatchmaking,
                new UnavailableMatchmakingService()
            });

            AppRunner.Ensure();
            Notifications.Post("Welcome to Noxy", "Offline profile loaded. Sign in under Settings > Account for online features.", NotificationSeverity.Info, false);
            if (Profile.CanClaimDailyReward)
                Notifications.Post("Daily reward ready", "Open Rewards to claim it.", NotificationSeverity.Success, false);

            Account.TryRestoreSession();
        }

        private static void OnConfigChanged() => DisplayApplier.Apply(Config.Settings);
    }
}
