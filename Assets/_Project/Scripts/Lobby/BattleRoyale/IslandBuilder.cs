using System;
using System.Collections;
using System.Collections.Generic;
using Noxy.Core;
using UnityEngine;
using UnityEngine.Rendering;

namespace Noxy.Lobby
{
    /// <summary>Colours the whole island into one texture. It is the terrain albedo AND the image behind the minimap / world map.</summary>
    public static class IslandTextures
    {
        public const int Size = 1024;

        public static IEnumerator BuildAsync(IslandLayout l, Action<Texture2D> done, Action<float> progress)
        {
            var tex = new Texture2D(Size, Size, TextureFormat.RGBA32, true, false) { wrapMode = TextureWrapMode.Clamp, filterMode = FilterMode.Bilinear, name = "IslandColor" };
            var pixels = new Color32[Size * Size];
            float world = l.WorldSize, step = world / Size;
            for (int pz = 0; pz < Size; pz++)
            {
                float z = -l.Half + (pz + 0.5f) * step;
                for (int px = 0; px < Size; px++)
                {
                    float x = -l.Half + (px + 0.5f) * step;
                    pixels[pz * Size + px] = Shade(l, x, z);
                }
                if ((pz & 31) == 31) { progress?.Invoke((pz + 1f) / Size * 0.8f); yield return null; }
            }
            foreach (var r in l.Roads) PaintPolyline(pixels, l, r.Points, r.Width, new Color32(70, 70, 76, 255));
            foreach (var b in l.Bridges) PaintDisc(pixels, l, b.X, b.Z, b.Width * 0.5f, new Color32(120, 92, 60, 255));
            tex.SetPixels32(pixels);
            tex.Apply(true);
            progress?.Invoke(1f);
            done(tex);
        }

        private static Color32 Shade(IslandLayout l, float x, float z)
        {
            float h = l.HeightAt(x, z);
            if (h < -3f) return new Color32(14, 50, 88, 255);
            if (h < 0.4f) return Color32.Lerp(new Color32(20, 70, 110, 255), new Color32(40, 120, 140, 255), Mathf.Clamp01((h + 3f) / 3.4f));
            float n = DetNoise.Value(x * 0.04f, z * 0.04f, 7u);
            if (h < 2.4f) return Color32.Lerp(new Color32(222, 205, 150, 255), new Color32(205, 190, 135, 255), n);
            float slope = l.SlopeAt(x, z);
            if (slope > 0.55f || h > 40f) return Color32.Lerp(new Color32(112, 108, 100, 255), new Color32(86, 83, 78, 255), n);
            float t = Mathf.Clamp01(h / 40f);
            var a = Color32.Lerp(new Color32(86, 140, 62, 255), new Color32(60, 108, 48, 255), t);
            return Color32.Lerp(a, new Color32(108, 150, 70, 255), n * 0.35f);
        }

        private static void PaintPolyline(Color32[] px, IslandLayout l, List<MapPoint> pts, float width, Color32 c)
        {
            float step = l.WorldSize / Size;
            for (int i = 0; i + 1 < pts.Count; i++)
            {
                float len = pts[i].DistanceTo(pts[i + 1]); int n = Mathf.Max(2, (int)(len / (step * 0.5f)));
                for (int k = 0; k <= n; k++)
                {
                    float t = k / (float)n;
                    PaintDisc(px, l, pts[i].X + (pts[i + 1].X - pts[i].X) * t, pts[i].Z + (pts[i + 1].Z - pts[i].Z) * t, width * 0.5f, c);
                }
            }
        }

        private static void PaintDisc(Color32[] px, IslandLayout l, float x, float z, float radius, Color32 c)
        {
            float step = l.WorldSize / Size;
            int cx = (int)((x + l.Half) / step), cz = (int)((z + l.Half) / step), r = Mathf.Max(1, Mathf.CeilToInt(radius / step));
            for (int dz = -r; dz <= r; dz++)
                for (int dx = -r; dx <= r; dx++)
                {
                    if (dx * dx + dz * dz > r * r) continue;
                    int ix = cx + dx, iz = cz + dz;
                    if (ix < 0 || iz < 0 || ix >= Size || iz >= Size) continue;
                    px[iz * Size + ix] = c;
                }
        }
    }

