using System;
using System.Collections.Generic;

namespace Noxy.Core
{
    // Original, procedurally generated Bermuda-style island. No UnityEngine types: it must run in tests, in the editor, on
    // device and be exported to the match server, and give the same answer everywhere (own RNG and noise, no System.Random).

    public struct MapPoint
    {
        public float X, Z;
        public MapPoint(float x, float z) { X = x; Z = z; }
        public float DistanceTo(MapPoint o) { float dx = X - o.X, dz = Z - o.Z; return (float)Math.Sqrt(dx * dx + dz * dz); }
    }

    public sealed class DetRng
    {
        private uint s;
        public DetRng(uint seed) { s = seed == 0 ? 0x9E3779B9u : seed; Next(); Next(); }
        public uint Next() { s ^= s << 13; s ^= s >> 17; s ^= s << 5; return s; }
        public float Value() => (Next() & 0xFFFFFF) / 16777216f;
        public float Range(float a, float b) => a + (b - a) * Value();
        public int Int(int a, int bExclusive) => a + (int)(Next() % (uint)Math.Max(1, bExclusive - a));
    }

    public static class DetNoise
    {
        private static uint Hash(int x, int y, uint seed)
        {
            unchecked
            {
                uint h = (uint)x * 374761393u + (uint)y * 668265263u + seed * 2246822519u;
                h = (h ^ (h >> 13)) * 1274126177u;
                return h ^ (h >> 16);
            }
        }
        private static float Cell(int x, int y, uint seed) => (Hash(x, y, seed) & 0xFFFF) / 65535f;
        private static float Smooth(float t) => t * t * (3f - 2f * t);

        public static float Value(float x, float y, uint seed)
        {
            int x0 = (int)Math.Floor(x), y0 = (int)Math.Floor(y);
            float tx = Smooth(x - x0), ty = Smooth(y - y0);
            float a = Cell(x0, y0, seed), b = Cell(x0 + 1, y0, seed), c = Cell(x0, y0 + 1, seed), d = Cell(x0 + 1, y0 + 1, seed);
            return a + (b - a) * tx + (c - a) * ty + (a - b - c + d) * tx * ty;
        }

        public static float Fbm(float x, float y, uint seed, int octaves)
        {
            float sum = 0f, amp = 0.5f, norm = 0f, f = 1f;
            for (int i = 0; i < octaves; i++) { sum += Value(x * f, y * f, seed + (uint)i * 101u) * amp; norm += amp; amp *= 0.5f; f *= 2f; }
            return sum / norm;
        }
    }

    public enum PoiKind { Village, Industrial, Landmark, Military, Coastal, Farm }

    public sealed class Poi
    {
        public string Id = "", Name = "", LandmarkSlot = "";
        public PoiKind Kind;
        public float X, Z, Radius, PadHeight;
        public int LootTier = 1, BuildingTarget;
    }

    public sealed class BuildingSpec
    {
        public string Id = "", PoiId = "";
        public float X, Z, Yaw, Width, Depth, BaseY;
        public int Floors = 1;
        public bool HasInterior;
    }

    public sealed class LootSpawn
    {
        public string Id = "", BuildingId = "", Kind = "";
        public float X, Y, Z;
        public int Tier;
    }

    public sealed class LandingZone { public string Id = "", Name = ""; public float X, Z, Radius; }
    public sealed class RoadSpec { public string Id = "", FromPoi = "", ToPoi = ""; public float Width = 5f; public List<MapPoint> Points = new List<MapPoint>(); }
    public sealed class BridgeSpec { public string Id = "", RoadId = ""; public float X, Z, Yaw, Length, Width, DeckY; }

    public sealed class IslandLayout
    {
        public uint Seed;
        public float WorldSize, WaterLevel;
        public int GridN;
        public float[] Heights = new float[0];     // baked terrain (pads, roads and river included), row-major z then x
        public List<Poi> Pois = new List<Poi>();
        public List<BuildingSpec> Buildings = new List<BuildingSpec>();
        public List<LootSpawn> Loot = new List<LootSpawn>();
        public List<LandingZone> LandingZones = new List<LandingZone>();
        public List<RoadSpec> Roads = new List<RoadSpec>();
        public List<BridgeSpec> Bridges = new List<BridgeSpec>();
        public List<MapPoint> River = new List<MapPoint>();
        public float RiverWidth = 12f;

