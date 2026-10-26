using System;
using Noxy.Core;
using UnityEngine;

namespace Noxy.Lobby
{
    public enum AirMode { None, Cabin, Freefall, Chute, Landing }

    /// <summary>
    /// Drives the local player while they are not on the ground: walking inside the cabin, jumping, freefall, the parachute and
    /// the landing. It only PROPOSES positions; the match server validates every one and decides when the player has jumped,
    /// opened the parachute and landed. A rejected move triggers <see cref="ResyncRequested"/> and the server's position wins.
    /// </summary>
    public sealed class BrAirController : MonoBehaviour
    {
        public const float MoveInterval = 0.12f;
        private const float CabinWalkSpeed = 3.4f;   // well below the server's cabin limit
        private const float LandingRetrySeconds = 0.2f;

        private Camera cam;
        private PlayerController player;
        private ICharacterView view;
        private BrPlaneRig plane;
        private IBattleRoyaleService svc;
        private BrConfigDto cfg;
        private IslandLayout layout;
        private TouchControlState touch;
        private readonly LookPipeline look = new LookPipeline();
        private int defaultCullingMask;

        private AirState air;
        private BrParachuteView chute;
        private float yaw, pitch, lookDistance = 6f, moveTimer, landingTimer;
        private Vector2 cabinPos;
        private bool moveInFlight, jumpPending, deployPending;

        public AirMode Mode { get; private set; } = AirMode.None;
        public bool InputEnabled = true;
        public bool UseMouseLook = true;
        public float Altitude { get; private set; }
        public float FallSpeed => -air.VelY;

        public event Action<Vector3, float, bool> Landed;     // position, yaw, relocated to the nearest land
        public event Action<string> Message;
        public event Action ResyncRequested;
        public event Action<bool> Exited;                     // true when the server forced the exit

        public bool CanDeployNow => Mode == AirMode.Freefall && cfg != null && JumpModel.CanDeploy(Altitude, cfg);
        public bool InAir => Mode == AirMode.Freefall || Mode == AirMode.Chute || Mode == AirMode.Landing;

        public void Init(Camera camera, PlayerController playerController, ICharacterView characterView, BrPlaneRig planeRig,
            IBattleRoyaleService service, BrConfigDto config, IslandLayout island, TouchControlState touchState)
        {
            cam = camera; player = playerController; view = characterView; plane = planeRig;
            svc = service; cfg = config; layout = island; touch = touchState;
            defaultCullingMask = cam.cullingMask;
        }

        public void SetTouch(TouchControlState touchState) { touch = touchState; }

        // ---------------------------------------------------------------- transitions
        public void BeginCabin(float startX, float startZ)
        {
            Mode = AirMode.Cabin;
            player.SetExternallyControlled(true);
            player.transform.SetParent(plane.Cabin.transform, false);
            cabinPos = new Vector2(startX, startZ);
            player.transform.localPosition = new Vector3(cabinPos.x, 0.05f, cabinPos.y);
            view.Root.gameObject.SetActive(false);         // first person inside the cabin
            yaw = 90f; pitch = 5f;
            cam.cullingMask = defaultCullingMask & ~(1 << BrLayers.PlaneExterior);   // no exterior hull in the cabin view
            cam.fieldOfView = 70f;
        }

        public void BeginFreefall(Vector3 position, bool forced)
        {
            if (Mode == AirMode.Freefall || Mode == AirMode.Chute || Mode == AirMode.Landing) return;
            Mode = AirMode.Freefall;
            jumpPending = false;
            player.SetExternallyControlled(true);
            player.transform.SetParent(null, true);
            player.transform.position = position;
            view.Root.gameObject.SetActive(true);
            cam.cullingMask = defaultCullingMask;
            air = new AirState { X = position.x, Y = position.y, Z = position.z, VelY = 0f, Chute = false };
            pitch = 35f; lookDistance = 6.5f;
            moveTimer = 0f;
            Exited?.Invoke(forced);
        }

        private void BeginChute()
        {
            if (Mode != AirMode.Freefall) return;
            Mode = AirMode.Chute;
            air.Chute = true;
            if (chute == null) chute = BrParachuteView.Attach(player.transform);
            chute.gameObject.SetActive(true);
        }

        private void FinishLanding(Vector3 position, bool relocated)
        {
            if (Mode == AirMode.None) return;
            Mode = AirMode.None;
            if (chute != null) chute.gameObject.SetActive(false);
            view.Root.localRotation = Quaternion.identity;
            view.Root.gameObject.SetActive(true);
            player.SetExternallyControlled(false);
            player.Snap(position, yaw);
            cam.fieldOfView = 60f;
            Landed?.Invoke(position, yaw, relocated);
        }

