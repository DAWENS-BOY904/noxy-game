using System;
using System.Collections.Generic;

namespace Noxy.Core
{
    /// <summary>
    /// One externally supplied 3D model. Values come from tools/asset-validate (measured from the files, not assumed).
    /// RightsConfirmed stays false until a human has verified that the model may be shipped; until then the game uses its
    /// own original placeholder for that slot.
    /// </summary>
    public sealed class LandmarkAsset
    {
        public string Id = "";
        public string Role = "";
        public string RequestedPath = "";   // path the task asked for (may not exist)
        public string ActualPath = "";      // path that actually exists in the project
        public string Author = "", License = "", SourceUrl = "";
        public float[] MeasuredSizeMeters = new float[3];     // after removing oversized ground plates
        public float[] RawSizeMeters = new float[3];          // as authored
        public int Triangles;
        public string RequiredExtension = "";                  // glTF extension the importer must support
        public string Problems = "";                           // what must be fixed before it is usable
        public bool RightsConfirmed;                           // set by a human after checking the licence AND the origin of the model
        public string IpConcern = "";                          // why rights are doubtful, if they are
    }

    public static class LandmarkManifest
    {
        public const string Dir = "Assets/_Project/Environment/Landmarks/";

        public static readonly IReadOnlyList<LandmarkAsset> All = new List<LandmarkAsset>
        {
            new LandmarkAsset
            {
                Id = "plane_cabin", Role = "Pre-match aircraft interior",
                RequestedPath = Dir + "PlaneCabin/plane_cabin__freefire.glb", ActualPath = Dir + "PlaneCabin/plane_cabin__freefire.glb",
                Author = "sxnneh", License = "CC-BY-4.0", SourceUrl = "https://sketchfab.com/3d-models/plane-cabin-freefire-17d7240ae16e4697bb1c37249de79f33",
                RawSizeMeters = new[] { 37.2f, 6.8f, 10.3f }, MeasuredSizeMeters = new[] { 37.2f, 6.8f, 10.3f }, Triangles = 5111,
                Problems = "None technical: 3 double-sided materials, textures 1024/256/128 px. Origin sits near floor level but 59% along its length, so re-centre.",
                IpConcern = "Titled 'freefire'; node names (PFB_Cabin_Winterlands_*) look like they come from a commercial game's files.",
            },
            new LandmarkAsset
            {
                Id = "plane", Role = "Aircraft exterior",
                RequestedPath = Dir + "Plane/freefire_plane.glb", ActualPath = Dir + "Plane/freefire_plane.glb",
                Author = "sxnneh", License = "CC-BY-4.0", SourceUrl = "https://sketchfab.com/3d-models/freefire-plane-eff06ad1c830441ea75280b1862b7db4",
                RawSizeMeters = new[] { 39.5f, 11.5f, 30.0f }, MeasuredSizeMeters = new[] { 39.5f, 11.5f, 30.0f }, Triangles = 972,
                RequiredExtension = "KHR_materials_pbrSpecularGlossiness",
                Problems = "Uses the deprecated spec/gloss material extension: the importer must support it, or convert to metal/rough in Blender.",
                IpConcern = "Titled 'freefire'.",
            },
            new LandmarkAsset
            {
                Id = "mill", Role = "Landmark (Mill)",
                RequestedPath = Dir + "Mill/free_fire_mill_3d_model_by_ramim_bhai.glb", ActualPath = Dir + "Mill/free_fire_mill_3d_model_by_ramim_bhai.glb",
                Author = "Ramim Bhai", License = "CC-BY-4.0", SourceUrl = "https://sketchfab.com/3d-models/free-fire-mill-3d-model-by-ramim-bhai-5eff84e4d5f440e6bde81241d5f3c169",
                RawSizeMeters = new[] { 1569.3f, 104.3f, 1519.4f }, MeasuredSizeMeters = new[] { 127.3f, 43.4f, 169.3f }, Triangles = 71300,
                Problems = "Not a single mill: the file holds a map chunk ('map bermuda_66', a 1569 m terrain mesh), a stone bridge and factory bases. Needs the oversized meshes stripped and the rest split. 27 materials / 26 textures (about 50 MB uncompressed): atlas them for mobile.",
                IpConcern = "Contains a mesh named 'map bermuda_66' and .dds texture names, consistent with data taken from the game's own files.",
            },
            new LandmarkAsset
            {
                Id = "factory", Role = "Landmark (Factory)",
                RequestedPath = Dir + "FacTory/factory_3d_model.glb", ActualPath = Dir + "FacTory/factory_3d_model.glb",
                Author = "Ramim Bhai", License = "CC-BY-4.0", SourceUrl = "https://sketchfab.com/3d-models/factory-3d-model-e723f4fe48ec4b9da52ec6e4a442286b",
                RawSizeMeters = new[] { 1820.4f, 32.7f, 1605.1f }, MeasuredSizeMeters = new[] { 149.4f, 32.7f, 134.6f }, Triangles = 35224,
                RequiredExtension = "",
                Problems = "Contains a flat 1820 m 'Plane_0' ground plate to delete. Uses KHR_texture_transform (optional). 18 textures (about 52 MB uncompressed).",
                IpConcern = "Texture names (Tex_0170_0.dds ...) suggest game-extracted assets.",
            },
            new LandmarkAsset
            {
                Id = "clock_tower", Role = "Landmark (Clock Tower)",
                RequestedPath = Dir + "ClockTower/clock_tower_freefire_model.glb", ActualPath = Dir + "ClockTower/clock_tower_free_fire_model.glb",
                Author = "JOKER SQUADS", License = "CC-BY-4.0", SourceUrl = "https://sketchfab.com/3d-models/clock-tower-free-fire-model-c44e3c60f61d4ea289065ec63082b5bb",
                RawSizeMeters = new[] { 2721.1f, 477.9f, 2787.3f }, MeasuredSizeMeters = new[] { 808.8f, 88.9f, 867.3f }, Triangles = 32105,
                Problems = "The requested file name does not exist; the real file is clock_tower_free_fire_model.glb. It contains a 2.8 km ground plate, a 1.2 km base, and buildings spread across about 1.4 km (mansion, houses, tower): it is a map region, not a single tower. 1 alpha-blended material.",
                IpConcern = "Node names (Zone_A_House_Huge_A_LOD0, mansion_LOD0 ...) are typical of data taken from a commercial game.",
            },
            new LandmarkAsset
            {
                Id = "parachute", Role = "Parachute (extra file found in the project)",
                RequestedPath = "", ActualPath = Dir + "parachute_simple.glb",
                Author = "sxnneh", License = "CC-BY-4.0", SourceUrl = "https://sketchfab.com/3d-models/parachute-simple-030a235d6f314a86bebc33f322c0e71c",
                RawSizeMeters = new[] { 7.1f, 6.4f, 3.5f }, MeasuredSizeMeters = new[] { 7.1f, 6.4f, 3.5f }, Triangles = 580,
                Problems = "None technical.",
                IpConcern = "",
            },
            new LandmarkAsset
            {
                Id = "tpose_character", Role = "Not used: static character mesh found in the project",
                RequestedPath = "", ActualPath = Dir + "new-user-format.glb",
                Author = "BOSS SHIVRAJ", License = "CC-BY-4.0", SourceUrl = "https://sketchfab.com/3d-models/freefire-new-tpose-adam-model-by-boss-shivraj-5a4f86bd89414348a17ea1d8b7864674",
                RawSizeMeters = new[] { 7.3f, 8.0f, 1.7f }, MeasuredSizeMeters = new[] { 7.3f, 8.0f, 1.7f }, Triangles = 5454,
                Problems = "A static T-pose mesh with no skeleton (skins: 0) and no animations, 8 m tall (needs ~0.22x rescale), so it cannot replace the game's rigged character; would need full rigging plus the game's whole animation set.",
                IpConcern = "Reviewed 2026-10-26 (docs/ASSET_REPORT.md): the listing's CC-BY-4.0 label is self-assigned by uploader 'BOSS H'/'BOSS SHIVRAJ', whose own title and tags identify it as Garena's Free Fire 'Adam' character. No ownership claim, no Garena permission, empty copyright field. The uploader cannot grant a licence to IP they do not hold. Rights not verified: do not ship.",
            },
            new LandmarkAsset
            {
                Id = "shop_prop", Role = "Not used: small prop found in the project",
                RequestedPath = "", ActualPath = Dir + "shop__freefire.glb",
                Author = "sxnneh", License = "CC-BY-4.0", SourceUrl = "https://sketchfab.com/3d-models/shop-freefire-7ecf28b4854649289ab115a851608316",
                RawSizeMeters = new[] { 2.2f, 3.0f, 1.7f }, MeasuredSizeMeters = new[] { 2.2f, 3.0f, 1.7f }, Triangles = 146,
                Problems = "None technical (146 triangles, one 512 px texture).",
                IpConcern = "Titled 'freefire'.",
            },
        };