        public float Half => WorldSize * 0.5f;
        public float Cell => WorldSize / (GridN - 1);

        /// <summary>Bilinear sample of the baked terrain. Outside the map it returns deep water.</summary>
        public float HeightAt(float x, float z)
        {
            float gx = (x + Half) / Cell, gz = (z + Half) / Cell;
            if (gx < 0f || gz < 0f || gx > GridN - 1 || gz > GridN - 1) return WaterLevel - 20f;
            int x0 = Math.Min((int)gx, GridN - 2), z0 = Math.Min((int)gz, GridN - 2);
            float tx = gx - x0, tz = gz - z0;
            float a = Heights[z0 * GridN + x0], b = Heights[z0 * GridN + x0 + 1], c = Heights[(z0 + 1) * GridN + x0], d = Heights[(z0 + 1) * GridN + x0 + 1];
            return a + (b - a) * tx + (c - a) * tz + (a - b - c + d) * tx * tz;
        }

        public float SlopeAt(float x, float z)
        {
            const float e = 2f;
            float dx = (HeightAt(x + e, z) - HeightAt(x - e, z)) / (2f * e), dz = (HeightAt(x, z + e) - HeightAt(x, z - e)) / (2f * e);
            return (float)Math.Sqrt(dx * dx + dz * dz);
        }

        public bool IsLand(float x, float z) => HeightAt(x, z) > WaterLevel + 0.6f;
        public bool InBounds(float x, float z) => Math.Abs(x) <= Half && Math.Abs(z) <= Half;

        /// <summary>A parachutist may touch down here: on land, not too steep, not in the river.</summary>
        public bool IsValidLanding(float x, float z) => InBounds(x, z) && HeightAt(x, z) > WaterLevel + 1.0f && SlopeAt(x, z) < 0.6f;

        /// <summary>Closest valid landing point to (x,z), searched in growing rings. Used to rescue a landing that ends in water.</summary>
        public MapPoint NearestValidLanding(float x, float z)
        {
            if (IsValidLanding(x, z)) return new MapPoint(x, z);
            for (float r = 8f; r <= Half; r += 8f)
                for (int i = 0; i < 24; i++)
                {
                    double a = i * Math.PI * 2 / 24;
                    float px = x + (float)Math.Cos(a) * r, pz = z + (float)Math.Sin(a) * r;
                    if (IsValidLanding(px, pz)) return new MapPoint(px, pz);
                }
            return new MapPoint(0f, 0f);
        }

        public Poi FindPoi(string id) { foreach (var p in Pois) if (p.Id == id) return p; return null; }
    }

    public static class IslandGenerator
    {
        public const float WorldSize = 1200f;
        public const int GridN = 513;
        public const uint DefaultSeed = 2025;

        private sealed class PoiDef
        {
            public string Id, Name, Slot; public PoiKind Kind; public float Ax, Az, Radius; public int Buildings, Tier;
            public PoiDef(string id, string name, PoiKind kind, float ax, float az, float radius, int buildings, int tier, string slot = "")
            { Id = id; Name = name; Kind = kind; Ax = ax; Az = az; Radius = radius; Buildings = buildings; Tier = tier; Slot = slot; }
        }

        // Anchors are in normalised map coordinates (-1..1). Names are original; "Mill", "Factory" and "Clock Tower" are
        // generic words and mark the slots where the supplied landmark models can replace the original placeholder.
        private static readonly PoiDef[] Defs =
        {
            new PoiDef("mill",       "Mill",             PoiKind.Landmark,    -0.38f, -0.20f, 70f, 7, 2, "mill"),
            new PoiDef("factory",    "Factory",          PoiKind.Industrial,   0.34f, -0.22f, 95f, 10, 3, "factory"),
            new PoiDef("clock",      "Clock Tower",      PoiKind.Landmark,     0.02f,  0.04f, 80f, 12, 3, "clock_tower"),
            new PoiDef("harbor",     "Salt Harbor",      PoiKind.Coastal,     -0.10f,  0.66f, 85f, 12, 2),
            new PoiDef("cove",       "Palm Cove",        PoiKind.Coastal,     -0.62f,  0.26f, 65f, 8, 1),
            new PoiDef("quarry",     "Red Quarry",       PoiKind.Industrial,   0.58f,  0.16f, 75f, 6, 2),
            new PoiDef("fort",       "Fort Ridge",       PoiKind.Military,    -0.05f, -0.58f, 80f, 8, 3),
            new PoiDef("marsh",      "Marsh Village",    PoiKind.Village,      0.30f,  0.52f, 70f, 9, 1),
            new PoiDef("light",      "Lighthouse Point", PoiKind.Coastal,      0.74f, -0.30f, 55f, 5, 1),
            new PoiDef("airfield",   "Airfield",         PoiKind.Military,    -0.60f, -0.50f, 110f, 9, 3),
            new PoiDef("hollow",     "Pine Hollow",      PoiKind.Village,     -0.34f,  0.40f, 60f, 7, 1),
            new PoiDef("farm",       "Windmill Farm",    PoiKind.Farm,         0.62f, -0.50f, 65f, 6, 1),
            new PoiDef("sandbar",    "Sandbar Camp",     PoiKind.Coastal,     -0.78f,  0.02f, 45f, 4, 1),
            new PoiDef("plateau",    "Central Plateau",  PoiKind.Village,      0.14f,  0.30f, 60f, 6, 2),
        };