        /// <summary>Applies where the server says the player is. Used after a rejected move and after a reconnect.</summary>
        public void Reconcile(BrYouDto you, bool relocated)
        {
            var pos = new Vector3(you.x, you.y, you.z);
            switch (you.state)
            {
                case "aircraft":
                    if (Mode == AirMode.Cabin) { cabinPos = new Vector2(you.x, you.z); player.transform.localPosition = new Vector3(you.x, 0.05f, you.z); }
                    break;
                case "freefall":
                    if (Mode == AirMode.Cabin || Mode == AirMode.None) BeginFreefall(pos, you.forced);
                    else if (InAir) { air.X = pos.x; air.Y = pos.y; air.Z = pos.z; }
                    break;
                case "parachute":
                    if (Mode == AirMode.Cabin || Mode == AirMode.None) BeginFreefall(pos, you.forced);
                    if (Mode == AirMode.Freefall) BeginChute();
                    if (InAir) { air.X = pos.x; air.Y = pos.y; air.Z = pos.z; }
                    break;
                case "landed":
                    if (Mode != AirMode.None) FinishLanding(pos, relocated);
                    break;
            }
        }

        // ---------------------------------------------------------------- input
        private Vector2 ReadMove()
        {
            var keys = AppServices.Config.Bindings;
            Vector2 m = Vector2.zero;
            if (InputCompat.KeyHeld(keys.Get(GameAction.MoveForward))) m.y += 1f;
            if (InputCompat.KeyHeld(keys.Get(GameAction.MoveBack))) m.y -= 1f;
            if (InputCompat.KeyHeld(keys.Get(GameAction.MoveRight))) m.x += 1f;
            if (InputCompat.KeyHeld(keys.Get(GameAction.MoveLeft))) m.x -= 1f;
            if (touch != null) m += touch.Move;
            return Vector2.ClampMagnitude(m, 1f);
        }

        private void ReadLook(float dt)
        {
            var s = AppServices.Config.Settings;
            Vector2 gyro = s.gyroEnabled ? InputCompat.GyroRate : Vector2.zero;
            Vector2 d = look.Process(UseMouseLook ? InputCompat.MouseDelta : Vector2.zero, touch != null ? touch.ConsumeLook() : Vector2.zero, gyro.x, gyro.y, false, dt, s);
            yaw += d.x;
            pitch = Mathf.Clamp(pitch + d.y, -60f, 80f);
        }

        // ---------------------------------------------------------------- actions
        public void TryJump()
        {
            if (Mode != AirMode.Cabin || jumpPending) return;
            jumpPending = true;
            svc.Jump(r =>
            {
                jumpPending = false;
                if (Mode != AirMode.Cabin) return;
                if (!r.Ok) { Message?.Invoke(r.Friendly); ResyncRequested?.Invoke(); return; }
                BeginFreefall(new Vector3(r.Value.x, r.Value.y, r.Value.z), false);
            });
        }

        public void TryDeploy(bool automatic = false)
        {
            if (Mode != AirMode.Freefall || deployPending) return;
            if (!JumpModel.CanDeploy(Altitude, cfg)) { if (!automatic) Message?.Invoke("Too low to open the parachute"); return; }
            deployPending = true;
            BeginChute();   // presentation only; the server still confirms through the next move/state
            svc.DeployParachute(r =>
            {
                deployPending = false;
                if (!r.Ok) { Message?.Invoke(r.Friendly); ResyncRequested?.Invoke(); }
            });
        }

        // ---------------------------------------------------------------- per frame
        private void Update()
        {
            float dt = Time.deltaTime;
            if (Mode == AirMode.None) return;
            if (InputEnabled) ReadLook(dt);
            if (Mode == AirMode.Cabin) UpdateCabin(dt);
            else UpdateAir(dt);
        }

        private void UpdateCabin(float dt)
        {
            if (!InputEnabled) return;
            Vector2 mv = ReadMove();
            float r = yaw * Mathf.Deg2Rad;
            // cabin-local axes: +X forward, +Z left, so "forward" at yaw 90 maps to +X
            float fx = Mathf.Sin(r), fz = Mathf.Cos(r);
            float rx = Mathf.Cos(r), rz = -Mathf.Sin(r);
            cabinPos += new Vector2(fx * mv.y + rx * mv.x, fz * mv.y + rz * mv.x) * (CabinWalkSpeed * dt);
            cabinPos.x = Mathf.Clamp(cabinPos.x, -cfg.cabin.halfLength + 0.3f, cfg.cabin.halfLength - 0.3f);
            cabinPos.y = Mathf.Clamp(cabinPos.y, -cfg.cabin.halfWidth + 0.3f, cfg.cabin.halfWidth - 0.3f);
            player.transform.localPosition = new Vector3(cabinPos.x, 0.05f, cabinPos.y);

            var keys = AppServices.Config.Bindings;
            if ((InputCompat.KeyDown(keys.Get(GameAction.Jump)) || (touch != null && touch.JumpPressed)) && jumpOpen) TryJump();
            touch?.ClearPresses();

            moveTimer += dt;
            if (moveTimer >= MoveInterval && !moveInFlight) SendMove("cabin", cabinPos.x, 0f, cabinPos.y, yaw);
        }

