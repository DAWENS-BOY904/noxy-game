using System;
using Noxy.Core;
using UnityEngine;

namespace Noxy.Lobby
{
    public enum TargetKind { Static, Moving, Popup }

    /// <summary>Practice target: takes damage, flashes, and respawns. Purely local; there is no networking here.</summary>
    public sealed class TargetDummy : MonoBehaviour
    {
        public const float MaxHealth = 100f;
        public const float RespawnSeconds = 3f;

        private float health = MaxHealth;
        private float respawnTimer;
        private float flashTimer;
        private Renderer[] renderers;
        private Collider[] colliders;
        private Color baseColor;
        private Material material;

        public bool IsAlive => health > 0f;

        public TargetKind Kind = TargetKind.Static;
        public float MoveRange = 4f, MoveSpeed = 1.5f;
        private Vector3 home;
        private float phase;
        private float popTimer;
        private bool popped = true;

        public event Action<TargetDummy> Died;

        private void Awake()
        {
            home = transform.position;
            phase = UnityEngine.Random.value * 6.28f;
            renderers = GetComponentsInChildren<Renderer>();
            colliders = GetComponentsInChildren<Collider>();
            if (renderers.Length > 0) { material = renderers[0].material; baseColor = material.HasProperty("_Color") ? material.color : Color.white; }
        }

        /// <returns>true when this hit killed the target</returns>
        public bool ApplyDamage(float amount)
        {
            if (!IsAlive || !popped) return false;
            health -= amount;
            flashTimer = 0.12f;
            if (health > 0f) return false;

            respawnTimer = RespawnSeconds;
            SetVisible(false);
            Died?.Invoke(this);
            return true;
        }

        private void SetVisible(bool visible)
        {
            foreach (var r in renderers) r.enabled = visible;
            foreach (var c in colliders) c.enabled = visible;
        }

        private void Update()
        {
            if (!IsAlive)
            {
                respawnTimer -= Time.deltaTime;
                if (respawnTimer <= 0f) { health = MaxHealth; popped = true; popTimer = 2f; SetVisible(true); }
                return;
            }

            if (Kind == TargetKind.Moving)
                transform.position = home + Vector3.right * Mathf.Sin(Time.time * MoveSpeed + phase) * MoveRange;
            else if (Kind == TargetKind.Popup)
            {
                popTimer -= Time.deltaTime;
                if (popTimer <= 0f)
                {
                    popped = !popped;
                    popTimer = popped ? 2.2f : 1.4f;
                    SetVisible(popped);
                }
            }
            if (material == null) return;
            flashTimer = Mathf.Max(0f, flashTimer - Time.deltaTime);
            MaterialFactory.SetColor(material, flashTimer > 0f ? Color.white : baseColor);
        }
    }

    /// <summary>
    /// Third-person controller for the Training range. Movement, camera and combat read input from the keyboard/mouse
    /// (using the player's key bindings) and from the on-screen touch controls, and apply the player's sensitivity
    /// settings through SensitivityModel.
    /// </summary>
    public sealed class PlayerController : MonoBehaviour
    {
        private const float WalkSpeed = 4.2f, SprintSpeed = 6.8f, CrouchSpeed = 2.2f, JumpSpeed = 5.6f, Gravity = -20f;

        private CharacterController body;
        private ICharacterView view;
        private Camera cam;
        private LineRenderer tracer;
        private float tracerTimer;
        private float yaw, pitch, verticalVelocity, currentFov = 60f, cameraDistance = 3.2f;
        private bool crouching, moving;
        private readonly RecoilModel recoil = new RecoilModel();

        public WeaponInventory Weapons { get; } = new WeaponInventory();
        public HudControlsView Touch;
        public bool InputEnabled = true;
        public bool UseMouseLook = true;
        public bool Aiming { get; private set; }
        public int Shots { get; private set; }
        public int Hits { get; private set; }
        public float CurrentSpreadDegrees => recoil.Spread(moving, Aiming, crouching);

        public event Action<bool> HitRegistered;      // true when the hit killed the target
        /// <summary>Raised for every bullet: whether it hit a target, the zone, the distance and whether it killed.</summary>
        public event Action<bool, HitZone, float, bool> ShotResolved;
        public event Action<string> Message;

        public float Yaw => yaw;

        public void Setup(Camera camera, ICharacterView characterView)
        {
            cam = camera;
            view = characterView;
            body = gameObject.AddComponent<CharacterController>();
            body.height = 1.8f;
            body.radius = 0.35f;
            body.center = new Vector3(0f, 0.9f, 0f);
            body.stepOffset = 0.35f;

            var lineGo = new GameObject("Tracer");
            lineGo.transform.SetParent(transform, false);
            tracer = lineGo.AddComponent<LineRenderer>();
            tracer.positionCount = 2;
            tracer.widthMultiplier = 0.03f;
            tracer.useWorldSpace = true;
            tracer.sharedMaterial = MaterialFactory.Unlit(new Color(1f, 0.85f, 0.4f));
            tracer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            tracer.enabled = false;

            view.Root.SetParent(transform, false);
            view.Root.localPosition = Vector3.zero;

            Weapons.Equip(DefaultWeapons.Rifle, WeaponSlot.Primary);
            Weapons.Equip(DefaultWeapons.Pistol, WeaponSlot.Secondary);
            Weapons.Equip(DefaultWeapons.Knife, WeaponSlot.Melee);
            view.SetHeldWeapon((int)Weapons.Selected);
            yaw = transform.eulerAngles.y;
            pitch = 12f;
        }