        public static LandmarkAsset Find(string id)
        {
            foreach (var a in All) if (a.Id == id) return a;
            return null;
        }
    }

    public static class LandmarkAssetPolicy
    {
        /// <summary>
        /// External models may be used only when the file exists AND a human confirmed the rights. Otherwise the caller must
        /// use the original placeholder. A missing file is reported, never silently skipped.
        /// </summary>
        public static bool CanUse(LandmarkAsset a, Func<string, bool> fileExists, out string reason)
        {
            reason = "";
            if (a == null) { reason = "Unknown landmark."; return false; }
            if (fileExists == null || !fileExists(a.ActualPath)) { reason = "File not found: " + a.ActualPath; return false; }
            if (!a.RightsConfirmed) { reason = "Rights not confirmed for '" + a.Id + "' (" + a.License + " by " + a.Author + "). " + (string.IsNullOrEmpty(a.IpConcern) ? "" : a.IpConcern + " ") + "Using the original placeholder."; return false; }
            if (string.IsNullOrEmpty(a.Author) || string.IsNullOrEmpty(a.SourceUrl)) { reason = "Attribution data (author, source) is missing."; return false; }
            return true;
        }

        /// <summary>Text that must appear in the game's credits for every external model that ships (CC-BY requires attribution).</summary>
        public static string Credit(LandmarkAsset a) =>
            "\"" + a.Id + "\" by " + a.Author + ", " + a.License + ", " + a.SourceUrl;

        /// <summary>True when the requested path is not the real one, so the caller can warn about the mismatch.</summary>
        public static bool PathMismatch(LandmarkAsset a) => !string.IsNullOrEmpty(a.RequestedPath) && a.RequestedPath != a.ActualPath;
    }
}