        private bool jumpOpen;
        /// <summary>The match controller tells the air controller when the server's jump window is open.</summary>
        public void SetJumpOpen(bool open) { jumpOpen = open; }

        private void UpdateAir(float dt)
        {
            Vector2 mv = ReadMove();
            float r = yaw * Mathf.Deg2Rad;
            float sx = Mathf.Sin(r) * mv.y + Mathf.Cos(r) * mv.x;
            float sz = Mathf.Cos(r) * mv.y - Mathf.Sin(r) * mv.x;
            if (Mode == AirMode.Landing) { sx = sz = 0f; }

            var keys = AppServices.Config.Bindings;
            if (InputEnabled && (InputCompat.KeyDown(keys.Get(GameAction.Jump)) || (touch != null && touch.JumpPressed))) TryDeploy();
            touch?.ClearPresses();

            if (Mode != AirMode.Landing)
            {
                air = JumpModel.Step(air, dt, sx, sz, cfg, layout.HeightAt, out bool landed);
                float lim = layout.Half - 1f;
                air.X = Mathf.Clamp(air.X, -lim, lim);
                air.Z = Mathf.Clamp(air.Z, -lim, lim);
                if (landed) { Mode = AirMode.Landing; landingTimer = LandingRetrySeconds; }
            }
            player.transform.position = new Vector3(air.X, air.Y, air.Z);
            float ground = layout.HeightAt(air.X, air.Z);
            Altitude = Mathf.Max(0f, air.Y - ground);

            player.transform.rotation = Quaternion.Euler(0f, yaw, 0f);
            view.Root.localRotation = Quaternion.Euler(Mode == AirMode.Freefall ? 80f : 0f, 0f, 0f);

            // the server opens the parachute by itself near the ground; do it a little earlier on the client so it looks intentional
            if (Mode == AirMode.Freefall && Altitude <= cfg.freefall.autoDeployAltitude + 12f) TryDeploy(true);

            moveTimer += dt;
            if (Mode == AirMode.Landing)
            {
                landingTimer += dt;
                if (landingTimer >= LandingRetrySeconds && !moveInFlight) { landingTimer = 0f; SendMove("landing", air.X, air.Y, air.Z, yaw); }
            }
            else if (moveTimer >= MoveInterval && !moveInFlight) SendMove(Mode == AirMode.Chute ? "parachute" : "freefall", air.X, air.Y, air.Z, yaw);
        }

        private void SendMove(string anim, float x, float y, float z, float yawDeg)
        {
            moveTimer = 0f; moveInFlight = true;
            svc.Move(x, y, z, yawDeg, anim, r =>
            {
                moveInFlight = false;
                if (Mode == AirMode.None) return;
                if (!r.Ok)
                {
                    if (r.Code == "match_over") return;
                    if (!r.NetworkError) ResyncRequested?.Invoke();   // the server rejected the move: it knows where we should be
                    return;
                }
                var v = r.Value;
                if (v.state == "parachute" && Mode == AirMode.Freefall) BeginChute();
                if (v.state == "landed" && Mode != AirMode.Cabin) FinishLanding(new Vector3(v.x, v.y, v.z), v.relocated);
            });
        }

        private void LateUpdate()
        {
            if (Mode == AirMode.None || cam == null) return;
            if (Mode == AirMode.Cabin)
            {
                var cabin = plane.Cabin.transform;
                cam.transform.position = cabin.TransformPoint(new Vector3(cabinPos.x, 1.65f, cabinPos.y));
                cam.transform.rotation = cabin.rotation * Quaternion.Euler(pitch, yaw, 0f);
                return;
            }
            float dt = Time.unscaledDeltaTime;
            Vector3 pivot = player.transform.position + Vector3.up * 1f;
            var rot = Quaternion.Euler(pitch, yaw, 0f);
            float target = Mode == AirMode.Freefall ? 6.5f : 5.5f;
            lookDistance = Mathf.Lerp(lookDistance, target, 1f - Mathf.Exp(-6f * dt));
            Vector3 desired = pivot - rot * Vector3.forward * lookDistance;
            if (Physics.Linecast(pivot, desired, out var hit, ~0, QueryTriggerInteraction.Ignore) && hit.collider.GetComponentInParent<PlayerController>() == null)
                desired = hit.point + hit.normal * 0.3f;
            cam.transform.position = desired;
            cam.transform.rotation = rot;
            float targetFov = Mode == AirMode.Freefall ? 78f : 62f;
            cam.fieldOfView = Mathf.Lerp(cam.fieldOfView, targetFov, 1f - Mathf.Exp(-5f * dt));
        }

        public float Yaw => yaw;
    }
}