        private static float Damage(WeaponDefinition w)
        {
            switch (w.Slot)
            {
                case WeaponSlot.Primary: return 20f;
                case WeaponSlot.Secondary: return 34f;
                default: return 50f;
            }
        }

        private void Update()
        {
            if (body == null) return;
            float dt = Time.deltaTime;

            Weapons.Tick(dt);
            pitch = Mathf.Clamp(pitch + recoil.Tick(dt), -45f, 70f); // camera settles back after a burst
            if (tracer.enabled)
            {
                tracerTimer -= dt;
                if (tracerTimer <= 0f) tracer.enabled = false;
            }

            if (!InputEnabled)
            {
                Touch?.State.ClearPresses();
                return;
            }

            var s = AppServices.Config.Settings;
            var keys = AppServices.Config.Bindings;
            var touch = Touch != null ? Touch.State : null;

            Aiming = InputCompat.KeyHeld(keys.Get(GameAction.Aim)) || (touch != null && touch.Aim);

            // Look: sensitivity is applied here, so the Settings sliders change real camera behavior.
            Vector2 look = Vector2.zero;
            if (UseMouseLook) look += SensitivityModel.LookDelta(InputCompat.MouseDelta, LookSource.Mouse, Aiming, s);
            if (touch != null) look += SensitivityModel.LookDelta(touch.ConsumeLook(), LookSource.Touch, Aiming, s);
            yaw += look.x;
            pitch = Mathf.Clamp(pitch + look.y, -45f, 70f);

            // Move
            Vector2 move = Vector2.zero;
            if (InputCompat.KeyHeld(keys.Get(GameAction.MoveForward))) move.y += 1f;
            if (InputCompat.KeyHeld(keys.Get(GameAction.MoveBack))) move.y -= 1f;
            if (InputCompat.KeyHeld(keys.Get(GameAction.MoveRight))) move.x += 1f;
            if (InputCompat.KeyHeld(keys.Get(GameAction.MoveLeft))) move.x -= 1f;
            if (touch != null) move += touch.Move;
            move = Vector2.ClampMagnitude(move, 1f);
            moving = move.sqrMagnitude > 0.01f;

            crouching = InputCompat.KeyHeld(keys.Get(GameAction.Crouch)) || (touch != null && touch.CrouchToggled);
            bool sprint = !crouching && (InputCompat.KeyHeld(keys.Get(GameAction.Sprint)) || (touch != null && touch.Sprint)) && move.y > 0.1f;
            float speed = crouching ? CrouchSpeed : (sprint ? SprintSpeed : WalkSpeed);
            if (Aiming) speed *= 0.6f;

            var flat = Quaternion.Euler(0f, yaw, 0f);
            Vector3 wish = (flat * Vector3.forward * move.y + flat * Vector3.right * move.x) * speed;

            bool grounded = body.isGrounded;
            if (grounded && verticalVelocity < 0f) verticalVelocity = -1f;
            bool jump = InputCompat.KeyDown(keys.Get(GameAction.Jump)) || (touch != null && touch.JumpPressed);
            if (jump && grounded && !crouching) verticalVelocity = JumpSpeed;
            verticalVelocity += Gravity * dt;
            body.Move((wish + Vector3.up * verticalVelocity) * dt);

            body.height = crouching ? 1.2f : 1.8f;
            body.center = new Vector3(0f, body.height * 0.5f, 0f);
            view.Root.localScale = new Vector3(1f, crouching ? 0.75f : 1f, 1f);

            // Face the camera direction while aiming or shooting, otherwise the move direction.
            bool wantsFire = InputCompat.KeyHeld(keys.Get(GameAction.Fire)) || (touch != null && touch.Fire);
            if (Aiming || wantsFire)
                transform.rotation = Quaternion.Slerp(transform.rotation, Quaternion.Euler(0f, yaw, 0f), 1f - Mathf.Exp(-16f * dt));
            else if (wish.sqrMagnitude > 0.01f)
                transform.rotation = Quaternion.Slerp(transform.rotation, Quaternion.LookRotation(wish), 1f - Mathf.Exp(-12f * dt));

            HandleWeapons(keys, touch, wantsFire);
            touch?.ClearPresses();
        }

