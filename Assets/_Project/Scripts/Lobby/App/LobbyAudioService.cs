using UnityEngine;
using UnityEngine.SceneManagement;

namespace Noxy.Lobby
{
    /// <summary>Single owner for lobby music transitions and UI feedback sounds.</summary>
    public sealed class LobbyAudioService : MonoBehaviour
    {
        public const string MusicResource = "Audio/LobbyMusic";
        private static LobbyAudioService instance;
        private AudioSource musicSource;
        private AudioSource sfxSource;
        private AudioClip clickClip;
        private AudioClip selectClip;
        private float targetMusicVolume;
        private float sceneFade = 1f;
        private Noxy.Core.GameSettings settings;
        private bool warnedMissingMusic;

        public static LobbyAudioService Ensure()
        {
            if (instance != null) return instance;
            var go = new GameObject("LobbyAudioService");
            DontDestroyOnLoad(go);
            instance = go.AddComponent<LobbyAudioService>();
            return instance;
        }

        private void Awake()
        {
            if (instance != null && instance != this)
            {
                Destroy(gameObject);
                return;
            }

            instance = this;
            musicSource = gameObject.AddComponent<AudioSource>();
            musicSource.loop = true;
            musicSource.playOnAwake = false;
            musicSource.spatialBlend = 0f;

            sfxSource = gameObject.AddComponent<AudioSource>();
            sfxSource.playOnAwake = false;
            sfxSource.spatialBlend = 0f;
            clickClip = CreateTone("Noxy UI Click", 720f, 0.045f);
            selectClip = CreateTone("Noxy Mode Select", 980f, 0.08f);

            SceneManager.sceneLoaded += OnSceneLoaded;
            OnSceneLoaded(SceneManager.GetActiveScene(), LoadSceneMode.Single);
        }

        private void OnDestroy()
        {
            SceneManager.sceneLoaded -= OnSceneLoaded;
            if (instance == this) instance = null;
            if (clickClip != null) Destroy(clickClip);
            if (selectClip != null) Destroy(selectClip);
        }

        public void ApplySettings(Noxy.Core.GameSettings value)
        {
            settings = value;
            RefreshMusicVolume();
        }

        public static void PlayUiClick()
        {
            if (instance != null) instance.PlaySfx(instance.clickClip);
        }

        public static void PlayModeSelect()
        {
            if (instance != null) instance.PlaySfx(instance.selectClip);
        }

        private void PlaySfx(AudioClip clip)
        {
            if (clip == null || settings == null || settings.sfxMuted) return;
            sfxSource.PlayOneShot(clip, settings.masterVolume * settings.sfxVolume);
        }

        private void OnSceneLoaded(Scene scene, LoadSceneMode mode)
        {
            sceneFade = scene.name == "MainLobby" ? 1f : 0f;
            if (scene.name == "MainLobby" && musicSource.clip == null)
            {
                musicSource.clip = Resources.Load<AudioClip>(MusicResource);
                if (musicSource.clip == null && !warnedMissingMusic)
                {
                    warnedMissingMusic = true;
                    Debug.LogWarning("[Audio] Lobby music asset is missing. Add an original or properly licensed clip at Resources/Audio/LobbyMusic.");
                }
                else if (musicSource.clip != null)
                {
                    musicSource.Play();
                }
            }
            RefreshMusicVolume();
        }

        private void RefreshMusicVolume()
        {
            targetMusicVolume = settings == null || settings.musicMuted || musicSource.clip == null
                ? 0f
                : settings.masterVolume * settings.musicVolume * sceneFade;
        }

        private void Update()
        {
            if (musicSource == null) return;
            float desiredSceneFade = SceneManager.GetActiveScene().name == "MainLobby" ? 1f : 0f;
            sceneFade = Mathf.MoveTowards(sceneFade, desiredSceneFade, Time.unscaledDeltaTime * 1.25f);
            RefreshMusicVolume();
            musicSource.volume = Mathf.MoveTowards(musicSource.volume, targetMusicVolume, Time.unscaledDeltaTime * 0.8f);
            if (musicSource.volume <= 0.001f && musicSource.isPlaying && desiredSceneFade == 0f)
                musicSource.Pause();
            else if (desiredSceneFade > 0f && musicSource.clip != null && !musicSource.isPlaying)
                musicSource.UnPause();
        }

        private static AudioClip CreateTone(string clipName, float frequency, float duration)
        {
            const int sampleRate = 22050;
            int sampleCount = Mathf.CeilToInt(sampleRate * duration);
            var samples = new float[sampleCount];
            for (int i = 0; i < sampleCount; i++)
            {
                float t = i / (float)sampleRate;
                float envelope = Mathf.Min(1f, i / (sampleRate * 0.004f)) * Mathf.Clamp01((sampleCount - i) / (sampleRate * 0.012f));
                samples[i] = Mathf.Sin(2f * Mathf.PI * frequency * t) * envelope * 0.18f;
            }

            var clip = AudioClip.Create(clipName, sampleCount, 1, sampleRate, false);
            clip.SetData(samples, 0);
            return clip;
        }
    }
}