        // River control points (normalised): a spring in the western hills draining south to the sea.
        private static readonly float[][] RiverCtrl = { new[] { -0.30f, -0.08f }, new[] { -0.20f, -0.26f }, new[] { -0.04f, -0.36f }, new[] { 0.10f, -0.48f }, new[] { 0.20f, -0.70f }, new[] { 0.26f, -1.05f } };

        public static float BaseHeight(IslandLayout l, float x, float z, bool carveRiver = true)
        {
            float half = l.Half;
            float nx = x / half, nz = z / (half * 0.8f);
            float warp = DetNoise.Fbm(x * 0.0035f + 11f, z * 0.0035f + 7f, l.Seed, 3) - 0.5f;
            float d = (float)Math.Sqrt(nx * nx + nz * nz) + warp * 0.38f;
            float mask = Clamp01(1f - d);
            float elev = mask * mask * (3f - 2f * mask);
            float hills = DetNoise.Fbm(x * 0.0062f, z * 0.0062f, l.Seed + 1u, 4);
            float h = -7f + elev * 28f + hills * 30f * (float)Math.Pow(elev, 1.25);
            // fine rolling detail, scaled down near the shore so beaches stay smooth
            h += (DetNoise.Fbm(x * 0.03f, z * 0.03f, l.Seed + 2u, 2) - 0.5f) * 2.2f * Clamp01(elev * 3f);
            if (carveRiver && l.River.Count > 1)
            {
                float rd = DistanceToPolyline(l.River, x, z);
                float w = l.RiverWidth;
                if (rd < w * 2.2f && elev > 0.05f)
                {
                    float t = 1f - Smoothstep(w * 0.45f, w * 2.2f, rd);
                    h = Lerp(h, Math.Min(h, -1.6f), t);   // bed below sea level: it fills with water
                }
            }
            return h;
        }

        public static IslandLayout Generate(uint seed = DefaultSeed)
        {
            var l = new IslandLayout { Seed = seed, WorldSize = WorldSize, WaterLevel = 0f, GridN = GridN };
            foreach (var c in RiverCtrl) l.River.Add(new MapPoint(c[0] * l.Half * 0.9f, c[1] * l.Half * 0.75f));

            // 1. points of interest, nudged to valid ground
            foreach (var def in Defs)
            {
                float x = def.Ax * l.Half * 0.9f, z = def.Az * l.Half * 0.78f;
                var p = FindGround(l, x, z, def.Radius * 0.6f);
                float padH = Math.Max(4f, BaseHeight(l, p.X, p.Z));
                l.Pois.Add(new Poi { Id = def.Id, Name = def.Name, Kind = def.Kind, LandmarkSlot = def.Slot, X = p.X, Z = p.Z, Radius = def.Radius, PadHeight = padH, LootTier = def.Tier, BuildingTarget = def.Buildings });
            }

            RelaxPois(l);

            // 2. roads: minimum spanning tree plus a few loops, avoiding the sea
            BuildRoads(l);
            // 3. bridges where roads cross the river
            BuildBridges(l);
            // 4. bake final terrain (pads, roads)
            Bake(l);
            // 5. buildings, loot, landing zones
            PlaceBuildings(l);
            PlaceLoot(l);
            PlaceLandingZones(l);
            return l;
        }