    /// <summary>Tiny procedural meshes with palette-based colours (UVs point into a small colour strip), so one Standard material colours everything.</summary>
    internal sealed class PaletteMeshBuilder
    {
        public const int PaletteSlots = 8;
        public static readonly Color[] Palette = { new Color(0.40f, 0.28f, 0.18f), new Color(0.16f, 0.36f, 0.20f), new Color(0.22f, 0.46f, 0.22f), new Color(0.46f, 0.45f, 0.42f), new Color(0.70f, 0.66f, 0.55f), new Color(0.3f, 0.3f, 0.32f), Color.white, Color.white };

        private readonly List<Vector3> verts = new List<Vector3>();
        private readonly List<Vector2> uvs = new List<Vector2>();
        private readonly List<int> tris = new List<int>();

        public static Texture2D MakePaletteTexture()
        {
            var t = new Texture2D(PaletteSlots, 1, TextureFormat.RGBA32, false) { filterMode = FilterMode.Point, wrapMode = TextureWrapMode.Clamp, name = "Palette" };
            for (int i = 0; i < PaletteSlots; i++) t.SetPixel(i, 0, Palette[i]);
            t.Apply(false);
            return t;
        }

        private int Vertex(Vector3 p, int slot) { verts.Add(p); uvs.Add(new Vector2((slot + 0.5f) / PaletteSlots, 0.5f)); return verts.Count - 1; }

        public void Prism(Vector3 baseCenter, float radius, float height, int sides, int slot)
        {
            int start = verts.Count;
            for (int i = 0; i < sides; i++)
            {
                float a = i * Mathf.PI * 2f / sides;
                var o = new Vector3(Mathf.Cos(a) * radius, 0f, Mathf.Sin(a) * radius);
                Vertex(baseCenter + o, slot); Vertex(baseCenter + o + Vector3.up * height, slot);
            }
            for (int i = 0; i < sides; i++)
            {
                int a = start + i * 2, b = start + ((i + 1) % sides) * 2;
                tris.Add(a); tris.Add(a + 1); tris.Add(b + 1);
                tris.Add(a); tris.Add(b + 1); tris.Add(b);
            }
        }

        public void Cone(Vector3 baseCenter, float radius, float height, int sides, int slot)
        {
            int apex = Vertex(baseCenter + Vector3.up * height, slot), start = verts.Count;
            for (int i = 0; i < sides; i++)
            {
                float a = i * Mathf.PI * 2f / sides;
                Vertex(baseCenter + new Vector3(Mathf.Cos(a) * radius, 0f, Mathf.Sin(a) * radius), slot);
            }
            for (int i = 0; i < sides; i++) { tris.Add(apex); tris.Add(start + (i + 1) % sides); tris.Add(start + i); }
        }

        public void Blob(Vector3 center, Vector3 scale, int slot, uint seed)
        {
            // a lumpy octahedron: cheap rock
            var rng = new DetRng(seed);
            Vector3[] dirs = { Vector3.up, Vector3.down, Vector3.left, Vector3.right, Vector3.forward, Vector3.back };
            int[] idx = new int[6];
            for (int i = 0; i < 6; i++) idx[i] = Vertex(center + Vector3.Scale(dirs[i], scale) * rng.Range(0.8f, 1.15f), slot);
            int[][] faces = { new[] { 0, 3, 4 }, new[] { 0, 4, 2 }, new[] { 0, 2, 5 }, new[] { 0, 5, 3 }, new[] { 1, 4, 3 }, new[] { 1, 2, 4 }, new[] { 1, 5, 2 }, new[] { 1, 3, 5 } };
            foreach (var f in faces) { tris.Add(idx[f[0]]); tris.Add(idx[f[1]]); tris.Add(idx[f[2]]); }
        }

        public Mesh ToMesh(string name)
        {
            var m = new Mesh { name = name, indexFormat = IndexFormat.UInt32 };
            m.SetVertices(verts); m.SetUVs(0, uvs); m.SetTriangles(tris, 0);
            m.RecalculateNormals(); m.RecalculateBounds();
            return m;
        }
    }

