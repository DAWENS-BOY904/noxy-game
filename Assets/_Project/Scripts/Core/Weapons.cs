using System;
using UnityEngine;

namespace Noxy.Core
{
    public enum WeaponSlot { Primary = 0, Secondary = 1, Melee = 2 }

    public sealed class WeaponDefinition
    {
        public string Id;
        public string DisplayName;
        public WeaponSlot Slot;
        public int MagazineSize;     // 0 for melee
        public int ReserveAmmo;
        public float FireInterval;   // seconds between shots
        public float ReloadSeconds;
        public bool Automatic;
        public float Range;
    }

    public static class DefaultWeapons
    {
        public static readonly WeaponDefinition Rifle = new WeaponDefinition
        {
            Id = "rifle_ar", DisplayName = "AR-7 Rifle", Slot = WeaponSlot.Primary, MagazineSize = 30, ReserveAmmo = 120,
            FireInterval = 0.1f, ReloadSeconds = 2f, Automatic = true, Range = 120f
        };
        public static readonly WeaponDefinition Pistol = new WeaponDefinition
        {
            Id = "pistol_p9", DisplayName = "P9 Pistol", Slot = WeaponSlot.Secondary, MagazineSize = 12, ReserveAmmo = 48,
            FireInterval = 0.25f, ReloadSeconds = 1.3f, Automatic = false, Range = 70f
        };
        public static readonly WeaponDefinition Knife = new WeaponDefinition
        {
            Id = "melee_knife", DisplayName = "Combat Knife", Slot = WeaponSlot.Melee, MagazineSize = 0, ReserveAmmo = 0,
            FireInterval = 0.5f, ReloadSeconds = 0f, Automatic = false, Range = 2.2f
        };
    }

    public enum FireResult { Fired, Empty, Reloading, Switching, Cooldown, NoWeapon }
    public enum SwitchResult { Switched, AlreadySelected, SlotEmpty, Busy }
    public enum ReloadResult { Started, Full, NoReserve, Busy, NotReloadable, NoWeapon }

    /// <summary>
    /// Three weapon slots with ammo, reload and switch timing. Pure logic: input layers (keyboard, touch buttons)
    /// call TrySwitch/TryFire/TryReload and the HUD reads the state.
    /// </summary>
    public sealed class WeaponInventory
    {
        public const float SwitchSeconds = 0.35f;

        private sealed class Slot
        {
            public WeaponDefinition Def;
            public int Magazine;
            public int Reserve;
        }

        private readonly Slot[] slots = new Slot[3];
        private float switchTimer;
        private float reloadTimer;
        private float cooldownTimer;

        public WeaponSlot Selected { get; private set; } = WeaponSlot.Primary;
        public bool IsReloading => reloadTimer > 0f;
        public bool IsSwitching => switchTimer > 0f;

        public event Action Changed;

        public bool HasWeapon(WeaponSlot slot) => slots[(int)slot] != null;
        public WeaponDefinition Weapon(WeaponSlot slot) => slots[(int)slot]?.Def;
        public WeaponDefinition CurrentWeapon => Weapon(Selected);
        public int Magazine(WeaponSlot slot) => slots[(int)slot]?.Magazine ?? 0;
        public int Reserve(WeaponSlot slot) => slots[(int)slot]?.Reserve ?? 0;

        /// <summary>Equips (or clears with null) the weapon for its slot and selects the first available slot.</summary>
        public void Equip(WeaponDefinition def, WeaponSlot slot)
        {
            slots[(int)slot] = def == null ? null : new Slot { Def = def, Magazine = def.MagazineSize, Reserve = def.ReserveAmmo };
            if (!HasWeapon(Selected))
            {
                for (int i = 0; i < slots.Length; i++)
                    if (slots[i] != null) { Selected = (WeaponSlot)i; break; }
            }
            Changed?.Invoke();
        }

        /// <summary>Online matches: overwrite a slot with what the server says the player owns (null clears it).</summary>
        public void SetAuthoritative(WeaponSlot slot, WeaponDefinition def, int magazine, int reserve)
        {
            slots[(int)slot] = def == null ? null : new Slot { Def = def, Magazine = magazine, Reserve = reserve };
            if (!HasWeapon(Selected))
                for (int i = 0; i < slots.Length; i++) if (slots[i] != null) { Selected = (WeaponSlot)i; break; }
            Changed?.Invoke();
        }

        /// <summary>Online matches: the server's selected slot wins over local prediction.</summary>
        public void SelectAuthoritative(WeaponSlot slot)
        {
            if (!HasWeapon(slot) || slot == Selected) return;
            Selected = slot; reloadTimer = 0f; switchTimer = 0f;
            Changed?.Invoke();
        }

        public SwitchResult TrySwitch(WeaponSlot slot)
        {
            if (!HasWeapon(slot)) return SwitchResult.SlotEmpty;
            if (slot == Selected) return SwitchResult.AlreadySelected;
            if (IsSwitching) return SwitchResult.Busy;

            reloadTimer = 0f; // switching away cancels the reload
            Selected = slot;
            switchTimer = SwitchSeconds;
            cooldownTimer = 0f;
            Changed?.Invoke();
            return SwitchResult.Switched;
        }

