using System;
using NUnit.Framework;
using Noxy.Core;
using UnityEngine;

namespace Noxy.Tests
{
    public class LookPipelineTests
    {
        private static GameSettings Defaults() { var s = new GameSettings(); s.Sanitize(); return s; }

        [Test]
        public void WithSmoothingOffThePipelineEqualsTheSensitivityModelExactlyOnce()
        {
            var s = Defaults(); s.sensGeneral = 80; s.sensHorizontal = 130; s.sensVertical = 70; s.invertY = true;
            var p = new LookPipeline();
            var raw = new Vector2(12f, -5f);
            var direct = SensitivityModel.LookDelta(raw, LookSource.Mouse, false, s);
            var viaPipeline = p.Process(raw, new Vector2(0f, 0f), 0f, 0f, false, 0.016f, s);
            Assert.AreEqual(direct.x, viaPipeline.x, 1e-6f); Assert.AreEqual(direct.y, viaPipeline.y, 1e-6f);
            var t = SensitivityModel.LookDelta(raw, LookSource.Touch, true, s);
            var tp = p.Process(new Vector2(0f, 0f), raw, 0f, 0f, true, 0.016f, s);
            Assert.AreEqual(t.x, tp.x, 1e-6f); Assert.AreEqual(t.y, tp.y, 1e-6f);
        }

        [Test]
        public void AdsUsesTheAdsSliderAndInvertAndAxisMultipliersApplyOnce()
        {
            var s = Defaults(); s.sensGeneral = 100; s.sensAds = 50;
            var hip = new LookPipeline().Process(new Vector2(10f, 0f), new Vector2(0f, 0f), 0f, 0f, false, 0.016f, s);
            var ads = new LookPipeline().Process(new Vector2(10f, 0f), new Vector2(0f, 0f), 0f, 0f, true, 0.016f, s);
            Assert.AreEqual(2f, hip.x / ads.x, 1e-4f, "ADS slider is half of hip-fire");
            s.invertY = false; var a = new LookPipeline().Process(new Vector2(0f, 10f), new Vector2(0f, 0f), 0f, 0f, false, 0.016f, s);
            s.invertY = true; var b = new LookPipeline().Process(new Vector2(0f, 10f), new Vector2(0f, 0f), 0f, 0f, false, 0.016f, s);
            Assert.AreEqual(-a.y, b.y, 1e-6f);
        }

        [TestCase(30)]
        [TestCase(60)]
        [TestCase(144)]
        [TestCase(240)]
        public void SmoothingNeverLosesRotationAndFeelsTheSameAtAnyFrameRate(int fps)
        {
            var s = Defaults(); s.mouseSmoothing = 60; s.sensGeneral = 50;
            var p = new LookPipeline();
            float dt = 1f / fps, total = 0f, at100ms = 0f, t = 0f;
            // a 0.1 s swipe of 100 raw counts spread over the frames, then stillness for a second
            int swipeFrames = Math.Max(1, (int)Math.Round(0.1f * fps));
            for (int i = 0; i < fps * 1.1f; i++)
            {
                var raw = i < swipeFrames ? new Vector2(100f / swipeFrames, 0f) : new Vector2(0f, 0f);
                total += p.Process(raw, new Vector2(0f, 0f), 0f, 0f, false, dt, s).x;
                t += dt;
                if (Math.Abs(t - 0.2f) < dt / 2f) at100ms = total;
            }
            float expected = SensitivityModel.LookDelta(new Vector2(100f, 0f), LookSource.Mouse, false, s).x;
            Assert.AreEqual(expected, total, expected * 0.001f, "everything that went in came out");
            Assert.AreEqual(0.957f * expected, at100ms, expected * 0.015f, "the lag is the same at every frame rate (about 96% has come out 0.2 s in)");
        }

        [Test]
        public void TurningSmoothingOffReleasesWhatWasPending()
        {
            var s = Defaults(); s.mouseSmoothing = 100;
            var p = new LookPipeline();
            float got = p.Process(new Vector2(50f, 0f), new Vector2(0f, 0f), 0f, 0f, false, 0.016f, s).x;
            s.mouseSmoothing = 0;
            got += p.Process(new Vector2(0f, 0f), new Vector2(0f, 0f), 0f, 0f, false, 0.016f, s).x;
            Assert.AreEqual(SensitivityModel.LookDelta(new Vector2(50f, 0f), LookSource.Mouse, false, s).x, got, 1e-4f);
        }

