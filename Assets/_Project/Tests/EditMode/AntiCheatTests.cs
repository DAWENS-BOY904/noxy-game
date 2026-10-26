using NUnit.Framework;
using Noxy.Core;

namespace Noxy.Tests
{
    public class AntiCheatTests
    {
        [Test] public void Movement_NormalPasses()
        {
            var m = new MovementValidator(6f);
            Assert.IsTrue(m.Check(new Vec3(0, 0, 0), 0).Ok);
            Assert.IsTrue(m.Check(new Vec3(5, 0, 0), 1).Ok);
        }
        [Test] public void Movement_SpeedHackFails()
        {
            var m = new MovementValidator(6f);
            m.Check(new Vec3(0, 0, 0), 0);
            Assert.AreEqual(CheatReason.Speed, m.Check(new Vec3(20, 0, 0), 1).Reason);
        }
        [Test] public void Movement_TeleportFails()
        {
            var m = new MovementValidator(6f);
            m.Check(new Vec3(0, 0, 0), 0);
            Assert.AreEqual(CheatReason.Teleport, m.Check(new Vec3(100, 0, 0), 10).Reason);
        }
        [Test] public void Movement_ZeroDtMoveFails()
        {
            var m = new MovementValidator(6f);
            m.Check(new Vec3(0, 0, 0), 5);
            Assert.IsFalse(m.Check(new Vec3(3, 0, 0), 5).Ok);
        }
        [Test] public void Fire_RateLimited()
        {
            var f = new FireValidator(5f, 30, 2f);
            Assert.IsTrue(f.Fire(0).Ok);
            Assert.AreEqual(CheatReason.FireRate, f.Fire(0.05).Reason);
            Assert.IsTrue(f.Fire(0.2).Ok);
        }
        [Test] public void Fire_OutOfAmmoFails()
        {
            var f = new FireValidator(100f, 2, 2f);
            f.Fire(0); f.Fire(1);
            Assert.AreEqual(CheatReason.Ammo, f.Fire(2).Reason);
        }
        [Test] public void Fire_DuringReloadFailsThenWorks()
        {
            var f = new FireValidator(100f, 1, 2f);
            f.Fire(0);
            Assert.IsTrue(f.Reload(1).Ok);
            Assert.AreEqual(CheatReason.Reload, f.Fire(2).Reason);
            Assert.IsTrue(f.Fire(3.1).Ok);
        }
        [Test] public void Reload_CannotBeRestarted()
        {
            var f = new FireValidator(5f, 10, 2f);
            f.Reload(0);
            Assert.IsFalse(f.Reload(1).Ok);
        }
        [Test] public void Hit_OutOfRangeFails()
        {
            Assert.AreEqual(CheatReason.HitRange, HitValidator.Check(new Vec3(0, 0, 0), new Vec3(200, 0, 0), 50, 20, 30, false).Reason);
        }
        [Test] public void Hit_ThroughWallFails()
        {
            Assert.AreEqual(CheatReason.HitThroughWall, HitValidator.Check(new Vec3(0, 0, 0), new Vec3(10, 0, 0), 50, 20, 30, true).Reason);
        }
        [Test] public void Hit_DamageClamp()
        {
            Assert.AreEqual(CheatReason.Damage, HitValidator.Check(new Vec3(0, 0, 0), new Vec3(10, 0, 0), 50, 999, 30, false).Reason);
            Assert.IsTrue(HitValidator.Check(new Vec3(0, 0, 0), new Vec3(10, 0, 0), 50, 25, 30, false).Ok);
        }
        [Test] public void Suspicion_FlagsAtThresholdWithinWindow()
        {
            var s = new SuspicionTracker(3, 10);
            Assert.IsFalse(s.Record("a", 0));
            Assert.IsFalse(s.Record("a", 1));
            Assert.IsTrue(s.Record("a", 2));
        }
        [Test] public void Suspicion_OldHitsExpire()
        {
            var s = new SuspicionTracker(3, 10);
            s.Record("a", 0); s.Record("a", 1);
            Assert.IsFalse(s.Record("a", 50));
        }
    }
}