        public FireResult TryFire()
        {
            var s = slots[(int)Selected];
            if (s == null) return FireResult.NoWeapon;
            if (IsSwitching) return FireResult.Switching;
            if (IsReloading) return FireResult.Reloading;
            if (cooldownTimer > 0f) return FireResult.Cooldown;

            if (s.Def.MagazineSize > 0)
            {
                if (s.Magazine <= 0) return FireResult.Empty;
                s.Magazine--;
            }
            cooldownTimer = s.Def.FireInterval;
            Changed?.Invoke();
            return FireResult.Fired;
        }

        public ReloadResult TryReload()
        {
            var s = slots[(int)Selected];
            if (s == null) return ReloadResult.NoWeapon;
            if (s.Def.MagazineSize <= 0) return ReloadResult.NotReloadable;
            if (IsSwitching || IsReloading) return ReloadResult.Busy;
            if (s.Magazine >= s.Def.MagazineSize) return ReloadResult.Full;
            if (s.Reserve <= 0) return ReloadResult.NoReserve;

            reloadTimer = s.Def.ReloadSeconds;
            Changed?.Invoke();
            return ReloadResult.Started;
        }

        public float ReloadProgress
        {
            get
            {
                var s = slots[(int)Selected];
                if (s == null || !IsReloading || s.Def.ReloadSeconds <= 0f) return 0f;
                return 1f - reloadTimer / s.Def.ReloadSeconds;
            }
        }

        public void Tick(float dt)
        {
            if (cooldownTimer > 0f) cooldownTimer = Mathf.Max(0f, cooldownTimer - dt);
            if (switchTimer > 0f)
            {
                switchTimer = Mathf.Max(0f, switchTimer - dt);
                if (switchTimer <= 0f) Changed?.Invoke();
            }
            if (reloadTimer > 0f)
            {
                reloadTimer -= dt;
                if (reloadTimer <= 0f)
                {
                    reloadTimer = 0f;
                    var s = slots[(int)Selected];
                    if (s != null)
                    {
                        int need = s.Def.MagazineSize - s.Magazine;
                        int take = Mathf.Min(need, s.Reserve);
                        s.Magazine += take;
                        s.Reserve -= take;
                    }
                    Changed?.Invoke();
                }
            }
        }
    }

    public enum RadarMarkerKind { Teammate, Objective, SafeZone, Mission, PracticeTarget, Enemy }

    /// <summary>
    /// What the radar is allowed to show per game mode. Enemies are never allowed: in online modes the server decides
    /// what a client may know, and it must not stream hidden enemy positions just so a radar can draw them.
    /// </summary>
    public static class RadarPolicy
    {
        public static bool Allows(string modeId, RadarMarkerKind kind)
        {
            if (kind == RadarMarkerKind.Enemy) return false;
            switch (modeId)
            {
                case GameModeIds.Training:
                    return kind == RadarMarkerKind.PracticeTarget || kind == RadarMarkerKind.Objective;
                case GameModeIds.BattleRoyale:
                    return kind == RadarMarkerKind.Teammate || kind == RadarMarkerKind.SafeZone || kind == RadarMarkerKind.Objective || kind == RadarMarkerKind.Mission;
                case GameModeIds.LoneWolf:
                    return kind == RadarMarkerKind.Objective;
                default:
                    return false;
            }
        }

        /// <summary>World distance (meters) shown at the radar edge.</summary>
        public static float Range(string modeId, bool expanded)
        {
            float baseRange = modeId == GameModeIds.BattleRoyale ? 120f : modeId == GameModeIds.LoneWolf ? 40f : 45f;
            return expanded ? baseRange * 2f : baseRange;
        }
    }

    /// <summary>Radar coordinate math (no scene dependencies).</summary>
    public static class RadarMath
    {
        public struct Blip
        {
            public float X;          // radar-space units, +x right, +y up, within [-1, 1] of the radar radius
            public float Y;
            public bool InRange;     // false when the target is beyond the radar range (blip is clamped to the edge)
            public float Distance;
        }

        /// <param name="dx">target.x - player.x in world units</param>
        /// <param name="dz">target.z - player.z in world units</param>
        /// <param name="playerYawDegrees">0 = facing +z (north); positive turns right/clockwise</param>
        /// <param name="rangeMeters">world distance mapped to the radar edge</param>
        /// <param name="northUp">true: map fixed to north; false: map rotates so the player faces up</param>
        public static Blip ToRadar(float dx, float dz, float playerYawDegrees, float rangeMeters, bool northUp)
        {
            float dist = (float)Math.Sqrt(dx * dx + dz * dz);
            float rx = dx, ry = dz;
            if (!northUp)
            {
                float a = playerYawDegrees * (float)(Math.PI / 180.0);
                float cos = (float)Math.Cos(a), sin = (float)Math.Sin(a);
                rx = dx * cos - dz * sin;
                ry = dx * sin + dz * cos;
            }

            float scale = rangeMeters > 0f ? 1f / rangeMeters : 0f;
            float x = rx * scale, y = ry * scale;
            float mag = (float)Math.Sqrt(x * x + y * y);
            bool inRange = mag <= 1f;
            if (!inRange && mag > 0f) { x /= mag; y /= mag; }
            return new Blip { X = x, Y = y, InRange = inRange, Distance = dist };
        }

        /// <summary>Angle (degrees, clockwise from up) the player's facing arrow should be drawn at.</summary>
        public static float PlayerArrowAngle(float playerYawDegrees, bool northUp) => northUp ? playerYawDegrees : 0f;
    }
}