    /// <summary>
    /// Builds the playable island from an IslandLayout: terrain chunks with colliders, water, roads (in the texture), bridges,
    /// hollow buildings with doorways and ramps, landmark placeholders, cover and vegetation. It yields between steps so the
    /// loading screen can report real progress. All art is original procedural placeholder geometry.
    /// </summary>
    public sealed class IslandBuilder
    {
        public const int ChunkCells = 128;
        public const float FloorHeight = 3.2f;

        public Texture2D ColorTexture { get; private set; }
        public Transform Root { get; private set; }
        public float VegetationDensity = 1f;
        public bool CastTreeShadows;

        private Material terrainMat, wallMat, roofMat, coverMat, woodMat, vegMat, deckMat, metalMat;

        private static Material Lit(Color c) => MaterialFactory.Lit(c, 0f, 0.15f);

        public IEnumerator Build(IslandLayout l, Transform parent, Action<float> progress)
        {
            Root = new GameObject("Island").transform;
            Root.SetParent(parent, false);

            // 1. colour texture (also the world map image)
            Texture2D tex = null;
            yield return IslandTextures.BuildAsync(l, t => tex = t, p => progress?.Invoke(p * 0.30f));
            ColorTexture = tex;
            terrainMat = Lit(Color.white); terrainMat.mainTexture = tex;
            wallMat = Lit(new Color(0.78f, 0.74f, 0.66f)); roofMat = Lit(new Color(0.55f, 0.28f, 0.22f)); coverMat = Lit(new Color(0.46f, 0.44f, 0.40f));
            woodMat = Lit(new Color(0.40f, 0.28f, 0.18f)); deckMat = Lit(new Color(0.42f, 0.34f, 0.26f)); metalMat = Lit(new Color(0.5f, 0.52f, 0.56f));
            vegMat = Lit(Color.white); vegMat.mainTexture = PaletteMeshBuilder.MakePaletteTexture();

            // 2. terrain chunks
            int chunks = (l.GridN - 1) / ChunkCells, total = chunks * chunks, done = 0;
            for (int cz = 0; cz < chunks; cz++)
                for (int cx = 0; cx < chunks; cx++)
                {
                    BuildTerrainChunk(l, cx, cz);
                    progress?.Invoke(0.30f + 0.30f * (++done / (float)total));
                    yield return null;
                }
            BuildWater(l);

            // 3. buildings, landmarks, bridges, cover
            var buildings = new GameObject("Buildings").transform; buildings.SetParent(Root, false);
            for (int i = 0; i < l.Buildings.Count; i++)
            {
                BuildBuilding(l, l.Buildings[i], buildings);
                if (i % 8 == 7) { progress?.Invoke(0.60f + 0.20f * i / l.Buildings.Count); yield return null; }
            }
            foreach (var b in l.Bridges) BuildBridge(b);
            BuildCover(l);
            StaticBatchingUtility.Combine(buildings.gameObject);

            // 4. vegetation, one combined mesh per chunk
            var veg = new GameObject("Vegetation").transform; veg.SetParent(Root, false);
            done = 0;
            for (int cz = 0; cz < chunks; cz++)
                for (int cx = 0; cx < chunks; cx++)
                {
                    BuildVegetationChunk(l, cx, cz, veg);
                    progress?.Invoke(0.80f + 0.20f * (++done / (float)total));
                    yield return null;
                }
            progress?.Invoke(1f);
        }

