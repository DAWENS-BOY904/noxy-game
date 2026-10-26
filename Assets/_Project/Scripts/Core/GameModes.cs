using System;
using System.Collections.Generic;

namespace Noxy.Core
{
    public enum ModeAvailability
    {
        Available,
        ComingSoon
    }

    public static class GameModeIds
    {
        public const string BattleRoyale = "battle_royale";
        public const string LoneWolf = "lone_wolf";
        public const string Training = "training";
    }

    /// <summary>
    /// Describes a playable (or planned) game mode. A backend can later supply these instead of DefaultGameModes.
    /// </summary>
    public sealed class GameModeDefinition
    {
        public string Id;
        public string DisplayName;
        public string Tagline;
        public int MaxPlayers;

        /// <summary>Largest party that may queue together. 1 means the mode is solo-only.</summary>
        public int MaxPartySize = 1;

        /// <summary>True when the mode needs a dedicated server + matchmaking backend.</summary>
        public bool RequiresDedicatedServer;

        /// <summary>Scene loaded for offline/local modes. Null for server-driven modes.</summary>
        public string OfflineSceneName;

        public ModeAvailability Availability;

        /// <summary>Short player-facing explanation of the current status.</summary>
        public string StatusNote;

        public bool IsPlayable => Availability == ModeAvailability.Available;
    }

    public static class DefaultGameModes
    {
        public const string TrainingSceneName = "Training";

        public static List<GameModeDefinition> Create()
        {
            return new List<GameModeDefinition>
            {
                new GameModeDefinition
                {
                    Id = GameModeIds.BattleRoyale,
                    DisplayName = "Battle Royale",
                    Tagline = "Last squad standing",
                    MaxPlayers = 50,
                    MaxPartySize = 4,
                    RequiresDedicatedServer = true,
                    Availability = ModeAvailability.ComingSoon,
                    StatusNote = "Coming Soon - needs the dedicated game server, which is not built yet."
                },
                new GameModeDefinition
                {
                    Id = GameModeIds.LoneWolf,
                    DisplayName = "Lone Wolf",
                    Tagline = "Fast solo duels",
                    MaxPlayers = 2,
                    RequiresDedicatedServer = true,
                    Availability = ModeAvailability.ComingSoon,
                    StatusNote = "Coming Soon - needs the dedicated game server, which is not built yet."
                },
                new GameModeDefinition
                {
                    Id = GameModeIds.Training,
                    DisplayName = "Training",
                    Tagline = "Offline practice range",
                    MaxPlayers = 1,
                    RequiresDedicatedServer = false,
                    OfflineSceneName = TrainingSceneName,
                    Availability = ModeAvailability.Available,
                    StatusNote = "Offline local practice range - no human opponents or online progression."
                }
            };
        }
    }

    /// <summary>Holds the currently selected mode. Pure state, no UI.</summary>
    public sealed class GameModeSelection
    {
        private readonly List<GameModeDefinition> modes;

        public GameModeSelection(IEnumerable<GameModeDefinition> modes, string initialModeId)
        {
            this.modes = new List<GameModeDefinition>(modes);
            if (this.modes.Count == 0)
                throw new ArgumentException("At least one game mode is required.", nameof(modes));

            Selected = Find(initialModeId) ?? this.modes[0];
        }

        public IReadOnlyList<GameModeDefinition> Modes => modes;
        public GameModeDefinition Selected { get; private set; }

        public event Action<GameModeDefinition> SelectionChanged;

        public GameModeDefinition Find(string id)
        {
            for (int i = 0; i < modes.Count; i++)
                if (modes[i].Id == id) return modes[i];
            return null;
        }

        public bool Select(string id)
        {
            var mode = Find(id);
            if (mode == null || mode == Selected) return false;
            Selected = mode;
            SelectionChanged?.Invoke(mode);
            return true;
        }
    }
}