        [Test]
        public void TouchAccelerationDependsOnSwipeSpeedNotOnFrameRate()
        {
            Assert.AreEqual(1f, LookPipeline.TouchGain(30f, 0f, 0.016f, 0), "off");
            float slow = LookPipeline.TouchGain(1f, 0f, 0.016f, 100), fast = LookPipeline.TouchGain(60f, 0f, 0.016f, 100);
            Assert.Less(slow, 1.1f); Assert.Greater(fast, 2.5f - 0.6f); Assert.LessOrEqual(fast, 2f + 1e-4f);
            // the same finger speed (1800 px/s) at 30 and 120 FPS gives the same gain
            Assert.AreEqual(LookPipeline.TouchGain(60f, 0f, 1f / 30f, 50), LookPipeline.TouchGain(15f, 0f, 1f / 120f, 50), 1e-4f);
            Assert.AreEqual(1f, LookPipeline.TouchGain(100f, 0f, 0f, 100), "no time step, no acceleration");
        }

        [Test]
        public void GyroIsOffByDefaultScalesWithItsSliderAndRespectsAds()
        {
            var s = Defaults();
            Assert.AreEqual(0f, LookPipeline.GyroDelta(1f, 1f, 0.016f, false, s).x, "disabled");
            s.gyroEnabled = true; s.sensGyro = 50;
            var one = LookPipeline.GyroDelta(0f, 1f, 0.1f, false, s);
            s.sensGyro = 100;
            var two = LookPipeline.GyroDelta(0f, 1f, 0.1f, false, s);
            Assert.AreEqual(2f, two.x / one.x, 1e-4f);
            s.sensGyro = 50; s.sensGeneral = 100; s.sensAds = 50;
            var ads = LookPipeline.GyroDelta(0f, 1f, 0.1f, true, s);
            Assert.AreEqual(0.5f, ads.x / one.x, 1e-4f);
            // 1 rad/s for 0.1 s at the default slider is 5.73 degrees
            s.sensGyro = 50; s.sensAds = 35; s.sensGeneral = 50; s.sensHorizontal = 100;
            Assert.AreEqual(5.7296f, Math.Abs(LookPipeline.GyroDelta(0f, 1f, 0.1f, false, s).x), 1e-3f);
        }

        [Test]
        public void NewSettingsAreRangeCheckedSyncedAndResetToSaneDefaults()
        {
            var d = Defaults();
            Assert.AreEqual(0, d.mouseSmoothing); Assert.AreEqual(0, d.touchSmoothing); Assert.AreEqual(0, d.touchAcceleration);
            Assert.IsFalse(d.gyroEnabled); Assert.AreEqual(50, d.sensGyro);

            var s = new GameSettings { mouseSmoothing = 999, touchSmoothing = -5, touchAcceleration = 150, sensGyro = 9999 };
            s.Sanitize();
            Assert.AreEqual(100, s.mouseSmoothing); Assert.AreEqual(0, s.touchSmoothing); Assert.AreEqual(100, s.touchAcceleration); Assert.AreEqual(SensitivityModel.MaxSlider, s.sensGyro);

            var src = Defaults(); src.mouseSmoothing = 35; src.touchSmoothing = 20; src.touchAcceleration = 45; src.gyroEnabled = true; src.sensGyro = 120;
            Assert.IsTrue(MiniJson.TryParse(MiniJson.Write(src.ToSyncMap()), out var parsed));   // the real path: JSON to the server and back
            var map = MiniJson.AsObject(parsed);
            foreach (var k in new[] { "mouseSmoothing", "touchSmoothing", "touchAcceleration", "gyroEnabled", "sensGyro" }) Assert.IsTrue(map.ContainsKey(k), k);
            var dst = Defaults(); dst.ApplySyncMap(map);
            Assert.AreEqual(35, dst.mouseSmoothing); Assert.AreEqual(20, dst.touchSmoothing); Assert.AreEqual(45, dst.touchAcceleration);
            Assert.IsTrue(dst.gyroEnabled); Assert.AreEqual(120, dst.sensGyro);
            // an old server that does not know the new keys leaves local values alone
            var old = Defaults(); old.mouseSmoothing = 70; old.ApplySyncMap(new System.Collections.Generic.Dictionary<string, object> { ["sensGeneral"] = 77.0 });
            Assert.AreEqual(70, old.mouseSmoothing); Assert.AreEqual(77, old.sensGeneral);
        }
    }
}