        /// <summary>Pushes overlapping points of interest apart (then back onto good ground) so each has room for its buildings.</summary>
        private static void RelaxPois(IslandLayout l)
        {
            for (int iter = 0; iter < 12; iter++)
            {
                bool moved = false;
                for (int i = 0; i < l.Pois.Count; i++)
                    for (int j = i + 1; j < l.Pois.Count; j++)
                    {
                        var a = l.Pois[i]; var b = l.Pois[j];
                        float dx = b.X - a.X, dz = b.Z - a.Z, d = (float)Math.Sqrt(dx * dx + dz * dz);
                        float need = (a.Radius + b.Radius) * 0.85f;
                        if (d >= need) continue;
                        if (d < 0.01f) { dx = 1f; dz = 0f; d = 1f; }
                        float push = (need - d) * 0.5f + 2f, ux = dx / d, uz = dz / d;
                        var pa = FindGround(l, a.X - ux * push, a.Z - uz * push, a.Radius * 0.6f);
                        var pb = FindGround(l, b.X + ux * push, b.Z + uz * push, b.Radius * 0.6f);
                        a.X = pa.X; a.Z = pa.Z; b.X = pb.X; b.Z = pb.Z; moved = true;
                    }
                if (!moved) break;
            }
            foreach (var p in l.Pois) p.PadHeight = Math.Max(4f, BaseHeight(l, p.X, p.Z));
        }

        // ------------------------------------------------------------------ roads and bridges

        private static void BuildRoads(IslandLayout l)
        {
            int n = l.Pois.Count;
            var inTree = new bool[n]; inTree[0] = true;
            var edges = new List<int[]>();
            for (int k = 1; k < n; k++)
            {
                float best = float.MaxValue; int bi = -1, bj = -1;
                for (int i = 0; i < n; i++) if (inTree[i])
                    for (int j = 0; j < n; j++) if (!inTree[j])
                    {
                        float dd = new MapPoint(l.Pois[i].X, l.Pois[i].Z).DistanceTo(new MapPoint(l.Pois[j].X, l.Pois[j].Z));
                        if (dd < best) { best = dd; bi = i; bj = j; }
                    }
                inTree[bj] = true; edges.Add(new[] { bi, bj });
            }
            // loops make the map less linear: connect each POI to its nearest unconnected neighbour within range
            for (int i = 0; i < n; i++)
            {
                float best = 330f; int bj = -1;
                for (int j = 0; j < n; j++)
                {
                    if (i == j || edges.Exists(e => (e[0] == i && e[1] == j) || (e[0] == j && e[1] == i))) continue;
                    float dd = new MapPoint(l.Pois[i].X, l.Pois[i].Z).DistanceTo(new MapPoint(l.Pois[j].X, l.Pois[j].Z));
                    if (dd < best) { best = dd; bj = j; }
                }
                if (bj >= 0 && (i + bj) % 3 == 0) edges.Add(new[] { i, bj });
            }
            // two deliberate north-south trunk roads: they cross the river, which is what makes bridges
            foreach (var pair in new[] { new[] { "clock", "fort" }, new[] { "mill", "clock" } })
            {
                int i = l.Pois.FindIndex(p => p.Id == pair[0]), j = l.Pois.FindIndex(p => p.Id == pair[1]);
                if (i >= 0 && j >= 0 && !edges.Exists(e => (e[0] == i && e[1] == j) || (e[0] == j && e[1] == i))) edges.Add(new[] { i, j });
            }
            int id = 0;
            foreach (var e in edges)
            {
                var a = l.Pois[e[0]]; var b = l.Pois[e[1]];
                var road = new RoadSpec { Id = "R" + (++id).ToString("00"), FromPoi = a.Id, ToPoi = b.Id };
                RoutePolyline(l, road, a, b, (uint)id);
                l.Roads.Add(road);
            }
        }

        private static void RoutePolyline(IslandLayout l, RoadSpec road, Poi a, Poi b, uint salt)
        {
            for (int attempt = 0; attempt < 10; attempt++)
            {
                var rng = new DetRng(l.Seed * 31u + salt * 97u + (uint)attempt * 13u);
                float amp = attempt == 0 ? 0f : 18f + attempt * 6f;
                var pts = new List<MapPoint> { new MapPoint(a.X, a.Z) };
                float dx = b.X - a.X, dz = b.Z - a.Z, len = (float)Math.Sqrt(dx * dx + dz * dz);
                float px = -dz / len, pz = dx / len;
                int seg = Math.Max(4, (int)(len / 40f));
                bool ok = true;
                for (int i = 1; i < seg; i++)
                {
                    float t = i / (float)seg, off = (rng.Value() - 0.5f) * 2f * amp * (float)Math.Sin(t * Math.PI);
                    var p = new MapPoint(a.X + dx * t + px * off, a.Z + dz * t + pz * off);
                    if (BaseHeight(l, p.X, p.Z, carveRiver: false) < 1.5f) { ok = false; break; }
                    pts.Add(p);
                }
                pts.Add(new MapPoint(b.X, b.Z));
                if (ok || attempt == 9) { road.Points = pts; return; }
            }
        }