        // ------------------------------------------------------------------ terrain and water
        private void BuildTerrainChunk(IslandLayout l, int cx, int cz)
        {
            int vps = ChunkCells + 1, n = l.GridN;
            var verts = new Vector3[vps * vps]; var normals = new Vector3[vps * vps]; var uvs = new Vector2[vps * vps];
            float cell = l.Cell, half = l.Half;
            Func<int, int, float> H = (gx, gz) => l.Heights[Mathf.Clamp(gz, 0, n - 1) * n + Mathf.Clamp(gx, 0, n - 1)];
            for (int z = 0; z < vps; z++)
                for (int x = 0; x < vps; x++)
                {
                    int gx = cx * ChunkCells + x, gz = cz * ChunkCells + z, i = z * vps + x;
                    verts[i] = new Vector3(-half + gx * cell, H(gx, gz), -half + gz * cell);
                    normals[i] = new Vector3(H(gx - 1, gz) - H(gx + 1, gz), 2f * cell, H(gx, gz - 1) - H(gx, gz + 1)).normalized;
                    uvs[i] = new Vector2(gx / (float)(n - 1), gz / (float)(n - 1));
                }
            var tris = new int[ChunkCells * ChunkCells * 6]; int t = 0;
            for (int z = 0; z < ChunkCells; z++)
                for (int x = 0; x < ChunkCells; x++)
                {
                    int i = z * vps + x;
                    tris[t++] = i; tris[t++] = i + vps; tris[t++] = i + 1;
                    tris[t++] = i + 1; tris[t++] = i + vps; tris[t++] = i + vps + 1;
                }
            var mesh = new Mesh { name = "TerrainChunk_" + cx + "_" + cz, vertices = verts, normals = normals, uv = uvs, triangles = tris };
            mesh.RecalculateBounds();
            var go = new GameObject("Terrain_" + cx + "_" + cz, typeof(MeshFilter), typeof(MeshRenderer), typeof(MeshCollider));
            go.transform.SetParent(Root, false);
            go.GetComponent<MeshFilter>().sharedMesh = mesh;
            var r = go.GetComponent<MeshRenderer>(); r.sharedMaterial = terrainMat; r.shadowCastingMode = ShadowCastingMode.Off; r.receiveShadows = true;
            go.GetComponent<MeshCollider>().sharedMesh = mesh;
            go.isStatic = true;
        }

        private void BuildWater(IslandLayout l)
        {
            var water = GameObject.CreatePrimitive(PrimitiveType.Plane);
            water.name = "Ocean";
            water.transform.SetParent(Root, false);
            water.transform.position = new Vector3(0f, l.WaterLevel - 0.25f, 0f);
            water.transform.localScale = new Vector3(l.WorldSize * 3f / 10f, 1f, l.WorldSize * 3f / 10f);
            water.GetComponent<Renderer>().sharedMaterial = MaterialFactory.Unlit(new Color(0.10f, 0.42f, 0.58f));
            water.GetComponent<Renderer>().shadowCastingMode = ShadowCastingMode.Off;
            // swimming is not implemented: the collider keeps stray characters from sinking, it is not a gameplay surface
        }

        // ------------------------------------------------------------------ buildings
        private GameObject Box(Transform parent, string name, Vector3 localPos, Vector3 size, Material m)
        {
            var go = GameObject.CreatePrimitive(PrimitiveType.Cube);
            go.name = name; go.transform.SetParent(parent, false);
            go.transform.localPosition = localPos; go.transform.localScale = size;
            go.GetComponent<Renderer>().sharedMaterial = m;
            go.isStatic = true;
            return go;
        }

