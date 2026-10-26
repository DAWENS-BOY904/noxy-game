using NUnit.Framework;
using Noxy.Core;

namespace Noxy.Tests
{
    public class TrainingLogicTests
    {
        [Test] public void Recoil_BloomGrowsAndIsCapped()
        {
            var r = new RecoilModel();
            float s0 = r.Spread(false, false, false);
            for (int i = 0; i < 50; i++) r.OnShot(false);
            float s1 = r.Spread(false, false, false);
            Assert.Greater(s1, s0);
            Assert.LessOrEqual(s1, 3f + 0.0001f);
        }
        [Test] public void Recoil_RecoversOverTime()
        {
            var r = new RecoilModel();
            for (int i = 0; i < 10; i++) r.OnShot(false);
            for (int i = 0; i < 100; i++) r.Tick(0.1f);
            Assert.AreEqual(0f, r.Bloom, 0.0001f);
            Assert.AreEqual(0f, r.OutstandingKick, 0.0001f);
        }
        [Test] public void Recoil_AimCrouchTighterMovingWider()
        {
            var r = new RecoilModel();
            float stand = r.Spread(false, false, false);
            Assert.Less(r.Spread(false, true, false), stand);
            Assert.Less(r.Spread(false, false, true), stand);
            Assert.Greater(r.Spread(true, false, false), stand);
        }
        [Test] public void Recoil_KickCappedAndAimReduces()
        {
            var r = new RecoilModel();
            float hip = r.OnShot(false);
            r.Reset();
            float ads = r.OnShot(true);
            Assert.Less(ads, hip);
            for (int i = 0; i < 100; i++) r.OnShot(false);
            Assert.LessOrEqual(r.OutstandingKick, 8f + 0.0001f);
        }
        [Test] public void Recoil_TickNeverReturnsMoreThanKick()
        {
            var r = new RecoilModel();
            r.OnShot(false);
            float total = 0f;
            for (int i = 0; i < 200; i++) total += r.Tick(0.1f);
            Assert.AreEqual(0.9f, total, 0.0001f);
        }

        [Test] public void Drill_DoesNothingUntilStarted()
        {
            var d = new TrainingDrill();
            Assert.AreEqual(0, d.RegisterHit(HitZone.Head, 10, true));
            d.RegisterMiss();
            Assert.AreEqual(0, d.Shots);
        }
        [Test] public void Drill_HeadshotWorthMoreThanBody()
        {
            var a = new TrainingDrill(); a.Start();
            var b = new TrainingDrill(); b.Start();
            Assert.Greater(a.RegisterHit(HitZone.Head, 0, false), b.RegisterHit(HitZone.Body, 0, false));
            Assert.AreEqual(1, a.Headshots);
        }
        [Test] public void Drill_DistanceAndKillBonus()
        {
            var near = new TrainingDrill(); near.Start();
            var far = new TrainingDrill(); far.Start();
            Assert.Greater(far.RegisterHit(HitZone.Body, 40, false), near.RegisterHit(HitZone.Body, 0, false));
            var k = new TrainingDrill(); k.Start();
            Assert.GreaterOrEqual(k.RegisterHit(HitZone.Body, 0, true), TrainingDrill.BodyPoints + TrainingDrill.KillBonus);
            Assert.AreEqual(1, k.Kills);
        }
        [Test] public void Drill_ComboBuildsAndMissResets()
        {
            var d = new TrainingDrill(); d.Start();
            int first = d.RegisterHit(HitZone.Body, 0, false);
            int second = d.RegisterHit(HitZone.Body, 0, false);
            Assert.Greater(second, first);
            d.RegisterMiss();
            Assert.AreEqual(0, d.Combo);
            Assert.AreEqual(2, d.BestCombo);
        }
        [Test] public void Drill_ComboMultiplierCapped()
        {
            var d = new TrainingDrill(); d.Start();
            int last = 0;
            for (int i = 0; i < 30; i++) last = d.RegisterHit(HitZone.Body, 0, false);
            Assert.AreEqual(20, last);
        }
        [Test] public void Drill_AccuracyAndTimeout()
        {
            var d = new TrainingDrill(10f); d.Start();
            d.RegisterHit(HitZone.Body, 0, false); d.RegisterMiss();
            Assert.AreEqual(0.5f, d.Accuracy, 0.0001f);
            d.Tick(4f);
            Assert.IsTrue(d.Running);
            d.Tick(6f);
            Assert.IsTrue(d.Finished);
            Assert.IsFalse(d.Running);
            int score = d.Score;
            d.RegisterHit(HitZone.Head, 0, true);
            Assert.AreEqual(score, d.Score);
        }
        [Test] public void Drill_RestartClearsState()
        {
            var d = new TrainingDrill(); d.Start();
            d.RegisterHit(HitZone.Head, 0, true);
            d.Start();
            Assert.AreEqual(0, d.Score);
            Assert.AreEqual(0, d.Shots);
            Assert.IsTrue(d.Running);
        }
        [Test] public void Rank_Thresholds()
        {
            Assert.AreEqual(DrillRank.None, TrainingDrill.RankFor(0));
            Assert.AreEqual(DrillRank.C, TrainingDrill.RankFor(1));
            Assert.AreEqual(DrillRank.B, TrainingDrill.RankFor(450));
            Assert.AreEqual(DrillRank.A, TrainingDrill.RankFor(900));
            Assert.AreEqual(DrillRank.S, TrainingDrill.RankFor(1500));
        }
        [Test] public void Headshot_DoublesDamage()
        {
            Assert.AreEqual(50f, HitZones.Damage(25f, HitZone.Head));
            Assert.AreEqual(25f, HitZones.Damage(25f, HitZone.Body));
        }
    }
}
