using System;

namespace Noxy.Core
{
    public enum HitZone { Body, Head }

    /// <summary>Spread bloom and camera kick for sustained fire. Unity applies the numbers; the maths is testable here.</summary>
    public sealed class RecoilModel
    {
        private readonly float baseSpread, bloomPerShot, maxSpread, recovery, kickPerShot, maxKick, kickRecovery;
        private float bloom, kick;

        public RecoilModel(float baseSpread = 0.25f, float bloomPerShot = 0.35f, float maxSpread = 3f, float recoveryPerSecond = 4f,
            float kickPerShot = 0.9f, float maxKick = 8f, float kickRecoveryPerSecond = 10f)
        {
            this.baseSpread = baseSpread; this.bloomPerShot = bloomPerShot; this.maxSpread = maxSpread; recovery = recoveryPerSecond;
            this.kickPerShot = kickPerShot; this.maxKick = maxKick; kickRecovery = kickRecoveryPerSecond;
        }

        /// <summary>Half-angle of the firing cone in degrees.</summary>
        public float Spread(bool moving, bool aiming, bool crouching)
        {
            float s = baseSpread + bloom;
            if (moving) s *= 1.6f;
            if (aiming) s *= 0.5f;
            if (crouching) s *= 0.75f;
            return s;
        }

        /// <returns>Pitch kick in degrees to add to the camera for this shot.</returns>
        public float OnShot(bool aiming)
        {
            bloom = Math.Min(maxSpread - baseSpread, bloom + bloomPerShot);
            float k = kickPerShot * (aiming ? 0.6f : 1f);
            kick = Math.Min(maxKick, kick + k);
            return k;
        }

        /// <returns>Pitch degrees the camera should settle back down this frame (never more than the kick still outstanding).</returns>
        public float Tick(float dt)
        {
            bloom = Math.Max(0f, bloom - recovery * dt);
            float back = Math.Min(kick, kickRecovery * dt * 0.5f);
            kick -= back;
            return back;
        }

        public float Bloom => bloom;
        public float OutstandingKick => kick;
        public void Reset() { bloom = 0f; kick = 0f; }
    }

    public enum DrillRank { None, C, B, A, S }

    /// <summary>A timed accuracy drill: points for hits (more for headshots and distance), combo for consecutive hits.</summary>
    public sealed class TrainingDrill
    {
        public const int BodyPoints = 10, HeadPoints = 25, KillBonus = 50;

        private readonly float duration;
        private float elapsed;

        public TrainingDrill(float durationSeconds = 60f) { duration = durationSeconds; }

        public bool Running { get; private set; }
        public bool Finished { get; private set; }
        public int Score { get; private set; }
        public int Shots { get; private set; }
        public int Hits { get; private set; }
        public int Headshots { get; private set; }
        public int Kills { get; private set; }
        public int Combo { get; private set; }
        public int BestCombo { get; private set; }
        public float Remaining => Math.Max(0f, duration - elapsed);
        public float Accuracy => Shots == 0 ? 0f : (float)Hits / Shots;

        public void Start()
        {
            elapsed = 0f; Score = Shots = Hits = Headshots = Kills = Combo = BestCombo = 0;
            Running = true; Finished = false;
        }

        public void Tick(float dt)
        {
            if (!Running) return;
            elapsed += dt;
            if (elapsed >= duration) { Running = false; Finished = true; }
        }

        public void RegisterMiss()
        {
            if (!Running) return;
            Shots++; Combo = 0;
        }

        /// <returns>Points awarded for this hit.</returns>
        public int RegisterHit(HitZone zone, float distanceMeters, bool killed)
        {
            if (!Running) return 0;
            Shots++; Hits++; Combo++;
            BestCombo = Math.Max(BestCombo, Combo);
            int pts = zone == HitZone.Head ? HeadPoints : BodyPoints;
            if (zone == HitZone.Head) Headshots++;
            pts += (int)(Math.Max(0f, distanceMeters) / 10f);
            if (killed) { pts += KillBonus; Kills++; }
            pts = (int)Math.Round(pts * (1f + Math.Min(Combo, 10) * 0.1f));
            Score += pts;
            return pts;
        }

        public static DrillRank RankFor(int score) =>
            score >= 1500 ? DrillRank.S : score >= 900 ? DrillRank.A : score >= 450 ? DrillRank.B : score > 0 ? DrillRank.C : DrillRank.None;
    }

    public static class HitZones
    {
        public const float HeadMultiplier = 2f;
        public static float Damage(float baseDamage, HitZone zone) => zone == HitZone.Head ? baseDamage * HeadMultiplier : baseDamage;
    }
}
