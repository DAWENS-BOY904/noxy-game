using System;
using System.IO;
using NUnit.Framework;
using Noxy.Core;
using UnityEngine;

namespace Noxy.Tests
{
    public class SettingsTests
    {
        [Test]
        public void Sanitize_ClampsExtremeValues()
        {
            var s = new GameSettings
            {
                uiScale = 9f, masterVolume = 4f, sensGeneral = 9999, sensAds = -5, sensHorizontal = 0, sensVertical = 9999,
                radarSize = 0f, radarCorner = 99, fpsLimit = 5000, graphicsPreset = 42, textureQuality = 9
            };
            s.Sanitize(false);
            Assert.AreEqual(1.3f, s.uiScale, 1e-4f);
            Assert.AreEqual(1f, s.masterVolume);
            Assert.AreEqual(SensitivityModel.MaxSlider, s.sensGeneral);
            Assert.AreEqual(SensitivityModel.MinSlider, s.sensAds);
            Assert.AreEqual(20, s.sensHorizontal);
            Assert.AreEqual(200, s.sensVertical);
            Assert.AreEqual(0.7f, s.radarSize, 1e-4f);
            Assert.AreEqual(3, s.radarCorner);
            Assert.AreEqual(1000, s.fpsLimit);
            Assert.AreEqual((int)GraphicsPreset.Ultra, s.graphicsPreset);
            Assert.AreEqual(2, s.textureQuality);
        }

        [Test]
        public void Sanitize_MobileCannotUseUltraOrUnlimitedFps()
        {
            var s = new GameSettings { graphicsPreset = (int)GraphicsPreset.Ultra, fpsLimit = 0 };
            s.Sanitize(true);
            Assert.AreEqual((int)GraphicsPreset.High, s.graphicsPreset);
            Assert.AreEqual(60, s.fpsLimit);
        }

        [Test]
        public void Privacy_OnlyAllowsSupportedValues()
        {
            var s = new GameSettings { friendRequests = (int)Visibility.Friends, partyInvites = (int)Visibility.Everyone };
            s.Sanitize();
            Assert.AreEqual((int)Visibility.Everyone, s.friendRequests);
            Assert.AreEqual((int)Visibility.Friends, s.partyInvites);
        }

