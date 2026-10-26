using System;
using System.Collections.Generic;

namespace Noxy.Core
{
    public enum CheatReason { None, Speed, Teleport, FireRate, Ammo, Reload, HitRange, HitThroughWall, Damage }

    public readonly struct Vec3
    {
        public readonly float X, Y, Z;
        public Vec3(float x, float y, float z) { X = x; Y = y; Z = z; }
        public float Distance(Vec3 o)
        {
            float dx = X - o.X, dy = Y - o.Y, dz = Z - o.Z;
            return (float)Math.Sqrt(dx * dx + dy * dy + dz * dz);
        }
    }

    public readonly struct Verdict
    {
        public readonly bool Ok;
        public readonly CheatReason Reason;
        public Verdict(bool ok, CheatReason reason) { Ok = ok; Reason = reason; }
        public static readonly Verdict Pass = new Verdict(true, CheatReason.None);
        public static Verdict Fail(CheatReason r) => new Verdict(false, r);
    }

    /// <summary>
    /// Authoritative checks meant to run on the dedicated server (or in tests). A client can run them too, but a
    /// client-side result can be bypassed and must never be trusted.
    /// </summary>
    public sealed class MovementValidator
    {
        private readonly float maxSpeed, tolerance, teleportDistance;
        private Vec3 last;
        private double lastTime;
        private bool has;

        public MovementValidator(float maxSpeed, float tolerance = 1.25f, float teleportDistance = 25f)
        {
            this.maxSpeed = maxSpeed; this.tolerance = tolerance; this.teleportDistance = teleportDistance;
        }

        public Verdict Check(Vec3 pos, double time)
        {
            if (!has) { has = true; last = pos; lastTime = time; return Verdict.Pass; }
            double dt = time - lastTime;
            float dist = last.Distance(pos);
            last = pos; lastTime = time;
            if (dt <= 0) return dist > 0.01f ? Verdict.Fail(CheatReason.Teleport) : Verdict.Pass;
            if (dist > teleportDistance) return Verdict.Fail(CheatReason.Teleport);
            return dist / dt > maxSpeed * tolerance ? Verdict.Fail(CheatReason.Speed) : Verdict.Pass;
        }

        public void Reset() { has = false; }
    }

    public sealed class FireValidator
    {
        private readonly float interval;
        private readonly int magazine;
        private readonly float reloadSeconds;
        private double lastShot = double.NegativeInfinity;
        private double reloadEnds = double.NegativeInfinity;
        private int ammo;

        public int Ammo => ammo;

        public FireValidator(float shotsPerSecond, int magazine, float reloadSeconds)
        {
            interval = 1f / Math.Max(0.01f, shotsPerSecond);
            this.magazine = magazine; ammo = magazine; this.reloadSeconds = reloadSeconds;
        }

        public Verdict Fire(double time)
        {
            if (time < reloadEnds) return Verdict.Fail(CheatReason.Reload);
            if (ammo <= 0) return Verdict.Fail(CheatReason.Ammo);
            // 10% slack for network jitter.
            if (time - lastShot < interval * 0.9) return Verdict.Fail(CheatReason.FireRate);
            lastShot = time; ammo--;
            return Verdict.Pass;
        }

        public Verdict Reload(double time)
        {
            if (time < reloadEnds) return Verdict.Fail(CheatReason.Reload);
            reloadEnds = time + reloadSeconds; ammo = magazine;
            return Verdict.Pass;
        }
    }

    public static class HitValidator
    {
        /// <param name="blocked">Result of a server-side line-of-sight test between shooter and target.</param>
        public static Verdict Check(Vec3 shooter, Vec3 target, float weaponRange, float claimedDamage, float maxDamage, bool blocked, float slack = 1.5f)
        {
            if (shooter.Distance(target) > weaponRange + slack) return Verdict.Fail(CheatReason.HitRange);
            if (blocked) return Verdict.Fail(CheatReason.HitThroughWall);
            if (claimedDamage > maxDamage || claimedDamage < 0) return Verdict.Fail(CheatReason.Damage);
            return Verdict.Pass;
        }
    }

    /// <summary>Counts violations per player and recommends an action once a threshold is crossed within a window.</summary>
    public sealed class SuspicionTracker
    {
        private readonly int threshold;
        private readonly double window;
        private readonly Dictionary<string, List<double>> hits = new Dictionary<string, List<double>>();

        public SuspicionTracker(int threshold = 5, double windowSeconds = 30) { this.threshold = threshold; window = windowSeconds; }

        /// <returns>True when the player should be flagged for review/kicked.</returns>
        public bool Record(string playerId, double time)
        {
            if (!hits.TryGetValue(playerId, out var list)) hits[playerId] = list = new List<double>();
            list.RemoveAll(t => time - t > window);
            list.Add(time);
            return list.Count >= threshold;
        }
    }
}
