using System;
using System.Collections.Generic;
using System.Linq;

namespace Noxy.Core
{
    /// <summary>Pure helpers for building and saving looks. Only owned items in the matching slot are ever produced.</summary>
    public static class LoadoutTools
    {
        public const int PresetCount = 3;
        private static readonly CosmeticSlot[] Slots = (CosmeticSlot[])Enum.GetValues(typeof(CosmeticSlot));

        public static Loadout Randomize(ICosmeticCatalog catalog, ISet<string> owned, Random rng)
        {
            var result = new Loadout();
            foreach (var slot in Slots)
            {
                var choices = new List<string>();
                foreach (var item in catalog.Items)
                    if (item.Slot == slot && owned.Contains(item.Id)) choices.Add(item.Id);
                if (choices.Count == 0) continue;
                // Optional slots may stay empty about a third of the time.
                if (slot.IsOptional() && rng.Next(3) == 0) continue;
                result.Set(slot, choices[rng.Next(choices.Count)]);
            }
            return result;
        }

        /// <summary>Drops anything unowned, unknown or in the wrong slot (e.g. a preset saved before an item was removed).</summary>
        public static Loadout Sanitize(Loadout l, ICosmeticCatalog catalog, ISet<string> owned)
        {
            var result = new Loadout();
            foreach (var slot in Slots)
            {
                var id = l.Get(slot);
                if (!string.IsNullOrEmpty(id) && catalog.TryGet(id, out var item) && item.Slot == slot && owned.Contains(id))
                    result.Set(slot, id);
            }
            return result;
        }

        public static string Encode(Loadout l)
        {
            var parts = new string[Slots.Length];
            for (int i = 0; i < Slots.Length; i++) parts[i] = l.Get(Slots[i]);
            return string.Join("|", parts);
        }

        public static Loadout Decode(string text)
        {
            var l = new Loadout();
            if (string.IsNullOrEmpty(text)) return l;
            var parts = text.Split('|');
            for (int i = 0; i < Slots.Length && i < parts.Length; i++) l.Set(Slots[i], parts[i]);
            return l;
        }
    }

    /// <summary>Three named look slots. Stored as plain text by the caller (PlayerPrefs, file, or backend later).</summary>
    public sealed class LoadoutPresets
    {
        private readonly string[] slots = new string[LoadoutTools.PresetCount];

        public bool Has(int index) => InRange(index) && !string.IsNullOrEmpty(slots[index]);

        public void Save(int index, Loadout l)
        {
            if (InRange(index)) slots[index] = LoadoutTools.Encode(l);
        }

        public Loadout Load(int index, ICosmeticCatalog catalog, ISet<string> owned) =>
            Has(index) ? LoadoutTools.Sanitize(LoadoutTools.Decode(slots[index]), catalog, owned) : null;

        public string Serialize() => string.Join("\n", slots.Select(s => s ?? ""));

        public static LoadoutPresets Deserialize(string text)
        {
            var p = new LoadoutPresets();
            if (string.IsNullOrEmpty(text)) return p;
            var lines = text.Split('\n');
            for (int i = 0; i < lines.Length && i < p.slots.Length; i++) p.slots[i] = lines[i];
            return p;
        }

        private static bool InRange(int i) => i >= 0 && i < LoadoutTools.PresetCount;
    }
}