        private void BuildBuilding(IslandLayout l, BuildingSpec b, Transform parent)
        {
            var poi = l.FindPoi(b.PoiId);
            var root = new GameObject(b.Id).transform;
            root.SetParent(parent, false);
            root.SetPositionAndRotation(new Vector3(b.X, b.BaseY, b.Z), Quaternion.Euler(0f, b.Yaw, 0f));
            float w = b.Width, d = b.Depth, t = 0.3f;

            if (!b.HasInterior)
            {
                // too small to walk into: a solid block with a roof (loot is placed beside the door)
                Box(root, "Block", new Vector3(0f, FloorHeight * 0.5f, 0f), new Vector3(w, FloorHeight, d), wallMat);
                Box(root, "Roof", new Vector3(0f, FloorHeight + 0.15f, 0f), new Vector3(w + 0.6f, 0.3f, d + 0.6f), roofMat);
                return;
            }

            for (int f = 0; f < b.Floors; f++)
            {
                float y0 = f * FloorHeight;
                // floor slab; upper floors leave a hole for the ramp that leads up to them
                if (f == 0) Box(root, "Floor0", new Vector3(0f, 0.1f, 0f), new Vector3(w, 0.2f, d), woodMat);
                else SlabWithHole(root, y0, w, d, w * 0.5f - 2.3f, w * 0.5f - 0.5f, -d * 0.5f + 0.8f, -d * 0.5f + 7.2f);
                // walls: back, left, right full; front has a doorway on the ground floor
                Box(root, "WallBack" + f, new Vector3(0f, y0 + FloorHeight * 0.5f, d * 0.5f - t * 0.5f), new Vector3(w, FloorHeight, t), wallMat);
                Box(root, "WallL" + f, new Vector3(-w * 0.5f + t * 0.5f, y0 + FloorHeight * 0.5f, 0f), new Vector3(t, FloorHeight, d), wallMat);
                Box(root, "WallR" + f, new Vector3(w * 0.5f - t * 0.5f, y0 + FloorHeight * 0.5f, 0f), new Vector3(t, FloorHeight, d), wallMat);
                if (f == 0)
                {
                    const float door = 1.9f; float seg = (w - door) * 0.5f;
                    Box(root, "FrontL", new Vector3(-(door * 0.5f + seg * 0.5f), FloorHeight * 0.5f, -d * 0.5f + t * 0.5f), new Vector3(seg, FloorHeight, t), wallMat);
                    Box(root, "FrontR", new Vector3(door * 0.5f + seg * 0.5f, FloorHeight * 0.5f, -d * 0.5f + t * 0.5f), new Vector3(seg, FloorHeight, t), wallMat);
                    Box(root, "DoorHeader", new Vector3(0f, 2.6f + (FloorHeight - 2.6f) * 0.5f, -d * 0.5f + t * 0.5f), new Vector3(door, FloorHeight - 2.6f, t), wallMat);
                }
                else Box(root, "WallFront" + f, new Vector3(0f, y0 + FloorHeight * 0.5f, -d * 0.5f + t * 0.5f), new Vector3(w, FloorHeight, t), wallMat);
                // window slits are left out: upper walls are solid so the placeholder stays cheap

                if (f < b.Floors - 1) RampUp(root, y0, w, d);
            }
            float top = b.Floors * FloorHeight;
            Box(root, "Roof", new Vector3(0f, top + 0.15f, 0f), new Vector3(w + 0.6f, 0.3f, d + 0.6f), roofMat);
            if (poi != null && poi.LandmarkSlot.Length > 0 && Mathf.Abs(b.X - poi.X) < 0.01f && Mathf.Abs(b.Z - poi.Z) < 0.01f) BuildLandmarkDecor(root, poi, top, w, d);
        }

        private void SlabWithHole(Transform root, float y0, float w, float d, float hx0, float hx1, float hz0, float hz1)
        {
            float y = y0 + 0.1f, th = 0.2f;
            Box(root, "SlabA", new Vector3(0f, y, (hz1 + d * 0.5f) * 0.5f), new Vector3(w, th, d * 0.5f - hz1), woodMat);               // beyond the hole
            Box(root, "SlabB", new Vector3(0f, y, (-d * 0.5f + hz0) * 0.5f), new Vector3(w, th, hz0 + d * 0.5f), woodMat);               // before the hole
            Box(root, "SlabC", new Vector3((-w * 0.5f + hx0) * 0.5f, y, (hz0 + hz1) * 0.5f), new Vector3(hx0 + w * 0.5f, th, hz1 - hz0), woodMat);
            Box(root, "SlabD", new Vector3((hx1 + w * 0.5f) * 0.5f, y, (hz0 + hz1) * 0.5f), new Vector3(w * 0.5f - hx1, th, hz1 - hz0), woodMat);
        }

        private void RampUp(Transform root, float y0, float w, float d)
        {
            float x = w * 0.5f - 1.4f, z0 = -d * 0.5f + 1.0f, run = 6f, rise = FloorHeight;
            float len = Mathf.Sqrt(run * run + rise * rise), angle = Mathf.Atan2(rise, run) * Mathf.Rad2Deg;
            var ramp = Box(root, "Ramp", new Vector3(x, y0 + rise * 0.5f + 0.1f, z0 + run * 0.5f), new Vector3(1.8f, 0.2f, len), woodMat);
            ramp.transform.localRotation = Quaternion.Euler(-angle, 0f, 0f);
        }