        [Test]
        public void ConfigStore_PersistsAcrossInstances()
        {
            string dir = Path.Combine(Path.GetTempPath(), "noxy-cfg-" + Guid.NewGuid().ToString("N"));
            try
            {
                var a = new LocalConfigStore(dir, false);
                a.Settings.sensGeneral = 77;
                a.Settings.radarNorthUp = true;
                a.SaveSettings();
                a.Bindings.Set(GameAction.Reload, KeyCode.Q);
                a.SaveBindings();

                var b = new LocalConfigStore(dir, false);
                Assert.AreEqual(77, b.Settings.sensGeneral);
                Assert.IsTrue(b.Settings.radarNorthUp);
                Assert.AreEqual(KeyCode.Q, b.Bindings.Get(GameAction.Reload));
            }
            finally { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
        }

        [Test]
        public void ConfigStore_ResetRestoresDefaults()
        {
            var store = new LocalConfigStore(null, false);
            store.Settings.sensGeneral = 150;
            store.ResetSettings();
            Assert.AreEqual(new GameSettings().sensGeneral, store.Settings.sensGeneral);
        }

        [Test]
        public void ConfigStore_CorruptFileFallsBackToDefaults()
        {
            string dir = Path.Combine(Path.GetTempPath(), "noxy-cfg-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            try
            {
                File.WriteAllText(Path.Combine(dir, "settings.json"), "{ not json");
                var store = new LocalConfigStore(dir, false);
                Assert.AreEqual(new GameSettings().sensGeneral, store.Settings.sensGeneral);
            }
            finally { Directory.Delete(dir, true); }
        }

        [Test]
        public void SyncMap_ContainsNoPrivacyOrSecrets()
        {
            var map = new GameSettings().ToSyncMap();
            Assert.IsFalse(map.ContainsKey("friendRequests"));
            foreach (var key in map.Keys) StringAssert.DoesNotContain("token", key.ToLowerInvariant());
            foreach (var v in map.Values) Assert.IsTrue(v is string || v is bool || v is int || v is float, v?.GetType().Name);
        }
    }

    public class FramePolicyTests
    {
        [Test]
        public void Pc_ListsRequestedCaps()
        {
            CollectionAssert.AreEqual(new[] { 60, 120, 144, 240, 300, 0 }, FramePolicy.Options(false, 144));
        }

        [Test]
        public void Mobile_OffersHigherCapsOnlyWhenDisplaySupportsThem()
        {
            CollectionAssert.AreEqual(new[] { 30, 45, 60 }, FramePolicy.Options(true, 60));
            CollectionAssert.AreEqual(new[] { 30, 45, 60, 90, 120 }, FramePolicy.Options(true, 120));
        }

        [Test]
        public void Pc_CapAppliesWhenVSyncOff()
        {
            var r = FramePolicy.Resolve(300, false, false);
            Assert.AreEqual(300, r.TargetFrameRate);
            Assert.AreEqual(0, r.VSyncCount);
            Assert.IsFalse(r.CapIgnoredByVSync);
        }

        [Test]
        public void Pc_VSyncOverridesCapAndSaysSo()
        {
            var r = FramePolicy.Resolve(144, true, false);
            Assert.AreEqual(1, r.VSyncCount);
            Assert.AreEqual(-1, r.TargetFrameRate);
            Assert.IsTrue(r.CapIgnoredByVSync);
        }

        [Test]
        public void Pc_UnlimitedMapsToPlatformDefault()
        {
            var r = FramePolicy.Resolve(0, false, false);
            Assert.AreEqual(-1, r.TargetFrameRate);
            Assert.AreEqual(0, r.VSyncCount);
        }

        [Test]
        public void Mobile_NeverUsesVSyncAndCapsToValidRange()
        {
            var r = FramePolicy.Resolve(0, true, true);
            Assert.AreEqual(0, r.VSyncCount);
            Assert.AreEqual(60, r.TargetFrameRate);
        }

        [Test]
        public void Recommend_LowEndPhonesGetLowPreset()
        {
            Assert.AreEqual(GraphicsPreset.Low, GraphicsPresets.Recommend(true, 3000, 8));
            Assert.AreEqual(GraphicsPreset.Medium, GraphicsPresets.Recommend(true, 4096, 8));
            Assert.AreEqual(GraphicsPreset.High, GraphicsPresets.Recommend(true, 8192, 8));
            Assert.IsFalse(GraphicsPresets.Get(GraphicsPreset.Low).ShadowsEnabled);
            Assert.IsTrue(GraphicsPresets.Get(GraphicsPreset.High).ShadowsEnabled);
        }
    }

    public class SensitivityTests
    {
        [Test]
        public void HigherSliderRotatesMore()
        {
            var low = new GameSettings { sensGeneral = 25 };
            var high = new GameSettings { sensGeneral = 100 };
            var a = SensitivityModel.LookDelta(new Vector2(10, 0), LookSource.Mouse, false, low);
            var b = SensitivityModel.LookDelta(new Vector2(10, 0), LookSource.Mouse, false, high);
            Assert.AreEqual(4f, b.x / a.x, 1e-3f);
        }

        [Test]
        public void AimingUsesAdsSlider()
        {
            var s = new GameSettings { sensGeneral = 100, sensAds = 20 };
            var hip = SensitivityModel.LookDelta(new Vector2(10, 0), LookSource.Mouse, false, s);
            var ads = SensitivityModel.LookDelta(new Vector2(10, 0), LookSource.Mouse, true, s);
            Assert.Less(ads.x, hip.x);
            Assert.AreEqual(5f, hip.x / ads.x, 1e-3f);
        }

        [Test]
        public void HorizontalAndVerticalAreIndependent()
        {
            var s = new GameSettings { sensHorizontal = 200, sensVertical = 50 };
            var d = SensitivityModel.LookDelta(new Vector2(10, 10), LookSource.Mouse, false, s);
            Assert.AreEqual(4f, d.x / Mathf.Abs(d.y), 1e-3f);
        }

        [Test]
        public void InvertYFlipsPitchOnly()
        {
            var n = SensitivityModel.LookDelta(new Vector2(5, 5), LookSource.Mouse, false, new GameSettings());
            var i = SensitivityModel.LookDelta(new Vector2(5, 5), LookSource.Mouse, false, new GameSettings { invertY = true });
            Assert.AreEqual(n.x, i.x, 1e-5f);
            Assert.AreEqual(-n.y, i.y, 1e-5f);
        }

        [Test]
        public void TouchUsesTouchSlider()
        {
            var s = new GameSettings { sensTouch = 100, sensGeneral = 1 };
            var touch = SensitivityModel.LookDelta(new Vector2(10, 0), LookSource.Touch, false, s);
            var mouse = SensitivityModel.LookDelta(new Vector2(10, 0), LookSource.Mouse, false, s);
            Assert.Greater(touch.x, mouse.x);
        }

        [Test]
        public void ExtremeInputsStayFinite()
        {
            var s = new GameSettings { sensGeneral = int.MaxValue, sensHorizontal = int.MaxValue };
            s.Sanitize();
            var d = SensitivityModel.LookDelta(new Vector2(100, 100), LookSource.Mouse, false, s);
            Assert.IsFalse(float.IsNaN(d.x) || float.IsInfinity(d.x));
            Assert.Less(Mathf.Abs(d.x), 100f * 0.06f * 4f * 2f + 1f);
        }
    }

    public class KeyBindingsTests
    {
        [Test]
        public void Defaults_AreConflictFreeAndMatchSpec()
        {
            var b = KeyBindings.CreateDefault();
            Assert.AreEqual(0, b.FindAllConflicts().Count);
            Assert.AreEqual(KeyCode.Alpha1, b.Get(GameAction.Slot1));
            Assert.AreEqual(KeyCode.Alpha2, b.Get(GameAction.Slot2));
            Assert.AreEqual(KeyCode.Alpha3, b.Get(GameAction.Slot3));
        }

        [Test]
        public void ConflictsAreDetectedAndResolvable()
        {
            var b = KeyBindings.CreateDefault();
            var conflicts = b.ConflictsFor(GameAction.Reload, KeyCode.E);
            CollectionAssert.AreEqual(new[] { GameAction.Interact }, conflicts);
            b.Set(GameAction.Reload, KeyCode.E);
            Assert.AreEqual(1, b.FindAllConflicts().Count);
            b.Set(GameAction.Interact, KeyCode.F);
            Assert.AreEqual(0, b.FindAllConflicts().Count);
        }

        [Test]
        public void RestoreDefaults_UndoesChanges()
        {
            var b = KeyBindings.CreateDefault();
            b.Set(GameAction.Jump, KeyCode.Q);
            b.RestoreDefaults();
            Assert.AreEqual(KeyCode.Space, b.Get(GameAction.Jump));
        }

        [Test]
        public void Sanitize_FillsMissingAndDropsUnknown()
        {
            var b = new KeyBindings();
            b.entries.Add(new KeyBindingEntry { action = "Bogus", key = "W" });
            b.entries.Add(new KeyBindingEntry { action = "Jump", key = "NotAKey" });
            b.Sanitize();
            Assert.AreEqual(Enum.GetValues(typeof(GameAction)).Length, b.entries.Count);
            Assert.AreEqual(KeyCode.Space, b.Get(GameAction.Jump));
        }

        [Test]
        public void SurvivesJsonRoundTrip()
        {
            var b = KeyBindings.CreateDefault();
            b.Set(GameAction.Crouch, KeyCode.C);
            var copy = b.Clone();
            Assert.AreEqual(KeyCode.C, copy.Get(GameAction.Crouch));
        }
    }

    public class HudLayoutTests
    {
        private static readonly Vector2 Landscape = new Vector2(1600f, 900f);

        [Test]
        public void Defaults_CoverEveryElementInBothOrientations()
        {
            foreach (var portrait in new[] { false, true })
            {
                var l = HudLayout.CreateDefault(portrait);
                foreach (HudElementId id in Enum.GetValues(typeof(HudElementId))) Assert.IsNotNull(l.Get(id), id.ToString());
            }
        }

        [Test]
        public void Sanitize_KeepsControlsInsideSafeArea()
        {
            var l = HudLayout.CreateDefault(false);
            l.Get(HudElementId.Fire).x = 5f;
            l.Get(HudElementId.Fire).y = -3f;
            l.Get(HudElementId.Jump).scale = 50f;
            l.Sanitize(Landscape);

            var fire = l.Get(HudElementId.Fire);
            float half = HudElementInfo.BaseSize(HudElementId.Fire).x * fire.scale * 0.5f / Landscape.x;
            Assert.LessOrEqual(fire.x + half, 1.0001f);
            Assert.GreaterOrEqual(fire.y, 0f);
            Assert.AreEqual(HudElementInfo.MaxScale, l.Get(HudElementId.Jump).scale);
        }

        [Test]
        public void Sanitize_FillsMissingElementsAndRejectsNaN()
        {
            var l = new HudLayout { key = "landscape" };
            l.elements.Add(new HudElementState { id = "Fire", x = float.NaN, y = 0.5f, scale = float.NaN, opacity = 5f });
            l.Sanitize(Landscape);
            Assert.AreEqual(Enum.GetValues(typeof(HudElementId)).Length, l.elements.Count);
            Assert.IsFalse(float.IsNaN(l.Get(HudElementId.Fire).x));
            Assert.AreEqual(1f, l.Get(HudElementId.Fire).opacity);
        }

        [Test]
        public void ApiIds_MatchBackendNames()
        {
            Assert.AreEqual("move_stick", HudElementInfo.ApiId(HudElementId.MoveStick));
            Assert.AreEqual("slot1", HudElementInfo.ApiId(HudElementId.Slot1));
            Assert.AreEqual("minimap", HudElementInfo.ApiId(HudElementId.Minimap));
        }

        [Test]
        public void StoreSavesAndRestoresLayouts()
        {
            string dir = Path.Combine(Path.GetTempPath(), "noxy-hud-" + Guid.NewGuid().ToString("N"));
            try
            {
                var store = new LocalConfigStore(dir, true);
                var layout = store.LoadHud(false, Landscape);
                layout.Get(HudElementId.Fire).x = 0.7f;
                layout.Get(HudElementId.Fire).scale = 1.4f;
                store.SaveHud(layout);

                var reloaded = new LocalConfigStore(dir, true).LoadHud(false, Landscape);
                Assert.AreEqual(1.4f, reloaded.Get(HudElementId.Fire).scale, 1e-4f);

                store.ResetHud(false);
                var restored = store.LoadHud(false, Landscape);
                Assert.AreEqual(HudLayout.CreateDefault(false).Get(HudElementId.Fire).scale, restored.Get(HudElementId.Fire).scale, 1e-4f);
            }
            finally { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
        }
    }

    public class WeaponInventoryTests
    {
        private static WeaponInventory Full()
        {
            var w = new WeaponInventory();
            w.Equip(DefaultWeapons.Rifle, WeaponSlot.Primary);
            w.Equip(DefaultWeapons.Pistol, WeaponSlot.Secondary);
            w.Equip(DefaultWeapons.Knife, WeaponSlot.Melee);
            return w;
        }

        private static void Wait(WeaponInventory w, float seconds)
        {
            for (float t = 0; t < seconds; t += 0.05f) w.Tick(0.05f);
        }

        [Test]
        public void SwitchesBetweenAllThreeSlots()
        {
            var w = Full();
            Assert.AreEqual(SwitchResult.Switched, w.TrySwitch(WeaponSlot.Secondary));
            Assert.AreEqual(WeaponSlot.Secondary, w.Selected);
            Wait(w, 0.5f);
            Assert.AreEqual(SwitchResult.Switched, w.TrySwitch(WeaponSlot.Melee));
            Wait(w, 0.5f);
            Assert.AreEqual(SwitchResult.Switched, w.TrySwitch(WeaponSlot.Primary));
        }

        [Test]
        public void CannotSwitchToEmptySlot()
        {
            var w = new WeaponInventory();
            w.Equip(DefaultWeapons.Rifle, WeaponSlot.Primary);
            Assert.AreEqual(SwitchResult.SlotEmpty, w.TrySwitch(WeaponSlot.Secondary));
            Assert.AreEqual(WeaponSlot.Primary, w.Selected);
        }

        [Test]
        public void SwitchingIsRateLimitedAndBlocksFiring()
        {
            var w = Full();
            w.TrySwitch(WeaponSlot.Secondary);
            Assert.AreEqual(SwitchResult.Busy, w.TrySwitch(WeaponSlot.Melee));
            Assert.AreEqual(FireResult.Switching, w.TryFire());
            Wait(w, 0.4f);
            Assert.AreEqual(FireResult.Fired, w.TryFire());
        }

        [Test]
        public void FireConsumesAmmoAndRespectsCooldown()
        {
            var w = Full();
            Assert.AreEqual(FireResult.Fired, w.TryFire());
            Assert.AreEqual(29, w.Magazine(WeaponSlot.Primary));
            Assert.AreEqual(FireResult.Cooldown, w.TryFire());
            Wait(w, 0.15f);
            Assert.AreEqual(FireResult.Fired, w.TryFire());
        }

        [Test]
        public void EmptyMagazineCannotFire_ReloadRefillsFromReserve()
        {
            var w = Full();
            for (int i = 0; i < 30; i++) { w.TryFire(); Wait(w, 0.11f); }
            Assert.AreEqual(0, w.Magazine(WeaponSlot.Primary));
            Assert.AreEqual(FireResult.Empty, w.TryFire());
            Assert.AreEqual(ReloadResult.Started, w.TryReload());
            Assert.AreEqual(FireResult.Reloading, w.TryFire());
            Wait(w, 2.1f);
            Assert.AreEqual(30, w.Magazine(WeaponSlot.Primary));
            Assert.AreEqual(90, w.Reserve(WeaponSlot.Primary));
        }

        [Test]
        public void ReloadRules()
        {
            var w = Full();
            Assert.AreEqual(ReloadResult.Full, w.TryReload());
            w.TryFire();
            Assert.AreEqual(ReloadResult.Started, w.TryReload());
            Assert.AreEqual(ReloadResult.Busy, w.TryReload());
            Wait(w, 0.5f);
            Assert.AreEqual(SwitchResult.Switched, w.TrySwitch(WeaponSlot.Secondary)); // switching cancels the reload
            Assert.IsFalse(w.IsReloading);
            Wait(w, 0.5f);
            w.TrySwitch(WeaponSlot.Melee);
            Wait(w, 0.5f);
            Assert.AreEqual(ReloadResult.NotReloadable, w.TryReload());
        }

        [Test]
        public void ReloadCannotStartWithoutReserve()
        {
            var w = new WeaponInventory();
            w.Equip(new WeaponDefinition { Id = "x", Slot = WeaponSlot.Primary, MagazineSize = 2, ReserveAmmo = 0, FireInterval = 0.01f, ReloadSeconds = 0.1f }, WeaponSlot.Primary);
            w.TryFire();
            Assert.AreEqual(ReloadResult.NoReserve, w.TryReload());
        }

        [Test]
        public void MeleeNeverRunsOutOfAmmo()
        {
            var w = Full();
            w.TrySwitch(WeaponSlot.Melee);
            Wait(w, 0.4f);
            for (int i = 0; i < 5; i++) { Assert.AreEqual(FireResult.Fired, w.TryFire()); Wait(w, 0.55f); }
        }
    }

    public class RadarMathTests
    {
        [Test]
        public void Radar_NeverShowsEnemies_InAnyMode()
        {
            foreach (var mode in new[] { GameModeIds.Training, GameModeIds.BattleRoyale, GameModeIds.LoneWolf, "unknown" })
                Assert.IsFalse(RadarPolicy.Allows(mode, RadarMarkerKind.Enemy), mode);
        }

        [Test]
        public void Radar_PerModeMarkerRules()
        {
            Assert.IsTrue(RadarPolicy.Allows(GameModeIds.Training, RadarMarkerKind.PracticeTarget));
            Assert.IsFalse(RadarPolicy.Allows(GameModeIds.BattleRoyale, RadarMarkerKind.PracticeTarget));
            Assert.IsTrue(RadarPolicy.Allows(GameModeIds.BattleRoyale, RadarMarkerKind.Teammate));
            Assert.IsTrue(RadarPolicy.Allows(GameModeIds.BattleRoyale, RadarMarkerKind.SafeZone));
            Assert.IsFalse(RadarPolicy.Allows(GameModeIds.LoneWolf, RadarMarkerKind.Teammate));
            Assert.Greater(RadarPolicy.Range(GameModeIds.Training, true), RadarPolicy.Range(GameModeIds.Training, false));
        }

        [Test]
        public void TargetAhead_IsUpWhenRotating()
        {
            var b = RadarMath.ToRadar(0f, 10f, 0f, 50f, false);
            Assert.AreEqual(0f, b.X, 1e-4f);
            Assert.AreEqual(0.2f, b.Y, 1e-4f);
        }

        [Test]
        public void RotatingMap_KeepsTargetAheadUpWhenTurning()
        {
            // Player faces east (+x, yaw 90); a target to the east must still draw "up".
            var b = RadarMath.ToRadar(10f, 0f, 90f, 50f, false);
            Assert.AreEqual(0f, b.X, 1e-4f);
            Assert.AreEqual(0.2f, b.Y, 1e-4f);
        }

        [Test]
        public void NorthUp_IgnoresPlayerYaw()
        {
            var b = RadarMath.ToRadar(10f, 0f, 90f, 50f, true);
            Assert.AreEqual(0.2f, b.X, 1e-4f);
            Assert.AreEqual(0f, b.Y, 1e-4f);
            Assert.AreEqual(90f, RadarMath.PlayerArrowAngle(90f, true));
            Assert.AreEqual(0f, RadarMath.PlayerArrowAngle(90f, false));
        }

        [Test]
        public void OutOfRange_ClampsToEdge()
        {
            var b = RadarMath.ToRadar(0f, 500f, 0f, 50f, false);
            Assert.IsFalse(b.InRange);
            Assert.AreEqual(1f, b.Y, 1e-4f);
        }

        [Test]
        public void TargetOnRight_IsRight()
        {
            var b = RadarMath.ToRadar(10f, 0f, 0f, 50f, false);
            Assert.Greater(b.X, 0f);
        }
    }
}
