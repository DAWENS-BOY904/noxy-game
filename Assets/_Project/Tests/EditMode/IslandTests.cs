using System;
using System.Diagnostics;
using System.Linq;
using NUnit.Framework;
using Noxy.Core;

namespace Noxy.Tests
{
    public class IslandTests
    {
        private static IslandLayout layout;
        private static long generateMs;

        [OneTimeSetUp]
        public void Build()
        {
            var sw = Stopwatch.StartNew();
            layout = IslandGenerator.Generate();
            generateMs = sw.ElapsedMilliseconds;
            TestContext.Progress.WriteLine("island generation (513x513 bake): " + generateMs + " ms on this machine");
        }

        [Test]
        public void GenerationIsDeterministicAndSeedDependent()
        {
            var a = IslandGenerator.Generate(7); var b = IslandGenerator.Generate(7); var c = IslandGenerator.Generate(8);
            Assert.AreEqual(a.Pois.Count, b.Pois.Count);
            Assert.AreEqual(a.Buildings.Count, b.Buildings.Count);
            Assert.AreEqual(a.Loot.Count, b.Loot.Count);
            CollectionAssert.AreEqual(a.Heights, b.Heights);
            CollectionAssert.AreNotEqual(a.Heights, c.Heights);
        }

        [Test]
        public void TheDefaultIslandPassesEveryValidationRule()
        {
            var problems = IslandValidator.Validate(layout);
            Assert.IsEmpty(problems, string.Join("\n", problems.Take(20)));
        }

        [Test]
        public void ItIsAnIslandWithOceanBeachesHillsAndEnoughContent()
        {
            int water = 0, land = 0, high = 0, n = 0; float max = float.MinValue;
            for (float x = -layout.Half; x <= layout.Half; x += 20f)
                for (float z = -layout.Half; z <= layout.Half; z += 20f)
                {
                    n++; float h = layout.HeightAt(x, z);
                    if (h < 0f) water++; else land++;
                    if (h > 18f) high++;
                    max = Math.Max(max, h);
                }
            Assert.Greater(water / (float)n, 0.30f, "ocean around the island");
            Assert.Greater(land / (float)n, 0.25f, "enough land to play on");
            Assert.Greater(high, 0, "hills exist");
            Assert.Greater(max, 20f);
            // the map edge is open sea
            foreach (var p in new[] { new[] { -layout.Half + 5, 0f }, new[] { layout.Half - 5, 0f }, new[] { 0f, layout.Half - 5 }, new[] { 0f, -layout.Half + 5 } })
                Assert.Less(layout.HeightAt(p[0], p[1]), 0f);
            Assert.GreaterOrEqual(layout.Pois.Count, 12);
            Assert.GreaterOrEqual(layout.Buildings.Count, 90);
            Assert.GreaterOrEqual(layout.Loot.Count, 150);
            Assert.GreaterOrEqual(layout.LandingZones.Count, layout.Pois.Count);
            Assert.GreaterOrEqual(layout.Roads.Count, layout.Pois.Count - 1);
            Assert.GreaterOrEqual(layout.Bridges.Count, 1, "roads cross the river on bridges");
        }

        [Test]
        public void TheThreeRequestedLandmarkSlotsExistAndAreSpreadOut()
        {
            foreach (var slot in new[] { "mill", "factory", "clock_tower" })
                Assert.IsNotNull(layout.Pois.Find(p => p.LandmarkSlot == slot), slot);
            var mill = layout.FindPoi("mill"); var fac = layout.FindPoi("factory"); var clock = layout.FindPoi("clock");
            Assert.Greater(new MapPoint(mill.X, mill.Z).DistanceTo(new MapPoint(fac.X, fac.Z)), 200f);
            Assert.Greater(new MapPoint(mill.X, mill.Z).DistanceTo(new MapPoint(clock.X, clock.Z)), 150f);
        }

        [Test]
        public void LootIsInsideItsBuildingAndAtTheRightHeight()
        {
            foreach (var s in layout.Loot.Take(400))
            {
                var b = layout.Buildings.Find(x => x.Id == s.BuildingId);
                Assert.IsNotNull(b);
                Assert.Less(new MapPoint(s.X, s.Z).DistanceTo(new MapPoint(b.X, b.Z)), Math.Sqrt(b.Width * b.Width + b.Depth * b.Depth) * 0.5 + 0.1);
                Assert.GreaterOrEqual(s.Y, b.BaseY);
                Assert.LessOrEqual(s.Y, b.BaseY + b.Floors * 3.2f + 0.5f);
            }
            Assert.AreEqual(layout.Loot.Count, layout.Loot.Select(s => s.Id).Distinct().Count(), "ids are unique");
        }

        [Test]
        public void LandingRulesRejectWaterAndRescueToLand()
        {
            Assert.IsFalse(layout.IsValidLanding(layout.Half - 5, 0f), "open sea");
            Assert.IsFalse(layout.IsValidLanding(layout.Half + 100f, 0f), "outside the map");
            var rescued = layout.NearestValidLanding(layout.Half - 5, 0f);
            Assert.IsTrue(layout.IsValidLanding(rescued.X, rescued.Z));
            foreach (var z in layout.LandingZones) Assert.IsTrue(layout.IsValidLanding(z.X, z.Z), z.Name);
        }

        [Test]
        public void BuildingsAreNotFloatingOrSunken()
        {
            foreach (var b in layout.Buildings)
            {
                float ground = layout.HeightAt(b.X, b.Z);
                Assert.That(Math.Abs(ground - b.BaseY), Is.LessThan(3.5f), b.Id + " base " + b.BaseY + " vs ground " + ground);
            }
        }

        [Test]
        public void MapProjectionRoundTrips()
        {
            var uv = MapProjection.ToUv(1200f, 300f, -150f);
            Assert.AreEqual(0.75f, uv.X, 1e-5f); Assert.AreEqual(0.375f, uv.Z, 1e-5f);
            var w = MapProjection.ToWorld(1200f, uv.X, uv.Z);
            Assert.AreEqual(300f, w.X, 1e-3f); Assert.AreEqual(-150f, w.Z, 1e-3f);
        }

        [Test]
        public void LandmarkAssetPolicyQuarantinesUnconfirmedModels()
        {
            var mill = LandmarkManifest.Find("mill");
            Assert.IsFalse(mill.RightsConfirmed, "no supplied model is enabled by default");
            Assert.IsFalse(LandmarkAssetPolicy.CanUse(mill, p => true, out var why));
            StringAssert.Contains("Rights not confirmed", why);
            Assert.IsFalse(LandmarkAssetPolicy.CanUse(mill, p => false, out why));
            StringAssert.Contains("File not found", why);
            var confirmed = new LandmarkAsset { Id = "x", ActualPath = "p", Author = "a", SourceUrl = "u", License = "CC-BY-4.0", RightsConfirmed = true };
            Assert.IsTrue(LandmarkAssetPolicy.CanUse(confirmed, p => true, out _));
            StringAssert.Contains("CC-BY-4.0", LandmarkAssetPolicy.Credit(confirmed));
            // the requested clock tower path does not exist: the manifest records the real one and flags the mismatch
            var clock = LandmarkManifest.Find("clock_tower");
            Assert.IsTrue(LandmarkAssetPolicy.PathMismatch(clock));
            StringAssert.EndsWith("clock_tower_free_fire_model.glb", clock.ActualPath);
        }
    }
}
