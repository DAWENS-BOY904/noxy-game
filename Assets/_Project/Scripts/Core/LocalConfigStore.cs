using System;
using System.IO;
using UnityEngine;

namespace Noxy.Core
{
    /// <summary>
    /// Local persistence for settings, key bindings and HUD layouts (JSON files). Loads instantly and never needs
    /// the network; the account layer syncs these to the backend separately. Pass a null directory for in-memory use.
    /// </summary>
    public sealed class LocalConfigStore
    {
        private readonly string directory;
        private readonly bool isMobile;
        private readonly System.Collections.Generic.Dictionary<string, HudLayout> hudCache = new System.Collections.Generic.Dictionary<string, HudLayout>();

        public LocalConfigStore(string directory, bool isMobile)
        {
            this.directory = directory;
            this.isMobile = isMobile;
            Settings = Load<GameSettings>("settings.json") ?? new GameSettings();
            Settings.Sanitize(isMobile);
            Bindings = Load<KeyBindings>("keybindings.json") ?? KeyBindings.CreateDefault();
            Bindings.Sanitize();
        }

        public GameSettings Settings { get; private set; }
        public KeyBindings Bindings { get; private set; }

        /// <summary>Raised after settings or bindings change (UI edits, reset, or a server pull).</summary>
        public event Action Changed;

        /// <summary>Used to pick a first-run graphics preset.</summary>
        public bool NeedsGraphicsPreset => Settings.graphicsPreset < 0;

        public void SaveSettings()
        {
            Settings.Sanitize(isMobile);
            Write("settings.json", Settings);
            Changed?.Invoke();
        }

        public void SaveBindings()
        {
            Bindings.Sanitize();
            Write("keybindings.json", Bindings);
            Changed?.Invoke();
        }

        public void ResetSettings()
        {
            var keepPreset = Settings.graphicsPreset;
            Settings = new GameSettings { graphicsPreset = keepPreset };
            SaveSettings();
        }

        public void ReplaceSettings(GameSettings settings)
        {
            Settings = settings ?? new GameSettings();
            SaveSettings();
        }

        public HudLayout LoadHud(bool portrait, Vector2 safeSize)
        {
            string key = HudLayout.KeyFor(portrait);
            HudLayout layout;
            if (hudCache.TryGetValue(key, out var cached)) layout = cached.Clone();
            else layout = Load<HudLayout>("hud_" + key + ".json") ?? HudLayout.CreateDefault(portrait);
            layout.key = key;
            layout.Sanitize(safeSize);
            return layout;
        }

        public void SaveHud(HudLayout layout)
        {
            hudCache[layout.key] = layout.Clone();
            Write("hud_" + layout.key + ".json", layout);
            Changed?.Invoke();
        }

        public void ResetHud(bool portrait)
        {
            string key = HudLayout.KeyFor(portrait);
            hudCache.Remove(key);
            string path = PathFor("hud_" + key + ".json");
            try { if (path != null && File.Exists(path)) File.Delete(path); }
            catch (Exception e) { Debug.LogWarning("[Config] Could not reset HUD layout: " + e.Message); }
            Changed?.Invoke();
        }

        private string PathFor(string file) => string.IsNullOrEmpty(directory) ? null : Path.Combine(directory, file);

        private T Load<T>(string file) where T : class
        {
            string path = PathFor(file);
            if (path == null || !File.Exists(path)) return null;
            try { return JsonUtility.FromJson<T>(File.ReadAllText(path)); }
            catch (Exception e)
            {
                Debug.LogWarning($"[Config] {file} unreadable, using defaults: {e.Message}");
                return null;
            }
        }

        private void Write(string file, object data)
        {
            string path = PathFor(file);
            if (path == null) return;
            try
            {
                Directory.CreateDirectory(directory);
                File.WriteAllText(path, JsonUtility.ToJson(data, true));
            }
            catch (Exception e) { Debug.LogWarning($"[Config] Could not save {file}: {e.Message}"); }
        }
    }
}
