using System;
using System.Collections.Generic;
using UnityEngine;

namespace Noxy.Core
{
    public enum GameAction
    {
        MoveForward, MoveBack, MoveLeft, MoveRight,
        Jump, Sprint, Crouch, Fire, Aim, Reload, Interact,
        Slot1, Slot2, Slot3,
        Map, Inventory, Pause
    }

    [Serializable]
    public sealed class KeyBindingEntry
    {
        public string action;
        public string key;
    }

    /// <summary>PC key bindings with conflict detection. Stored as action-name -> KeyCode-name pairs.</summary>
    [Serializable]
    public sealed class KeyBindings
    {
        public List<KeyBindingEntry> entries = new List<KeyBindingEntry>();

        private static readonly Dictionary<GameAction, KeyCode> Defaults = new Dictionary<GameAction, KeyCode>
        {
            [GameAction.MoveForward] = KeyCode.W, [GameAction.MoveBack] = KeyCode.S,
            [GameAction.MoveLeft] = KeyCode.A, [GameAction.MoveRight] = KeyCode.D,
            [GameAction.Jump] = KeyCode.Space, [GameAction.Sprint] = KeyCode.LeftShift, [GameAction.Crouch] = KeyCode.LeftControl,
            [GameAction.Fire] = KeyCode.Mouse0, [GameAction.Aim] = KeyCode.Mouse1,
            [GameAction.Reload] = KeyCode.R, [GameAction.Interact] = KeyCode.E,
            [GameAction.Slot1] = KeyCode.Alpha1, [GameAction.Slot2] = KeyCode.Alpha2, [GameAction.Slot3] = KeyCode.Alpha3,
            [GameAction.Map] = KeyCode.M, [GameAction.Inventory] = KeyCode.Tab, [GameAction.Pause] = KeyCode.Escape,
        };

        public static KeyBindings CreateDefault()
        {
            var b = new KeyBindings();
            b.RestoreDefaults();
            return b;
        }

        public static KeyCode DefaultFor(GameAction action) => Defaults[action];

        public void RestoreDefaults()
        {
            entries.Clear();
            foreach (GameAction a in Enum.GetValues(typeof(GameAction)))
                entries.Add(new KeyBindingEntry { action = a.ToString(), key = Defaults[a].ToString() });
        }

        public KeyCode Get(GameAction action)
        {
            string name = action.ToString();
            foreach (var e in entries)
            {
                if (e.action != name) continue;
                return Enum.TryParse(e.key, out KeyCode k) ? k : Defaults[action];
            }
            return Defaults[action];
        }

        /// <summary>Sets a binding without conflict checking (the editor UI resolves conflicts before saving).</summary>
        public void Set(GameAction action, KeyCode key)
        {
            string name = action.ToString();
            foreach (var e in entries)
            {
                if (e.action != name) continue;
                e.key = key.ToString();
                return;
            }
            entries.Add(new KeyBindingEntry { action = name, key = key.ToString() });
        }

        /// <summary>Actions (other than <paramref name="action"/>) already using <paramref name="key"/>.</summary>
        public List<GameAction> ConflictsFor(GameAction action, KeyCode key)
        {
            var list = new List<GameAction>();
            if (key == KeyCode.None) return list;
            foreach (GameAction other in Enum.GetValues(typeof(GameAction)))
                if (other != action && Get(other) == key) list.Add(other);
            return list;
        }

        public List<KeyValuePair<GameAction, GameAction>> FindAllConflicts()
        {
            var result = new List<KeyValuePair<GameAction, GameAction>>();
            var actions = (GameAction[])Enum.GetValues(typeof(GameAction));
            for (int i = 0; i < actions.Length; i++)
                for (int j = i + 1; j < actions.Length; j++)
                    if (Get(actions[i]) != KeyCode.None && Get(actions[i]) == Get(actions[j]))
                        result.Add(new KeyValuePair<GameAction, GameAction>(actions[i], actions[j]));
            return result;
        }

        /// <summary>Drops unknown actions/keys and fills in missing ones with defaults.</summary>
        public void Sanitize()
        {
            var clean = new List<KeyBindingEntry>();
            foreach (GameAction a in Enum.GetValues(typeof(GameAction)))
                clean.Add(new KeyBindingEntry { action = a.ToString(), key = Get(a).ToString() });
            entries = clean;
        }

        public KeyBindings Clone() => JsonUtility.FromJson<KeyBindings>(JsonUtility.ToJson(this));
    }

    public enum HudElementId
    {
        MoveStick, Look, Fire, Aim, Jump, Sprint, Crouch, Reload, Interact,
        Slot1, Slot2, Slot3, Grenade, Map, Inventory, Minimap
    }

    [Serializable]
    public sealed class HudElementState
    {
        public string id;
        public float x;        // center, 0-1 across the safe area
        public float y;        // center, 0-1 up the safe area
        public float scale = 1f;
        public float opacity = 0.85f;

