using System;
using System.IO;
using NUnit.Framework;
using Noxy.Core;

namespace Noxy.IntegrationTests
{
    /// <summary>
    /// server/data/maps/bermuda.json is generated from the same C# island the client renders. Regenerate it with
    ///   $env:NOXY_WRITE_MAP = "1"; dotnet test --filter MapExportTests
    /// and commit the result. Without the variable this test fails if the committed file no longer matches the generator.
    /// </summary>
    [TestFixture]
    public class MapExportTests
    {
        private static string MapFile() =>
            Path.GetFullPath(Path.Combine(TestContext.CurrentContext.TestDirectory, "..", "..", "..", "..", "..", "server", "data", "maps", "bermuda.json"));

        [Test]
        public void CommittedServerMapMatchesTheGenerator()
        {
            string json = IslandExport.ToJson(IslandGenerator.Generate());
            string file = MapFile();
            if (Environment.GetEnvironmentVariable("NOXY_WRITE_MAP") == "1")
            {
                Directory.CreateDirectory(Path.GetDirectoryName(file));
                File.WriteAllText(file, json);
                Assert.Pass("map written to " + file);
            }
            Assert.IsTrue(File.Exists(file), "server/data/maps/bermuda.json is missing. Run with NOXY_WRITE_MAP=1.");
            Assert.AreEqual(json, File.ReadAllText(file), "server map is stale: regenerate with NOXY_WRITE_MAP=1 and commit it");
        }

        [Test]
        public void ServerTerrainAgreesWithTheClientTerrainClosely()
        {
            var layout = IslandGenerator.Generate();
            Assert.IsTrue(MiniJson.TryParse(File.ReadAllText(MapFile()), out var root));
            var map = MiniJson.AsObject(root);
            int n = (int)MiniJson.Number(map, "grid"); float size = (float)MiniJson.Number(map, "worldSize"), half = size / 2f, cell = size / (n - 1);
            var raw = (System.Collections.Generic.List<object>)map["heightsDecimeters"];
            float Server(float x, float z)
            {
                float gx = (x + half) / cell, gz = (z + half) / cell;
                int x0 = Math.Min((int)gx, n - 2), z0 = Math.Min((int)gz, n - 2); float tx = gx - x0, tz = gz - z0;
                float H(int xi, int zi) => (float)Convert.ToDouble(raw[zi * n + xi]) / 10f;
                float a = H(x0, z0), b = H(x0 + 1, z0), c = H(x0, z0 + 1), d = H(x0 + 1, z0 + 1);
                return a + (b - a) * tx + (c - a) * tz + (a - b - c + d) * tx * tz;
            }
            var rng = new DetRng(99); var diffs = new System.Collections.Generic.List<float>();
            for (int i = 0; i < 20000; i++)
            {
                float x = rng.Range(-half + 5, half - 5), z = rng.Range(-half + 5, half - 5);
                if (layout.HeightAt(x, z) < 0.5f) continue;                        // only land matters for landing
                diffs.Add(Math.Abs(Server(x, z) - layout.HeightAt(x, z)));
            }
            diffs.Sort();
            float p99 = diffs[(int)(diffs.Count * 0.99f)], max = diffs[diffs.Count - 1];
            TestContext.Progress.WriteLine("server vs client terrain on land: n=" + diffs.Count + " median=" + diffs[diffs.Count / 2].ToString("0.00") + " p99=" + p99.ToString("0.00") + " max=" + max.ToString("0.00") + " m");
            Assert.Less(p99, 1.5f, "the server's coarse terrain must stay within the landing tolerance of what the player sees");
        }
    }
}
