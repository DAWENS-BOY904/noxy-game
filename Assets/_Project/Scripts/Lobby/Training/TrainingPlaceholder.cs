using Noxy.Core;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Noxy.Lobby
{
    /// <summary>
    /// Stand-in for the Training mode. It proves the PLAY -> matchmaking -> scene flow works offline;
    /// there is no gameplay here yet. Replace this scene with the real training range later.
    /// </summary>
    public sealed class TrainingPlaceholder : MonoBehaviour
    {
        public const string LobbySceneName = "MainLobby";

        private Camera cam;
        private Transform character;

        private void Awake()
        {
            AppServices.EnsureInitialized();
            EventSystemFactory.Ensure();
            Screen.orientation = ScreenOrientation.AutoRotation;

            cam = Camera.main;
            if (cam == null)
            {
                var camGo = new GameObject("Main Camera", typeof(Camera), typeof(AudioListener));
                camGo.tag = "MainCamera";
                cam = camGo.GetComponent<Camera>();
            }
            cam.clearFlags = CameraClearFlags.SolidColor;
            cam.backgroundColor = new Color(0.45f, 0.6f, 0.75f);
            cam.fieldOfView = 40f;

            RenderSettings.ambientMode = UnityEngine.Rendering.AmbientMode.Flat;
            RenderSettings.ambientLight = new Color(0.45f, 0.5f, 0.58f);

            var sun = new GameObject("Sun").AddComponent<Light>();
            sun.type = LightType.Directional;
            sun.transform.rotation = Quaternion.Euler(50f, -30f, 0f);
            sun.shadows = LightShadows.Soft;

            MaterialFactory.Primitive(PrimitiveType.Cylinder, "Ground", transform, new Vector3(0f, -0.05f, 0f), new Vector3(60f, 0.05f, 60f),
                MaterialFactory.Lit(new Color(0.33f, 0.4f, 0.3f), 0f, 0.1f));

            var dummyMat = MaterialFactory.Lit(new Color(0.8f, 0.35f, 0.2f), 0f, 0.3f);
            for (int i = 0; i < 5; i++)
            {
                float x = -6f + i * 3f;
                MaterialFactory.Primitive(PrimitiveType.Capsule, "TargetDummy", transform, new Vector3(x, 1f, 9f), new Vector3(0.8f, 1f, 0.8f), dummyMat);
            }

            var view = CharacterViewFactory.Create(transform);
            view.Apply(AppServices.Profile.Profile.loadout, AppServices.Catalog);
            character = view.Root;
            character.rotation = Quaternion.Euler(0f, 0f, 0f);

            BuildUi();
        }

        private void BuildUi()
        {
            LobbyCanvas.Create("TrainingCanvas", 0, out _, out var safe);

            var banner = UIKit.NewRounded(safe, "Banner", UITheme.Panel);
            UIKit.Anchor(banner.rectTransform, new Vector2(0.5f, 1f), new Vector2(0.5f, 1f), new Vector2(0.5f, 1f), new Vector2(0f, -16f), new Vector2(780f, 130f));
            var title = UIKit.NewText(banner.transform, "TRAINING RANGE (PLACEHOLDER)", UITheme.FontTitle, UITheme.Accent, TextAnchor.MiddleCenter, FontStyle.Bold, "Title");
            UIKit.Anchor(title.rectTransform, new Vector2(0f, 0.5f), Vector2.one, new Vector2(0.5f, 1f), Vector2.zero, Vector2.zero);
            title.rectTransform.offsetMin = new Vector2(12f, 0f);
            title.rectTransform.offsetMax = new Vector2(-12f, -8f);
            UIKit.FitText(title, 22);
            var body = UIKit.NewText(banner.transform, "Gameplay isn't implemented yet. This scene only proves the PLAY flow loads a scene.", UITheme.FontCaption, UITheme.TextDim, TextAnchor.MiddleCenter);
            UIKit.Anchor(body.rectTransform, Vector2.zero, new Vector2(1f, 0.5f), new Vector2(0.5f, 0f), Vector2.zero, Vector2.zero);
            body.rectTransform.offsetMin = new Vector2(16f, 8f);
            body.rectTransform.offsetMax = new Vector2(-16f, 0f);
            UIKit.FitText(body, 16);

            var back = UIKit.NewButton(safe, "BackToLobby", "BACK TO LOBBY", UITheme.Accent, ReturnToLobby);
            UIKit.Anchor(back.Background.rectTransform, new Vector2(0.5f, 0f), new Vector2(0.5f, 0f), new Vector2(0.5f, 0f), new Vector2(0f, 24f), new Vector2(380f, 84f));
        }

        private void Update()
        {
            float t = Time.unscaledTime * 0.25f;
            cam.transform.position = new Vector3(Mathf.Sin(t) * 5.5f, 1.8f, Mathf.Cos(t) * 5.5f);
            cam.transform.LookAt(new Vector3(0f, 1f, 0f));

            if (InputCompat.BackPressed) ReturnToLobby();
        }

        private void ReturnToLobby()
        {
            SceneManager.LoadScene(LobbySceneName);
        }
    }
}