        public HudElementState Clone() => new HudElementState { id = id, x = x, y = y, scale = scale, opacity = opacity };
    }

    public static class HudElementInfo
    {
        public const float MinScale = 0.5f, MaxScale = 2f, MinOpacity = 0.2f, MaxOpacity = 1f;

        /// <summary>Server-side/API identifier (snake_case).</summary>
        public static string ApiId(HudElementId id)
        {
            string s = id.ToString();
            var sb = new System.Text.StringBuilder();
            for (int i = 0; i < s.Length; i++)
            {
                char c = s[i];
                if (char.IsUpper(c) && i > 0 && !char.IsDigit(s[i - 1])) sb.Append('_');
                sb.Append(char.ToLowerInvariant(c));
            }
            return sb.ToString();
        }

        public static string Label(HudElementId id)
        {
            switch (id)
            {
                case HudElementId.MoveStick: return "MOVE";
                case HudElementId.Look: return "LOOK";
                case HudElementId.Slot1: return "1";
                case HudElementId.Slot2: return "2";
                case HudElementId.Slot3: return "3";
                case HudElementId.Minimap: return "RADAR";
                default: return id.ToString().ToUpperInvariant();
            }
        }

        /// <summary>Base size of the control in canvas units at scale 1.</summary>
        public static Vector2 BaseSize(HudElementId id)
        {
            switch (id)
            {
                case HudElementId.MoveStick: return new Vector2(260f, 260f);
                case HudElementId.Look: return new Vector2(520f, 420f);
                case HudElementId.Fire: return new Vector2(170f, 170f);
                case HudElementId.Minimap: return new Vector2(260f, 260f);
                case HudElementId.Slot1:
                case HudElementId.Slot2:
                case HudElementId.Slot3: return new Vector2(120f, 120f);
                default: return new Vector2(120f, 120f);
            }
        }

        /// <summary>Whether the player can move/resize it in the HUD editor.</summary>
        public static bool IsMovable(HudElementId id) => id != HudElementId.Look;

        /// <summary>
        /// Controls that are wired to real gameplay in the current build. Others (grenade, interact, inventory) stay in
        /// the saved layout for the future but are not shown, so the editor never offers dead buttons.
        /// </summary>
        public static bool IsSupported(HudElementId id)
        {
            switch (id)
            {
                case HudElementId.Grenade:
                case HudElementId.Interact:
                case HudElementId.Inventory: return false;
                default: return true;
            }
        }
    }

    /// <summary>Positions, sizes and opacity of the on-screen controls for one orientation.</summary>
    [Serializable]
    public sealed class HudLayout
    {
        public string key = "landscape";
        public List<HudElementState> elements = new List<HudElementState>();

        public static string KeyFor(bool portrait) => portrait ? "portrait" : "landscape";

        public static HudLayout CreateDefault(bool portrait)
        {
            var l = new HudLayout { key = KeyFor(portrait) };
            void Add(HudElementId id, float x, float y, float scale = 1f, float opacity = 0.85f) =>
                l.elements.Add(new HudElementState { id = id.ToString(), x = x, y = y, scale = scale, opacity = opacity });

            if (!portrait)
            {
                Add(HudElementId.MoveStick, 0.14f, 0.28f);
                Add(HudElementId.Look, 0.62f, 0.5f, 1f, 0f);
                Add(HudElementId.Fire, 0.88f, 0.34f, 1f);
                Add(HudElementId.Aim, 0.76f, 0.22f);
                Add(HudElementId.Jump, 0.93f, 0.6f);
                Add(HudElementId.Sprint, 0.24f, 0.58f, 0.8f);
                Add(HudElementId.Crouch, 0.82f, 0.56f, 0.9f);
                Add(HudElementId.Reload, 0.70f, 0.40f, 0.9f);
                Add(HudElementId.Interact, 0.55f, 0.30f, 0.9f);
                Add(HudElementId.Slot1, 0.42f, 0.08f, 0.8f);
                Add(HudElementId.Slot2, 0.50f, 0.08f, 0.8f);
                Add(HudElementId.Slot3, 0.58f, 0.08f, 0.8f);
                Add(HudElementId.Grenade, 0.66f, 0.08f, 0.8f);
                Add(HudElementId.Map, 0.30f, 0.86f, 0.7f);
                Add(HudElementId.Inventory, 0.36f, 0.86f, 0.7f);
                Add(HudElementId.Minimap, 0.12f, 0.72f, 0.8f, 1f);
            }
            else
            {
                Add(HudElementId.MoveStick, 0.24f, 0.17f);
                Add(HudElementId.Look, 0.6f, 0.5f, 1f, 0f);
                Add(HudElementId.Fire, 0.80f, 0.22f);
                Add(HudElementId.Aim, 0.82f, 0.36f);
                Add(HudElementId.Jump, 0.90f, 0.50f);
                Add(HudElementId.Sprint, 0.40f, 0.30f, 0.8f);
                Add(HudElementId.Crouch, 0.66f, 0.12f, 0.9f);
                Add(HudElementId.Reload, 0.64f, 0.30f, 0.9f);
                Add(HudElementId.Interact, 0.50f, 0.40f, 0.9f);
                Add(HudElementId.Slot1, 0.34f, 0.04f, 0.8f);
                Add(HudElementId.Slot2, 0.46f, 0.04f, 0.8f);
                Add(HudElementId.Slot3, 0.58f, 0.04f, 0.8f);
                Add(HudElementId.Grenade, 0.70f, 0.04f, 0.8f);
                Add(HudElementId.Map, 0.80f, 0.89f, 0.7f);
                Add(HudElementId.Inventory, 0.90f, 0.89f, 0.7f);
                Add(HudElementId.Minimap, 0.20f, 0.84f, 0.8f, 1f);
            }
            return l;
        }