        private void HandleWeapons(KeyBindings keys, TouchControlState touch, bool wantsFire)
        {
            if (InputCompat.KeyDown(keys.Get(GameAction.Slot1)) || (touch != null && touch.SlotPressed[0])) Switch(WeaponSlot.Primary);
            if (InputCompat.KeyDown(keys.Get(GameAction.Slot2)) || (touch != null && touch.SlotPressed[1])) Switch(WeaponSlot.Secondary);
            if (InputCompat.KeyDown(keys.Get(GameAction.Slot3)) || (touch != null && touch.SlotPressed[2])) Switch(WeaponSlot.Melee);

            if (InputCompat.KeyDown(keys.Get(GameAction.Reload)) || (touch != null && touch.ReloadPressed))
            {
                switch (Weapons.TryReload())
                {
                    case ReloadResult.NoReserve: Message?.Invoke("No reserve ammo"); break;
                    case ReloadResult.Full: Message?.Invoke("Magazine full"); break;
                }
            }

            bool touchEdge = TouchFireEdge(touch);
            var weapon = Weapons.CurrentWeapon;
            if (weapon == null || !wantsFire) return;

            bool pressedThisFrame = InputCompat.KeyDown(keys.Get(GameAction.Fire)) || touchEdge;
            if (!weapon.Automatic && !pressedThisFrame) return;

            switch (Weapons.TryFire())
            {
                case FireResult.Fired: Shoot(weapon); break;
                case FireResult.Empty:
                    if (pressedThisFrame) Message?.Invoke("Empty - press reload");
                    break;
            }
        }

        private bool prevTouchFire;

        // Evaluated every frame so the edge resets when the finger lifts.
        private bool TouchFireEdge(TouchControlState touch)
        {
            bool now = touch != null && touch.Fire;
            bool edge = now && !prevTouchFire;
            prevTouchFire = now;
            return edge;
        }

        private void Switch(WeaponSlot slot)
        {
            switch (Weapons.TrySwitch(slot))
            {
                case SwitchResult.Switched: view.SetHeldWeapon((int)slot); break;
                case SwitchResult.SlotEmpty: Message?.Invoke("Nothing equipped in that slot"); break;
            }
        }

        private void Shoot(WeaponDefinition weapon)
        {
            Shots++;
            Vector3 pivot = transform.position + Vector3.up * (crouching ? 1.1f : 1.5f);
            Vector3 dir = cam.transform.forward;
            float skip = Vector3.Distance(cam.transform.position, pivot) + 0.2f; // start beyond the player's own collider
            Vector3 origin = cam.transform.position + dir * skip;
            float range = weapon.Range;
            bool ranged = weapon.Slot != WeaponSlot.Melee;
            if (ranged)
            {
                // Random point inside the firing cone; bloom grows while firing and while moving.
                float cone = Mathf.Tan(recoil.Spread(moving, Aiming, crouching) * Mathf.Deg2Rad);
                Vector2 off = UnityEngine.Random.insideUnitCircle * cone;
                dir = (dir + cam.transform.right * off.x + cam.transform.up * off.y).normalized;
                pitch = Mathf.Clamp(pitch - recoil.OnShot(Aiming), -45f, 70f);
            }

            Vector3 end = origin + dir * range;
            if (Physics.Raycast(origin, dir, out var hit, range, ~0, QueryTriggerInteraction.Ignore))
            {
                end = hit.point;
                var dummy = hit.collider.GetComponentInParent<TargetDummy>();
                if (dummy != null)
                {
                    Hits++;
                    var zone = hit.collider.name == "Head" ? HitZone.Head : HitZone.Body;
                    bool killed = dummy.ApplyDamage(HitZones.Damage(Damage(weapon), zone));
                    HitRegistered?.Invoke(killed);
                    ShotResolved?.Invoke(true, zone, hit.distance, killed);
                }
                else ShotResolved?.Invoke(false, HitZone.Body, hit.distance, false);
            }
            else ShotResolved?.Invoke(false, HitZone.Body, range, false);

            if (weapon.Slot != WeaponSlot.Melee)
            {
                Vector3 muzzle = transform.position + Vector3.up * 1.25f + transform.right * 0.25f + transform.forward * 0.6f;
                tracer.SetPosition(0, muzzle);
                tracer.SetPosition(1, end);
                tracer.enabled = true;
                tracerTimer = 0.05f;
            }
        }

        private void LateUpdate()
        {
            if (cam == null) return;
            float dt = Time.unscaledDeltaTime;

            Vector3 pivot = transform.position + Vector3.up * (crouching ? 1.15f : 1.55f);
            var rot = Quaternion.Euler(pitch, yaw, 0f);
            float targetDistance = Aiming ? 1.9f : 3.2f;
            float shoulder = Aiming ? 0.45f : 0.65f;
            cameraDistance = Mathf.Lerp(cameraDistance, targetDistance, 1f - Mathf.Exp(-12f * dt));

            Vector3 desired = pivot + rot * new Vector3(shoulder, 0f, -cameraDistance);
            if (Physics.Linecast(pivot, desired, out var hit, ~0, QueryTriggerInteraction.Ignore) && hit.collider.GetComponentInParent<PlayerController>() == null)
                desired = hit.point + hit.normal * 0.2f;

            cam.transform.position = desired;
            cam.transform.rotation = rot;

            float targetFov = Aiming ? 45f : 60f;
            currentFov = Mathf.Lerp(currentFov, targetFov, 1f - Mathf.Exp(-12f * dt));
            cam.fieldOfView = currentFov;
        }
    }
}