        private static void BuildBridges(IslandLayout l)
        {
            int id = 0;
            foreach (var road in l.Roads)
                for (int i = 0; i + 1 < road.Points.Count; i++)
                    for (int j = 0; j + 1 < l.River.Count; j++)
                        if (SegmentIntersect(road.Points[i], road.Points[i + 1], l.River[j], l.River[j + 1], out var hit))
                        {
                            float yaw = (float)(Math.Atan2(road.Points[i + 1].X - road.Points[i].X, road.Points[i + 1].Z - road.Points[i].Z) * 180.0 / Math.PI);
                            float len = l.RiverWidth * 2.6f;
                            float bank = Math.Max(2.2f, BaseHeight(l, hit.X, hit.Z, carveRiver: false) + 0.4f);
                            l.Bridges.Add(new BridgeSpec { Id = "B" + (++id).ToString("00"), RoadId = road.Id, X = hit.X, Z = hit.Z, Yaw = yaw, Length = len, Width = 6f, DeckY = bank });
                        }
        }

        // ------------------------------------------------------------------ terrain bake

        private static void Bake(IslandLayout l)
        {
            int n = l.GridN;
            l.Heights = new float[n * n];
            // flat road centre heights are sampled from the unmodified base terrain
            for (int zi = 0; zi < n; zi++)
                for (int xi = 0; xi < n; xi++)
                {
                    float x = -l.Half + xi * l.Cell, z = -l.Half + zi * l.Cell;
                    float h = BaseHeight(l, x, z);
                    if (h > -1f)
                    {
                        foreach (var p in l.Pois)
                        {
                            float dd = (float)Math.Sqrt((x - p.X) * (x - p.X) + (z - p.Z) * (z - p.Z));
                            if (dd < p.Radius * 1.5f) h = Lerp(h, p.PadHeight, 1f - Smoothstep(p.Radius * 0.8f, p.Radius * 1.5f, dd));
                        }
                        foreach (var r in l.Roads)
                        {
                            float rd = DistanceToPolylineWithPoint(r.Points, x, z, out var np);
                            if (rd > r.Width * 3f) continue;
                            if (NearBridge(l, x, z)) continue;
                            float roadH = BaseHeight(l, np.X, np.Z, carveRiver: false);
                            if (roadH < 1.5f) continue;
                            h = Lerp(h, roadH, (1f - Smoothstep(r.Width * 0.6f, r.Width * 3f, rd)) * 0.9f);
                        }
                    }
                    l.Heights[zi * n + xi] = h;
                }
        }

        private static bool NearBridge(IslandLayout l, float x, float z)
        {
            foreach (var b in l.Bridges) { float dx = x - b.X, dz = z - b.Z; if (dx * dx + dz * dz < b.Length * b.Length * 0.3f) return true; }
            return false;
        }

        // ------------------------------------------------------------------ buildings, loot, landing zones

        private static MapPoint FindGround(IslandLayout l, float x, float z, float clearance)
        {
            for (float r = 0f; r < 400f; r += 10f)
                for (int i = 0; i < (r == 0f ? 1 : 16); i++)
                {
                    double a = i * Math.PI * 2 / 16;
                    float px = x + (float)Math.Cos(a) * r, pz = z + (float)Math.Sin(a) * r;
                    float h = BaseHeight(l, px, pz);
                    if (h < 5f || h > 38f) continue;
                    bool flat = true;
                    for (int k = 0; k < 8 && flat; k++)
                    {
                        double b = k * Math.PI / 4;
                        float hh = BaseHeight(l, px + (float)Math.Cos(b) * clearance, pz + (float)Math.Sin(b) * clearance);
                        if (hh < 2f || Math.Abs(hh - h) > clearance * 0.28f) flat = false;
                    }
                    if (flat) return new MapPoint(px, pz);
                }
            return new MapPoint(x, z);
        }