        // ------------------------------------------------------------------ landmark placeholders (original, generic shapes)
        private void BuildLandmarkDecor(Transform root, Poi poi, float top, float w, float d)
        {
            switch (poi.LandmarkSlot)
            {
                case "mill":
                {
                    var tower = GameObject.CreatePrimitive(PrimitiveType.Cylinder); tower.name = "MillTower"; tower.transform.SetParent(root, false);
                    tower.transform.localPosition = new Vector3(0f, top + 6f, 0f); tower.transform.localScale = new Vector3(7f, 6f, 7f); tower.GetComponent<Renderer>().sharedMaterial = wallMat;
                    var hub = new GameObject("Vanes").transform; hub.SetParent(root, false); hub.localPosition = new Vector3(0f, top + 9f, -4.2f);
                    for (int i = 0; i < 4; i++) { var vane = Box(hub, "Vane" + i, new Vector3(0f, 0f, 0f), new Vector3(1.2f, 14f, 0.3f), woodMat); vane.transform.localRotation = Quaternion.Euler(0f, 0f, i * 45f); }
                    break;
                }
                case "factory":
                {
                    Box(root, "Hall", new Vector3(0f, top + 2f, 0f), new Vector3(w * 1.6f, 4f, d * 0.8f), metalMat);
                    for (int i = 0; i < 2; i++) { var s = GameObject.CreatePrimitive(PrimitiveType.Cylinder); s.name = "Chimney" + i; s.transform.SetParent(root, false); s.transform.localPosition = new Vector3(-6f + i * 12f, top + 11f, d * 0.2f); s.transform.localScale = new Vector3(2.4f, 11f, 2.4f); s.GetComponent<Renderer>().sharedMaterial = metalMat; }
                    break;
                }
                default:   // clock tower
                {
                    Box(root, "Tower", new Vector3(0f, top + 12f, 0f), new Vector3(7f, 24f, 7f), wallMat);
                    var face = GameObject.CreatePrimitive(PrimitiveType.Cylinder); face.name = "ClockFace"; face.transform.SetParent(root, false);
                    face.transform.localPosition = new Vector3(0f, top + 20f, -3.7f); face.transform.localRotation = Quaternion.Euler(90f, 0f, 0f); face.transform.localScale = new Vector3(5f, 0.2f, 5f);
                    face.GetComponent<Renderer>().sharedMaterial = MaterialFactory.Unlit(new Color(0.95f, 0.93f, 0.85f));
                    Box(root, "Spire", new Vector3(0f, top + 26f, 0f), new Vector3(5f, 4f, 5f), roofMat);
                    break;
                }
            }
        }

        // ------------------------------------------------------------------ bridges and cover
        private void BuildBridge(BridgeSpec b)
        {
            var root = new GameObject(b.Id).transform; root.SetParent(Root, false);
            root.SetPositionAndRotation(new Vector3(b.X, b.DeckY, b.Z), Quaternion.Euler(0f, b.Yaw, 0f));
            Box(root, "Deck", new Vector3(0f, -0.2f, 0f), new Vector3(b.Width, 0.4f, b.Length), deckMat);
            Box(root, "RailL", new Vector3(-b.Width * 0.5f + 0.1f, 0.5f, 0f), new Vector3(0.2f, 1f, b.Length), woodMat);
            Box(root, "RailR", new Vector3(b.Width * 0.5f - 0.1f, 0.5f, 0f), new Vector3(0.2f, 1f, b.Length), woodMat);
        }

        private void BuildCover(IslandLayout l)
        {
            var root = new GameObject("Cover").transform; root.SetParent(Root, false);
            foreach (var p in l.Pois)
            {
                var rng = new DetRng(l.Seed * 3u + (uint)p.Id.Length * 17u + (uint)(p.X * 10f));
                for (int i = 0; i < 6; i++)
                {
                    float a = rng.Range(0f, 6.283f), dist = rng.Range(14f, p.Radius * 1.1f);
                    float x = p.X + Mathf.Cos(a) * dist, z = p.Z + Mathf.Sin(a) * dist;
                    if (!l.IsLand(x, z) || l.SlopeAt(x, z) > 0.3f) continue;
                    var s = new Vector3(rng.Range(1.4f, 3.2f), rng.Range(1f, 1.8f), rng.Range(1.2f, 2.4f));
                    var c = Box(root, "Cover", new Vector3(x, l.HeightAt(x, z) + s.y * 0.5f - 0.1f, z), s, coverMat);
                    c.transform.localRotation = Quaternion.Euler(0f, rng.Range(0f, 360f), 0f);
                }
            }
        }

