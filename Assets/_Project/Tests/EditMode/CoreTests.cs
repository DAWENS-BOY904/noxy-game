using System;
using System.Collections.Generic;
using NUnit.Framework;
using Noxy.Core;

namespace Noxy.Tests
{
    public class GameModeSelectionTests
    {
        [Test]
        public void Select_ChangesStateAndRaisesEvent()
        {
            var selection = new GameModeSelection(DefaultGameModes.Create(), GameModeIds.Training);
            GameModeDefinition raised = null;
            selection.SelectionChanged += m => raised = m;

            Assert.IsTrue(selection.Select(GameModeIds.LoneWolf));

            Assert.AreEqual(GameModeIds.LoneWolf, selection.Selected.Id);
            Assert.AreEqual(GameModeIds.LoneWolf, raised.Id);
        }

        [Test]
        public void Select_UnknownOrSameMode_ReturnsFalse()
        {
            var selection = new GameModeSelection(DefaultGameModes.Create(), GameModeIds.Training);
            Assert.IsFalse(selection.Select("nope"));
            Assert.IsFalse(selection.Select(GameModeIds.Training));
        }

        [Test]
        public void ServerModes_AreComingSoon()
        {
            var selection = new GameModeSelection(DefaultGameModes.Create(), GameModeIds.Training);
            Assert.IsFalse(selection.Find(GameModeIds.BattleRoyale).IsPlayable);
            Assert.IsFalse(selection.Find(GameModeIds.LoneWolf).IsPlayable);
            Assert.IsTrue(selection.Find(GameModeIds.Training).IsPlayable);
        }
    }

    public class LocalProfileServiceTests
    {
        private static LocalProfileService Create(Func<DateTime> clock = null)
        {
            return new LocalProfileService(new DefaultCosmeticCatalog(), null, clock);
        }

        [Test]
        public void EquipLoadout_RejectsUnownedItem()
        {
            var profile = Create();
            var loadout = profile.Profile.loadout.Clone();
            loadout.outfit = "outfit_aurora";

            var result = profile.EquipLoadout(loadout);

            Assert.IsFalse(result.Success);
            Assert.AreNotEqual("outfit_aurora", profile.Profile.loadout.outfit);
        }

        [Test]
        public void EquipLoadout_AcceptsOwnedItemAndNotifies()
        {
            var profile = Create();
            int changes = 0;
            profile.ProfileChanged += () => changes++;
            var loadout = profile.Profile.loadout.Clone();
            loadout.outfit = "outfit_nightops";
            loadout.mask = "mask_visor";

            Assert.IsTrue(profile.EquipLoadout(loadout).Success);
            Assert.AreEqual("outfit_nightops", profile.Profile.loadout.outfit);
            Assert.AreEqual(1, changes);
        }

        [Test]
        public void EquipLoadout_RejectsItemInWrongSlot()
        {
            var profile = Create();
            var loadout = profile.Profile.loadout.Clone();
            loadout.shoes = "outfit_urban";
            Assert.IsFalse(profile.EquipLoadout(loadout).Success);
        }

        [Test]
        public void EquipLoadout_RejectsEmptyRequiredSlot()
        {
            var profile = Create();
            var loadout = profile.Profile.loadout.Clone();
            loadout.outfit = "";
            Assert.IsFalse(profile.EquipLoadout(loadout).Success);
        }

        [Test]
        public void DailyReward_CanOnlyBeClaimedOncePerDay()
        {
            var now = new DateTime(2026, 1, 1, 10, 0, 0, DateTimeKind.Utc);
            var profile = Create(() => now);
            int before = profile.Profile.credits;

            Assert.IsTrue(profile.ClaimDailyReward().Success);
            Assert.AreEqual(before + RewardRules.DailyRewardCredits, profile.Profile.credits);
            Assert.IsFalse(profile.ClaimDailyReward().Success);

            now = now.AddDays(1);
            Assert.IsTrue(profile.CanClaimDailyReward);
        }

        [Test]
        public void XpRequirement_GrowsWithLevel()
        {
            Assert.Greater(PlayerProfile.XpRequiredForLevel(10), PlayerProfile.XpRequiredForLevel(1));
        }
    }

    public class MatchmakingControllerTests
    {
        private static MatchmakingController Create(out OfflineMatchmakingService offline)
        {
            offline = new OfflineMatchmakingService();
            return new MatchmakingController(new IMatchmakingService[] { offline, new UnavailableMatchmakingService() });
        }

        private static GameModeDefinition Mode(string id)
        {
            return new GameModeSelection(DefaultGameModes.Create(), id).Selected;
        }

        [Test]
        public void ComingSoonMode_CannotStart()
        {
            var controller = Create(out _);
            Assert.IsFalse(controller.TryStart(Mode(GameModeIds.BattleRoyale), "p1", out var reason));
            StringAssert.Contains("coming soon", reason.ToLowerInvariant());
            Assert.AreEqual(MatchmakingState.Idle, controller.Status.State);
        }

        [Test]
        public void OfflineMode_RunsToSuccessWithSceneAssignment()
        {
            var controller = Create(out _);
            Assert.IsTrue(controller.TryStart(Mode(GameModeIds.Training), "p1", out _));
            Assert.AreEqual(MatchmakingState.Searching, controller.Status.State);

            for (int i = 0; i < 100 && controller.IsBusy; i++) controller.Tick(0.1f);

            Assert.AreEqual(MatchmakingState.Succeeded, controller.Status.State);
            Assert.AreEqual(DefaultGameModes.TrainingSceneName, controller.Status.Assignment.SceneName);
        }

        [Test]
        public void Cancel_StopsSearch()
        {
            var controller = Create(out _);
            controller.TryStart(Mode(GameModeIds.Training), "p1", out _);
            controller.Tick(0.2f);
            controller.Cancel();
            Assert.AreEqual(MatchmakingState.Cancelled, controller.Status.State);
            Assert.IsFalse(controller.IsBusy);
        }

        [Test]
        public void SimulatedFailure_ReportsErrorAndAllowsRetry()
        {
            var controller = Create(out var offline);
            offline.SimulateFailure = true;
            controller.TryStart(Mode(GameModeIds.Training), "p1", out _);
            for (int i = 0; i < 50 && controller.IsBusy; i++) controller.Tick(0.1f);

            Assert.AreEqual(MatchmakingState.Failed, controller.Status.State);
            Assert.AreEqual(MatchmakingError.Simulated, controller.Status.Error);

            offline.SimulateFailure = false;
            Assert.IsTrue(controller.TryStart(Mode(GameModeIds.Training), "p1", out _));
        }

        [Test]
        public void ServerRequiredMode_MarkedPlayable_FailsHonestly()
        {
            var controller = Create(out _);
            var mode = Mode(GameModeIds.BattleRoyale);
            mode.Availability = ModeAvailability.Available;

            Assert.IsTrue(controller.TryStart(mode, "p1", out _));
            Assert.AreEqual(MatchmakingState.Failed, controller.Status.State);
            Assert.AreEqual(MatchmakingError.ServerUnavailable, controller.Status.Error);
        }

        [Test]
        public void Timeout_FailsWithTimeoutError()
        {
            var controller = Create(out _);
            controller.SearchTimeoutSeconds = 0.05f;
            controller.TryStart(Mode(GameModeIds.Training), "p1", out _);
            controller.Tick(0.1f);
            Assert.AreEqual(MatchmakingError.Timeout, controller.Status.Error);
        }
    }
}