        private static void PlaceBuildings(IslandLayout l)
        {
            int id = 0;
            foreach (var poi in l.Pois)
            {
                var rng = new DetRng(l.Seed * 7u + (uint)poi.Id.GetHashCode32());
                int placed = 0, tries = 0;
                // the landmark itself sits in the middle of its point of interest
                if (poi.LandmarkSlot.Length > 0)
                {
                    l.Buildings.Add(new BuildingSpec { Id = "BL" + (++id).ToString("000"), PoiId = poi.Id, X = poi.X, Z = poi.Z, Yaw = 0f, Width = 18f, Depth = 18f, Floors = poi.LandmarkSlot == "clock_tower" ? 4 : 3, HasInterior = true, BaseY = poi.PadHeight });
                    placed++;
                }
                while (placed < poi.BuildingTarget && tries++ < 400)
                {
                    float ang = rng.Range(0f, 6.2831853f), dist = rng.Range(22f, poi.Radius * 1.05f);
                    float x = poi.X + (float)Math.Cos(ang) * dist, z = poi.Z + (float)Math.Sin(ang) * dist;
                    float w = rng.Range(8f, 15f), d = rng.Range(8f, 14f);
                    float rad = (float)Math.Sqrt(w * w + d * d) * 0.5f + 3f;
                    if (!l.IsLand(x, z) || l.SlopeAt(x, z) > 0.22f) continue;
                    if (NearRoad(l, x, z, rad + 3f) || NearBridge(l, x, z)) continue;
                    bool clash = false;
                    foreach (var o in l.Buildings) { float dx = o.X - x, dz = o.Z - z; float ro = (float)Math.Sqrt(o.Width * o.Width + o.Depth * o.Depth) * 0.5f + 3f; if (dx * dx + dz * dz < (rad + ro) * (rad + ro)) { clash = true; break; } }
                    if (clash) continue;
                    float yaw = (float)Math.Round(rng.Range(0f, 360f) / 15f) * 15f;
                    int floors = poi.Kind == PoiKind.Industrial ? rng.Int(1, 3) : rng.Int(1, 4);
                    l.Buildings.Add(new BuildingSpec { Id = "B" + (++id).ToString("000"), PoiId = poi.Id, X = x, Z = z, Yaw = yaw, Width = w, Depth = d, Floors = floors, HasInterior = w >= 9f && d >= 9f, BaseY = l.HeightAt(x, z) });
                    placed++;
                }
            }
        }

        private static bool NearRoad(IslandLayout l, float x, float z, float clearance)
        {
            foreach (var r in l.Roads) if (DistanceToPolyline(r.Points, x, z) < r.Width * 0.5f + clearance) return true;
            return false;
        }

        private static void PlaceLoot(IslandLayout l)
        {
            int id = 0;
            string[] kinds = { "weapon", "ammo", "medical", "armor", "ammo", "weapon" };
            foreach (var b in l.Buildings)
            {
                var poi = l.FindPoi(b.PoiId);
                var rng = new DetRng(l.Seed * 13u + (uint)b.Id.GetHashCode32());
                if (!b.HasInterior)
                {
                    // a solid block has nowhere to stand inside: leave one crate beside its door (local -Z side) instead
                    double yaw = b.Yaw * Math.PI / 180.0; float c0 = (float)Math.Cos(yaw), s0 = (float)Math.Sin(yaw);
                    float lz0 = -(b.Depth * 0.5f + 1.6f), lx0 = rng.Range(-b.Width * 0.3f, b.Width * 0.3f);
                    float cx = b.X + lx0 * c0 + lz0 * s0, cz = b.Z - lx0 * s0 + lz0 * c0;
                    l.Loot.Add(new LootSpawn { Id = "L" + (++id).ToString("0000"), BuildingId = b.Id, X = cx, Z = cz, Y = l.HeightAt(cx, cz) + 0.2f, Tier = poi.LootTier, Kind = kinds[rng.Int(0, kinds.Length)] });
                    continue;
                }
                for (int f = 0; f < b.Floors; f++)
                {
                    int count = b.HasInterior ? rng.Int(1, 4) : 1;
                    for (int i = 0; i < count; i++)
                    {
                        // inside the footprint, inset from the walls, rotated with the building
                        float lx = rng.Range(-b.Width * 0.5f + 1.2f, b.Width * 0.5f - 1.2f), lz = rng.Range(-b.Depth * 0.5f + 1.2f, b.Depth * 0.5f - 1.2f);
                        double yr = b.Yaw * Math.PI / 180.0; float c = (float)Math.Cos(yr), s = (float)Math.Sin(yr);
                        l.Loot.Add(new LootSpawn { Id = "L" + (++id).ToString("0000"), BuildingId = b.Id, X = b.X + lx * c + lz * s, Z = b.Z - lx * s + lz * c, Y = b.BaseY + f * 3.2f + 0.2f, Tier = Math.Min(3, poi.LootTier + (f > 0 ? 1 : 0)), Kind = kinds[rng.Int(0, kinds.Length)] });
                    }
                }
            }
        }