        // ------------------------------------------------------------------ vegetation
        private Mesh pine, palm, rock;

        private void EnsureVegetationMeshes()
        {
            if (pine != null) return;
            var b = new PaletteMeshBuilder(); b.Prism(Vector3.zero, 0.25f, 2.2f, 6, 0); b.Cone(new Vector3(0f, 1.6f, 0f), 1.5f, 3.2f, 8, 1); b.Cone(new Vector3(0f, 3.2f, 0f), 1.1f, 2.6f, 8, 1);
            pine = b.ToMesh("Pine");
            b = new PaletteMeshBuilder(); b.Prism(Vector3.zero, 0.2f, 4.2f, 5, 0);
            for (int i = 0; i < 6; i++) { float a = i * Mathf.PI / 3f; b.Cone(new Vector3(Mathf.Cos(a) * 0.9f, 4.1f - 0.3f, Mathf.Sin(a) * 0.9f), 0.45f, 2.2f, 4, 2); }
            palm = b.ToMesh("Palm");
            b = new PaletteMeshBuilder(); b.Blob(new Vector3(0f, 0.5f, 0f), new Vector3(1.1f, 0.7f, 0.9f), 3, 5u);
            rock = b.ToMesh("Rock");
        }

        private void BuildVegetationChunk(IslandLayout l, int cx, int cz, Transform parent)
        {
            EnsureVegetationMeshes();
            float size = ChunkCells * l.Cell, x0 = -l.Half + cx * size, z0 = -l.Half + cz * size;
            var rng = new DetRng(l.Seed * 11u + (uint)(cx * 31 + cz * 7 + 1));
            var list = new List<CombineInstance>();
            const float spacing = 11f;
            for (float z = z0; z < z0 + size; z += spacing)
                for (float x = x0; x < x0 + size; x += spacing)
                {
                    float px = x + rng.Range(0f, spacing), pz = z + rng.Range(0f, spacing);
                    if (rng.Value() > VegetationDensity) continue;
                    float h = l.HeightAt(px, pz);
                    if (h < 1.8f || h > 42f || l.SlopeAt(px, pz) > 0.5f) continue;
                    if (NearAnything(l, px, pz)) continue;
                    Mesh m; float scale = rng.Range(0.8f, 1.5f);
                    float roll = rng.Value();
                    if (h < 7f) m = roll < 0.7f ? palm : rock; else if (roll < 0.82f) m = pine; else m = rock;
                    list.Add(new CombineInstance { mesh = m, transform = Matrix4x4.TRS(new Vector3(px, h - 0.1f, pz), Quaternion.Euler(0f, rng.Range(0f, 360f), 0f), Vector3.one * scale) });
                }
            if (list.Count == 0) return;
            var combined = new Mesh { name = "Veg_" + cx + "_" + cz, indexFormat = IndexFormat.UInt32 };
            combined.CombineMeshes(list.ToArray(), true, true);
            var go = new GameObject("Veg_" + cx + "_" + cz, typeof(MeshFilter), typeof(MeshRenderer));
            go.transform.SetParent(parent, false);
            go.GetComponent<MeshFilter>().sharedMesh = combined;
            var r = go.GetComponent<MeshRenderer>(); r.sharedMaterial = vegMat; r.shadowCastingMode = CastTreeShadows ? ShadowCastingMode.On : ShadowCastingMode.Off;
            // whole chunks are culled by distance when they are small on screen
            var lod = go.AddComponent<LODGroup>();
            lod.SetLODs(new[] { new LOD(0.012f, new[] { (Renderer)r }) });
            lod.RecalculateBounds();
            go.isStatic = true;
        }

        private static bool NearAnything(IslandLayout l, float x, float z)
        {
            foreach (var r in l.Roads) if (IslandGenerator.DistanceToPolyline(r.Points, x, z) < r.Width * 0.5f + 5f) return true;
            foreach (var b in l.Buildings) { float dx = b.X - x, dz = b.Z - z; float rad = Mathf.Max(b.Width, b.Depth) * 0.5f + 5f; if (dx * dx + dz * dz < rad * rad) return true; }
            if (IslandGenerator.DistanceToPolyline(l.River, x, z) < l.RiverWidth * 0.9f) return true;
            return false;
        }
    }
}
