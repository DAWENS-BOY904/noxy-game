using UnityEngine;

namespace Noxy.Lobby
{
    public enum CameraDetail
    {
        Full,
        Head,
        Feet
    }

    /// <summary>
    /// Frames the character inside the part of the screen that is not covered by UI. Works for any
    /// aspect ratio and orientation because it solves camera distance/offset from the free screen area.
    /// </summary>
    public sealed class CameraRig : MonoBehaviour
    {
        private const float Pitch = 5f;

        private Camera cam;
        private Vector3 targetPosition;
        private Vector3 velocity;
        private Rect lastFree = new Rect(0, 0, 1, 1);
        private CameraDetail lastDetail = CameraDetail.Full;
        private int lastW, lastH;

        public void Initialize(Camera camera)
        {
            cam = camera;
            cam.clearFlags = CameraClearFlags.SolidColor;
            cam.backgroundColor = new Color(0.02f, 0.03f, 0.06f);
            cam.fieldOfView = 30f;
            cam.nearClipPlane = 0.1f;
            cam.farClipPlane = 120f;
            cam.transform.rotation = Quaternion.Euler(Pitch, 180f, 0f);
            Frame(lastFree, lastDetail, true);
        }

        /// <param name="free">Uncovered screen region, normalized 0-1 (origin bottom-left).</param>
        public void Frame(Rect free, CameraDetail detail, bool instant)
        {
            if (cam == null) return;
            lastFree = free;
            lastDetail = detail;
            lastW = Screen.width;
            lastH = Screen.height;

            float subjectHeight, subjectWidth, centerY;
            switch (detail)
            {
                case CameraDetail.Head: subjectHeight = 0.62f; subjectWidth = 0.62f; centerY = 1.72f; break;
                case CameraDetail.Feet: subjectHeight = 0.55f; subjectWidth = 0.75f; centerY = 0.22f; break;
                default: subjectHeight = 1.95f; subjectWidth = 1.1f; centerY = 0.97f; break;
            }

            float aspect = Mathf.Max(0.2f, (float)Screen.width / Mathf.Max(1, Screen.height));
            float tan = Mathf.Tan(cam.fieldOfView * 0.5f * Mathf.Deg2Rad);
            float freeH = Mathf.Max(0.15f, free.height);
            float freeW = Mathf.Max(0.15f, free.width);

            float visibleHeight = Mathf.Max(
                subjectHeight / (freeH * 0.86f),
                subjectWidth / (freeW * 0.9f) / aspect);
            float distance = visibleHeight * 0.5f / tan;
            float halfH = visibleHeight * 0.5f;
            float halfW = halfH * aspect;

            var rot = Quaternion.Euler(Pitch, 180f, 0f);
            Vector3 lookPoint = new Vector3(0f, centerY, 0f);
            float ndcX = free.center.x * 2f - 1f;
            float ndcY = free.center.y * 2f - 1f;

            // Moving the camera opposite to the wanted shift places the character at the free area's center.
            targetPosition = lookPoint - rot * Vector3.forward * distance
                             - rot * Vector3.right * (ndcX * halfW)
                             - rot * Vector3.up * (ndcY * halfH);

            if (instant)
            {
                cam.transform.position = targetPosition;
                velocity = Vector3.zero;
            }
        }

        private void LateUpdate()
        {
            if (cam == null) return;

            if (lastW != Screen.width || lastH != Screen.height) Frame(lastFree, lastDetail, true);

            float dt = Time.unscaledDeltaTime;
            Vector3 smoothed = Vector3.SmoothDamp(cam.transform.position, targetPosition, ref velocity, 0.28f, Mathf.Infinity, dt);
            float t = Time.unscaledTime;
            Vector3 sway = new Vector3(Mathf.Sin(t * 0.31f) * 0.03f, Mathf.Sin(t * 0.43f) * 0.015f, 0f);
            cam.transform.position = smoothed;
            cam.transform.rotation = Quaternion.Euler(Pitch, 180f, 0f) * Quaternion.Euler(sway.y * 40f, sway.x * 40f, 0f);
        }
    }
}