        private static void PlaceLandingZones(IslandLayout l)
        {
            int id = 0;
            foreach (var p in l.Pois)
                l.LandingZones.Add(new LandingZone { Id = "Z" + (++id).ToString("00"), Name = p.Name, X = p.X, Z = p.Z, Radius = Math.Max(45f, p.Radius * 0.8f) });
            // open fields between towns, so there are quiet landing options
            var rng = new DetRng(l.Seed * 5u);
            int fields = 0;
            for (int tries = 0; tries < 400 && fields < 6; tries++)
            {
                float x = rng.Range(-l.Half * 0.7f, l.Half * 0.7f), z = rng.Range(-l.Half * 0.7f, l.Half * 0.7f);
                if (!l.IsValidLanding(x, z) || l.HeightAt(x, z) < 4f || l.SlopeAt(x, z) > 0.2f) continue;
                bool far = true;
                foreach (var p in l.Pois) if (new MapPoint(x, z).DistanceTo(new MapPoint(p.X, p.Z)) < p.Radius + 70f) { far = false; break; }
                foreach (var o in l.LandingZones) if (new MapPoint(x, z).DistanceTo(new MapPoint(o.X, o.Z)) < 130f) { far = false; break; }
                if (!far) continue;
                l.LandingZones.Add(new LandingZone { Id = "Z" + (++id).ToString("00"), Name = "Open Field " + (++fields), X = x, Z = z, Radius = 50f });
            }
        }

        // ------------------------------------------------------------------ math helpers

        private static float Clamp01(float v) => v < 0f ? 0f : v > 1f ? 1f : v;
        private static float Lerp(float a, float b, float t) => a + (b - a) * t;
        private static float Smoothstep(float e0, float e1, float v) { float t = Clamp01((v - e0) / (e1 - e0)); return t * t * (3f - 2f * t); }

        public static float DistanceToPolyline(IList<MapPoint> pts, float x, float z) => DistanceToPolylineWithPoint(pts, x, z, out _);

        public static float DistanceToPolylineWithPoint(IList<MapPoint> pts, float x, float z, out MapPoint nearest)
        {
            float best = float.MaxValue; nearest = pts.Count > 0 ? pts[0] : default;
            for (int i = 0; i + 1 < pts.Count; i++)
            {
                float ax = pts[i].X, az = pts[i].Z, bx = pts[i + 1].X, bz = pts[i + 1].Z;
                float dx = bx - ax, dz = bz - az, len2 = dx * dx + dz * dz;
                float t = len2 <= 0f ? 0f : Clamp01(((x - ax) * dx + (z - az) * dz) / len2);
                float qx = ax + dx * t, qz = az + dz * t, ex = x - qx, ez = z - qz, d2 = ex * ex + ez * ez;
                if (d2 < best) { best = d2; nearest = new MapPoint(qx, qz); }
            }
            return (float)Math.Sqrt(best);
        }

        public static bool SegmentIntersect(MapPoint a, MapPoint b, MapPoint c, MapPoint d, out MapPoint hit)
        {
            hit = default;
            float r1x = b.X - a.X, r1z = b.Z - a.Z, r2x = d.X - c.X, r2z = d.Z - c.Z;
            float den = r1x * r2z - r1z * r2x;
            if (Math.Abs(den) < 1e-6f) return false;
            float t = ((c.X - a.X) * r2z - (c.Z - a.Z) * r2x) / den, u = ((c.X - a.X) * r1z - (c.Z - a.Z) * r1x) / den;
            if (t < 0f || t > 1f || u < 0f || u > 1f) return false;
            hit = new MapPoint(a.X + r1x * t, a.Z + r1z * t);
            return true;
        }