        public HudElementState Get(HudElementId id)
        {
            string name = id.ToString();
            foreach (var e in elements) if (e.id == name) return e;
            return null;
        }

        /// <summary>
        /// Clamps values and keeps each control fully inside the safe area. <paramref name="safeSize"/> is the
        /// safe-area size in canvas units, used to convert control size into a normalized margin.
        /// </summary>
        public void Sanitize(Vector2 safeSize)
        {
            var defaults = CreateDefault(key == "portrait");
            var clean = new List<HudElementState>();
            foreach (HudElementId id in Enum.GetValues(typeof(HudElementId)))
            {
                var e = Get(id) ?? defaults.Get(id);
                var s = e.Clone();
                s.id = id.ToString();
                s.scale = Mathf.Clamp(float.IsNaN(s.scale) ? 1f : s.scale, HudElementInfo.MinScale, HudElementInfo.MaxScale);
                s.opacity = Mathf.Clamp(float.IsNaN(s.opacity) ? 0.85f : s.opacity, HudElementInfo.MinOpacity, HudElementInfo.MaxOpacity);
                if (id == HudElementId.Look) s.opacity = 0f;
                ClampInside(s, id, safeSize);
                clean.Add(s);
            }
            elements = clean;
        }

        public static void ClampInside(HudElementState s, HudElementId id, Vector2 safeSize)
        {
            if (safeSize.x < 1f || safeSize.y < 1f) { s.x = Mathf.Clamp01(s.x); s.y = Mathf.Clamp01(s.y); return; }
            Vector2 size = HudElementInfo.BaseSize(id);
            float halfW = Mathf.Min(0.5f, size.x * s.scale * 0.5f / safeSize.x);
            float halfH = Mathf.Min(0.5f, size.y * s.scale * 0.5f / safeSize.y);
            s.x = Mathf.Clamp(float.IsNaN(s.x) ? 0.5f : s.x, halfW, 1f - halfW);
            s.y = Mathf.Clamp(float.IsNaN(s.y) ? 0.5f : s.y, halfH, 1f - halfH);
        }

        /// <summary>Applies a saved layout received from the backend ({ elements: { api_id: {x,y,scale,opacity} } }).</summary>
        public void ApplyApiBody(Dictionary<string, object> root)
        {
            if (!root.TryGetValue("elements", out var el) || !(el is Dictionary<string, object> map)) return;
            foreach (HudElementId id in Enum.GetValues(typeof(HudElementId)))
            {
                if (!map.TryGetValue(HudElementInfo.ApiId(id), out var v) || !(v is Dictionary<string, object> e)) continue;
                var state = Get(id);
                if (state == null) { state = new HudElementState { id = id.ToString() }; elements.Add(state); }
                state.x = (float)MiniJson.Number(e, "x", state.x);
                state.y = (float)MiniJson.Number(e, "y", state.y);
                state.scale = (float)MiniJson.Number(e, "scale", state.scale);
                state.opacity = (float)MiniJson.Number(e, "opacity", state.opacity);
            }
        }

        public HudLayout Clone()
        {
            var l = new HudLayout { key = key };
            foreach (var e in elements) l.elements.Add(e.Clone());
            return l;
        }

        /// <summary>Shape accepted by the backend: { elements: { id: {x,y,scale,opacity} } }.</summary>
        public Dictionary<string, object> ToApiBody()
        {
            var map = new Dictionary<string, object>();
            foreach (HudElementId id in Enum.GetValues(typeof(HudElementId)))
            {
                var e = Get(id);
                if (e == null) continue;
                map[HudElementInfo.ApiId(id)] = new Dictionary<string, object>
                {
                    ["x"] = e.x, ["y"] = e.y, ["scale"] = e.scale, ["opacity"] = Mathf.Max(e.opacity, HudElementInfo.MinOpacity)
                };
            }
            return new Dictionary<string, object> { ["elements"] = map };
        }
    }
}
