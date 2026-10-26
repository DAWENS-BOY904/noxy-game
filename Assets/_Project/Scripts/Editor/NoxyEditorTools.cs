using System.IO;
using Noxy.Lobby;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace Noxy.EditorTools
{
    /// <summary>Generates the MainLobby and Training scenes and registers them in Build Settings.</summary>
    public static class NoxySceneBuilder
    {
        public const string ScenesFolder = "Assets/_Project/Scenes";
        public const string LobbyScenePath = ScenesFolder + "/MainLobby.unity";
        public const string TrainingScenePath = ScenesFolder + "/Training.unity";

        [MenuItem("Noxy/Build Lobby + Training Scenes")]
        public static void BuildAll()
        {
            if (EditorApplication.isPlayingOrWillEnterPlaymode)
            {
                Debug.LogWarning("[Noxy] Stop Play Mode before building scenes.");
                return;
            }

            NoxyProjectSetup.Apply();
            Directory.CreateDirectory(ScenesFolder);

            BuildScene(LobbyScenePath, go => go.AddComponent<LobbyBootstrap>(), "LobbyBootstrap");
            BuildScene(TrainingScenePath, go => go.AddComponent<TrainingRange>(), "TrainingRange");
            RegisterBuildScenes();

            AssetDatabase.SaveAssets();
            EditorSceneManager.OpenScene(LobbyScenePath);
            Debug.Log("[Noxy] Scenes created: " + LobbyScenePath + " and " + TrainingScenePath + ". Press Play to try the lobby.");
        }

        [MenuItem("Noxy/Export Placeholder Backgrounds")]
        public static void ExportPlaceholderBackgrounds()
        {
            const string dir = "Assets/_Project/Resources/Backgrounds";
            Directory.CreateDirectory(dir);
            foreach (var key in new[] { BackgroundLibrary.Lobby, BackgroundLibrary.Menu, BackgroundLibrary.Auth })
            {
                var path = dir + "/" + key + ".png";
                if (File.Exists(path)) continue; // never overwrite real art
                var tex = BackgroundLibrary.GeneratePlaceholder(key, 1920, 1080);
                File.WriteAllBytes(path, tex.EncodeToPNG());
                Object.DestroyImmediate(tex);
            }
            AssetDatabase.Refresh();
            Debug.Log("Placeholder backgrounds are in " + dir + ". Replace the PNGs with your own art (same names).");
        }

        [MenuItem("Noxy/Open Main Lobby")]
        public static void OpenLobby()
        {
            if (!File.Exists(LobbyScenePath)) BuildAll();
            else EditorSceneManager.OpenScene(LobbyScenePath);
        }

        private static void BuildScene(string path, System.Action<GameObject> addLogic, string logicName)
        {
            var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);

            var camGo = new GameObject("Main Camera", typeof(Camera), typeof(AudioListener));
            camGo.tag = "MainCamera";
            var cam = camGo.GetComponent<Camera>();
            cam.clearFlags = CameraClearFlags.SolidColor;
            cam.backgroundColor = new Color(0.02f, 0.03f, 0.06f);
            cam.fieldOfView = 30f;
            camGo.transform.position = new Vector3(0f, 1.2f, 7f);
            camGo.transform.rotation = Quaternion.Euler(5f, 180f, 0f);

            var logic = new GameObject(logicName);
            addLogic(logic);

            EditorSceneManager.SaveScene(scene, path);
        }

        public static void RegisterBuildScenes()
        {
            EditorBuildSettings.scenes = new[]
            {
                new EditorBuildSettingsScene(LobbyScenePath, true),
                new EditorBuildSettingsScene(TrainingScenePath, true)
            };
        }
    }

    public static class NoxyProjectSetup
    {
        private static readonly string[] AlwaysIncludedShaders =
        {
            "Standard",
            "Unlit/Color",
            "Unlit/Texture",
            "Sprites/Default",
            "Legacy Shaders/Particles/Additive",
            "Mobile/Particles/Additive",
            "Universal Render Pipeline/Lit",
            "Universal Render Pipeline/Unlit"
        };

        [MenuItem("Noxy/Apply Project Settings")]
        public static void Apply()
        {
            PlayerSettings.productName = "Noxy";

            PlayerSettings.defaultInterfaceOrientation = UIOrientation.AutoRotation;
            PlayerSettings.allowedAutorotateToPortrait = true;
            PlayerSettings.allowedAutorotateToPortraitUpsideDown = false;
            PlayerSettings.allowedAutorotateToLandscapeLeft = true;
            PlayerSettings.allowedAutorotateToLandscapeRight = true;

            PlayerSettings.SetScriptingBackend(NamedBuildTarget.Android, ScriptingImplementation.IL2CPP);
            PlayerSettings.Android.targetArchitectures = AndroidArchitecture.ARM64;

            // The dev backend is plain http on a LAN address. Allowed in Development Builds only; use https for release.
            PlayerSettings.insecureHttpOption = InsecureHttpOption.DevelopmentBuildsOnly;

            IncludeShaders();
            EnsureLegacyInputUsable();
            Debug.Log("[Noxy] Project settings applied (orientation, Android IL2CPP/ARM64, runtime shaders).");
        }

        // Runtime-created materials look up shaders by name; without this they would be stripped from device builds.
        private static void IncludeShaders()
        {
            var graphics = AssetDatabase.LoadAssetAtPath<Object>("ProjectSettings/GraphicsSettings.asset");
            if (graphics == null) return;

            var so = new SerializedObject(graphics);
            var list = so.FindProperty("m_AlwaysIncludedShaders");
            if (list == null) return;

            foreach (var name in AlwaysIncludedShaders)
            {
                var shader = Shader.Find(name);
                if (shader == null) continue;

                bool present = false;
                for (int i = 0; i < list.arraySize; i++)
                {
                    if (list.GetArrayElementAtIndex(i).objectReferenceValue == shader) { present = true; break; }
                }
                if (present) continue;

                list.arraySize++;
                list.GetArrayElementAtIndex(list.arraySize - 1).objectReferenceValue = shader;
            }
            so.ApplyModifiedPropertiesWithoutUndo();
        }

        // The lobby reads the legacy Input Manager for keyboard shortcuts; the new-only mode would disable them.
        private static void EnsureLegacyInputUsable()
        {
            var assets = AssetDatabase.LoadAllAssetsAtPath("ProjectSettings/ProjectSettings.asset");
            if (assets == null || assets.Length == 0) return;

            var so = new SerializedObject(assets[0]);
            var handler = so.FindProperty("activeInputHandler");
            if (handler != null && handler.intValue == 1)
            {
                handler.intValue = 2; // Both
                so.ApplyModifiedPropertiesWithoutUndo();
                Debug.LogWarning("[Noxy] Active Input Handling set to 'Both'. Restart the Editor once to apply it.");
            }
        }
    }

    /// <summary>Creates the scenes automatically the first time the project is opened.</summary>
    [InitializeOnLoad]
    public static class NoxyFirstOpen
    {
        private const string TriedKey = "Noxy.FirstOpenTried";

        static NoxyFirstOpen()
        {
            EditorApplication.delayCall += Run;
        }

        private static void Run()
        {
            if (EditorApplication.isPlayingOrWillEnterPlaymode || SessionState.GetBool(TriedKey, false)) return;
            SessionState.SetBool(TriedKey, true);

            bool missing = !File.Exists(NoxySceneBuilder.LobbyScenePath) || !File.Exists(NoxySceneBuilder.TrainingScenePath);
            if (!missing) return;

            try
            {
                NoxySceneBuilder.BuildAll();
            }
            catch (System.Exception e)
            {
                Debug.LogError("[Noxy] Automatic scene creation failed. Use the menu Noxy > Build Lobby + Training Scenes. " + e);
            }
        }
    }
}