        private static uint GetHashCode32(this string s)
        {
            unchecked { uint h = 2166136261u; foreach (char c in s) h = (h ^ c) * 16777619u; return h; }
        }
    }

    /// <summary>Checks a generated island for the problems that make a battle-royale map unplayable.</summary>
    public static class IslandValidator
    {
        public static List<string> Validate(IslandLayout l)
        {
            var problems = new List<string>();
            foreach (var p in l.Pois)
            {
                if (!l.IsLand(p.X, p.Z)) problems.Add("POI " + p.Id + " is not on land");
                if (l.SlopeAt(p.X, p.Z) > 0.25f) problems.Add("POI " + p.Id + " sits on a steep slope");
            }
            for (int i = 0; i < l.Pois.Count; i++)
                for (int j = i + 1; j < l.Pois.Count; j++)
                    if (new MapPoint(l.Pois[i].X, l.Pois[i].Z).DistanceTo(new MapPoint(l.Pois[j].X, l.Pois[j].Z)) < (l.Pois[i].Radius + l.Pois[j].Radius) * 0.8f)
                        problems.Add("POIs " + l.Pois[i].Id + " and " + l.Pois[j].Id + " overlap");
            foreach (var p in l.Pois)
            {
                int n = 0; foreach (var b in l.Buildings) if (b.PoiId == p.Id) n++;
                if (n < Math.Max(3, p.BuildingTarget / 2)) problems.Add("POI " + p.Id + " has only " + n + " buildings");
            }
            for (int i = 0; i < l.Buildings.Count; i++)
            {
                var b = l.Buildings[i];
                if (!l.IsLand(b.X, b.Z)) problems.Add("building " + b.Id + " is not on land");
                if (b.BaseY < l.WaterLevel + 0.5f) problems.Add("building " + b.Id + " is below the waterline");
                for (int j = i + 1; j < l.Buildings.Count; j++)
                {
                    var o = l.Buildings[j];
                    float ra = (float)Math.Sqrt(b.Width * b.Width + b.Depth * b.Depth) * 0.5f, rb = (float)Math.Sqrt(o.Width * o.Width + o.Depth * o.Depth) * 0.5f;
                    if (new MapPoint(b.X, b.Z).DistanceTo(new MapPoint(o.X, o.Z)) < (ra + rb) * 0.95f) problems.Add("buildings " + b.Id + " and " + o.Id + " overlap");
                }
            }
            foreach (var s in l.Loot) if (!l.IsLand(s.X, s.Z)) problems.Add("loot " + s.Id + " is not on land");
            foreach (var z in l.LandingZones) if (!l.IsValidLanding(z.X, z.Z)) problems.Add("landing zone " + z.Id + " is not a valid landing spot");
            foreach (var r in l.Roads)
                foreach (var pt in r.Points)
                    if (!l.IsLand(pt.X, pt.Z) && !IsOnBridge(l, pt.X, pt.Z)) { problems.Add("road " + r.Id + " enters the water at " + (int)pt.X + "," + (int)pt.Z); break; }
            // every POI must be reachable by road
            var reach = new HashSet<string> { l.Pois[0].Id };
            bool grew = true;
            while (grew) { grew = false; foreach (var r in l.Roads) { bool a = reach.Contains(r.FromPoi), b = reach.Contains(r.ToPoi); if (a != b) { reach.Add(a ? r.ToPoi : r.FromPoi); grew = true; } } }
            foreach (var p in l.Pois) if (!reach.Contains(p.Id)) problems.Add("POI " + p.Id + " is not connected by road");
            return problems;
        }

        private static bool IsOnBridge(IslandLayout l, float x, float z)
        {
            foreach (var b in l.Bridges) { float dx = x - b.X, dz = z - b.Z; if (dx * dx + dz * dz < b.Length * b.Length * 0.35f) return true; }
            return false;
        }
    }

    /// <summary>World to minimap / world-map coordinates, shared by every map UI.</summary>
    public static class MapProjection
    {
        /// <summary>Returns 0..1 coordinates with (0,0) at the south-west corner of the map.</summary>
        public static MapPoint ToUv(float worldSize, float x, float z) => new MapPoint(x / worldSize + 0.5f, z / worldSize + 0.5f);
        public static MapPoint ToWorld(float worldSize, float u, float v) => new MapPoint((u - 0.5f) * worldSize, (v - 0.5f) * worldSize);
    }
}
