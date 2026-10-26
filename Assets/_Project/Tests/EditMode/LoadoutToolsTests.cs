using System;
using System.Collections.Generic;
using NUnit.Framework;
using Noxy.Core;

namespace Noxy.Tests
{
    public class LoadoutToolsTests
    {
        private static readonly DefaultCosmeticCatalog Catalog = new DefaultCosmeticCatalog();
        private static HashSet<string> Owned() => new HashSet<string>(DefaultCosmeticCatalog.StartingOwnedIds);

        [Test] public void Randomize_OnlyOwnedItemsInMatchingSlots()
        {
            var owned = Owned();
            for (int seed = 0; seed < 100; seed++)
            {
                var l = LoadoutTools.Randomize(Catalog, owned, new Random(seed));
                foreach (CosmeticSlot s in Enum.GetValues(typeof(CosmeticSlot)))
                {
                    var id = l.Get(s);
                    if (id == "") { Assert.IsTrue(s.IsOptional(), "required slot empty: " + s); continue; }
                    Assert.IsTrue(owned.Contains(id));
                    Catalog.TryGet(id, out var item);
                    Assert.AreEqual(s, item.Slot);
                }
            }
        }

        [Test] public void Randomize_IsDeterministicPerSeed()
        {
            Assert.IsTrue(LoadoutTools.Randomize(Catalog, Owned(), new Random(5)).SameAs(LoadoutTools.Randomize(Catalog, Owned(), new Random(5))));
        }

        [Test] public void EncodeDecode_RoundTrips()
        {
            var l = DefaultCosmeticCatalog.StartingLoadout();
            Assert.IsTrue(l.SameAs(LoadoutTools.Decode(LoadoutTools.Encode(l))));
        }

        [Test] public void Sanitize_DropsUnownedAndWrongSlot()
        {
            var l = new Loadout { outfit = "outfit_aurora", shoes = "outfit_urban", mask = "mask_bandana", headgear = "nope" };
            var s = LoadoutTools.Sanitize(l, Catalog, Owned());
            Assert.AreEqual("", s.outfit);
            Assert.AreEqual("", s.shoes);
            Assert.AreEqual("mask_bandana", s.mask);
            Assert.AreEqual("", s.headgear);
        }

        [Test] public void Presets_SaveLoadSerialize()
        {
            var p = new LoadoutPresets();
            Assert.IsFalse(p.Has(1));
            p.Save(1, DefaultCosmeticCatalog.StartingLoadout());
            var p2 = LoadoutPresets.Deserialize(p.Serialize());
            Assert.IsTrue(p2.Has(1));
            Assert.IsFalse(p2.Has(0));
            Assert.IsTrue(p2.Load(1, Catalog, Owned()).SameAs(DefaultCosmeticCatalog.StartingLoadout()));
            Assert.IsNull(p2.Load(0, Catalog, Owned()));
        }

        [Test] public void Presets_OutOfRangeIsSafe()
        {
            var p = new LoadoutPresets();
            p.Save(-1, new Loadout()); p.Save(9, new Loadout());
            Assert.IsFalse(p.Has(-1)); Assert.IsFalse(p.Has(9));
            Assert.IsNull(p.Load(9, Catalog, Owned()));
        }

        [Test] public void Deserialize_GarbageDoesNotThrow()
        {
            Assert.DoesNotThrow(() => LoadoutPresets.Deserialize("a|b\n\n\n\n\nzzz"));
            Assert.IsFalse(LoadoutPresets.Deserialize(null).Has(0));
        }
    }
}
