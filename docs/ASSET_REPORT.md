# Supplied 3D models: inspection and decision

Inspected with `tools/asset-validate/glb-inspect.mjs` and `glb-meshes.mjs` (pure Node, no dependencies). The machine-readable manifest is
`Core/LandmarkAssets.cs` (`LandmarkManifest`, `LandmarkAssetPolicy`).

## Decision: none of these models are imported or shipped

Every file is a Sketchfab download labelled CC-BY-4.0 by its uploader, but:

- The titles say "freefire" / "free fire".
- Node and texture names look extracted from a commercial game's files: `PFB_Cabin_Winterlands_*`, `Zone_A_House_Huge_A_LOD0`, `Tex_0170_0.dds`, and a mesh literally called `map bermuda_66`.
- A CC-BY label put on a model by a third-party uploader does not give that uploader rights they do not hold. If these are ripped from the game, shipping them is the same problem as copying the game's assets, which this project must not do.

So `RightsConfirmed = false` for all of them and `LandmarkAssetPolicy.CanUse` returns false with a reason. The game uses **original procedural placeholders** instead (cabin and plane in `BrWorldParts.cs`, landmarks in `IslandBuilder.cs`). A person must check provenance, then set `RightsConfirmed = true`, before any of this changes. Even then CC-BY needs visible attribution (`LandmarkAssetPolicy.Credit` produces the line) and a licence link in your credits screen.

## Review: "Free Fire Adam" T-pose model by BOSS H (requested 2026-10-26)

Source reviewed: https://sketchfab.com/3d-models/freefire-new-tpose-adam-model-by-boss-shivraj-5a4f86bd89414348a17ea1d8b7864674
(project file: `Assets/_Project/Environment/Landmarks/new-user-format.glb`, manifest id `tpose_character`).

**What the listing actually shows**

- Uploader: Sketchfab account "BOSS H" (`/Bossh`), self-describing as the YouTube channel "BOSS SHIVRAJ". The description only promotes that channel ("FOLLOW FOR MORE MODEL'S") — it makes **no claim of original creation and shows no permission from Garena**.
- Title and tags are the uploader's own words: `freefire-new-tpose-adam-model`, tagged `freefire`, `freefiregarena`, `freefirecharactermodel3d`, `freefiremax`, `freefiremalebundle`, `freefireclocktower`. "Adam" is one of Free Fire's default characters; the uploader's own account hosts a series of other Free Fire assets, which is consistent with game-file extraction, not original art.
- Licence label on the page: "CC Attribution" (CC BY 4.0), published 18 Aug 2022. The GLB's embedded metadata repeats author/licence/source; its `copyright` field is **empty**.

**Verdict: rights are NOT adequately verified. The asset stays blocked.**

A CC BY 4.0 licence can only be granted by someone who holds the rights to the work. The evidence shows a third party redistributing a recognisable commercial game character under a licence they have no demonstrated authority to apply. The label on the page is self-assigned metadata, not proof of ownership; Sketchfab does not verify uploader ownership (the page itself links a DMCA report form). Nothing in the listing, the embedded metadata, or the file establishes that BOSS H / BOSS SHIVRAJ owns or may redistribute Garena's Free Fire character. Shipping it would also directly violate this project's standing rule against copying Free Fire characters or artwork. `RightsConfirmed` for `tpose_character` remains **false** and `LandmarkAssetPolicy.CanUse` continues to return false. No attribution line is added to the Credits screen because the model will not ship; attribution is a licence obligation for *use*, not for an unused file.

What would change the verdict: written confirmation from the actual rights holder (Garena), or proof the mesh is the uploader's original fan-art-free creation with no game-derived geometry or textures — neither of which is plausible for this listing, and neither exists today.

**Technical facts (re-measured 2026-10-26 with `tools/asset-validate/glb-inspect.mjs`, unchanged from the first inspection)**

- glTF 2.0, no required extensions; 5 meshes, 5 double-sided materials, 5 PNG textures (256-1024 px, ~4 MB uncompressed), 5,454 triangles, 3,522 vertices.
- Bounds 7.35 x 8.01 x 1.70 m: an **8 m tall** character (a human-scaled character needs about 0.22x rescale), pivot at the feet.
- **`skins: 0`, `animations: 0` — confirmed: no skeleton, no rig, no animations.**

Rigging work that would be required *if* rights were ever settled (they are not): add a humanoid skeleton (hips/spine/chest/neck/head, arms, hands, legs), skin-bind all 5 meshes, author or retarget the full animation set the game already needs (idle, walk, run, jump, freefall, glide, land, crouch, punch, weapon poses), rescale to ~1.8 m, and hook it into the existing `CharacterView`/`CharacterPreview` pipeline. This is roughly a new character deliverable; the current procedural placeholder character already covers all of it.

## Measurements

| File (actual path under `Assets/_Project/Environment/Landmarks/`) | Author | Raw size (m) | Useful size (m) | Triangles | Notes |
|---|---|---|---|---|---|
| `PlaneCabin/plane_cabin__freefire.glb` | sxnneh | 37.2 x 6.8 x 10.3 | same | 5,111 | Clean. Origin near floor but off-centre. Textures 1024/256/128 |
| `Plane/freefire_plane.glb` | sxnneh | 39.5 x 11.5 x 30.0 | same | 972 | Needs `KHR_materials_pbrSpecularGlossiness`; convert to metal/rough or use an importer that supports it |
| `Mill/free_fire_mill_3d_model_by_ramim_bhai.glb` | Ramim Bhai | 1569 x 104 x 1519 | 127 x 43 x 169 | 71,300 | **Not a mill**: a map chunk ("map bermuda_66"), a stone bridge and factory bases. 27 materials / 26 textures (about 50 MB uncompressed) |
| `FacTory/factory_3d_model.glb` | Ramim Bhai | 1820 x 33 x 1605 | 149 x 33 x 135 | 35,224 | Delete the 1820 m `Plane_0` ground plate. 18 textures (about 52 MB uncompressed) |
| `ClockTower/clock_tower_free_fire_model.glb` | JOKER SQUADS | 2721 x 478 x 2787 | 809 x 89 x 867 | 32,105 | **The requested name `clock_tower_freefire_model.glb` does not exist**; this is the real file. A map region (2.8 km plate, mansion, houses, tower), not a single tower |
| `parachute_simple.glb` | sxnneh | 7.1 x 6.4 x 3.5 | same | 580 | Extra file, clean |
| `new-user-format.glb` | BOSS SHIVRAJ | 7.3 x 8.0 x 1.7 | same | 5,454 | Extra file: a static T-pose mesh titled as a Free Fire character, no skeleton or animation, 8 m tall. A recognisable character from a commercial game: **do not ship** |
| `shop__freefire.glb` | sxnneh | 2.2 x 3.0 x 1.7 | same | 146 | Extra file, clean |

Unity does not import `.glb` without a package, and none is installed, so these files are inert in the project today.

## If rights are confirmed: import workflow

1. Add a glTF importer (for example the glTFast package `com.unity.cloud.gltfast`) or convert in Blender and export FBX. The built-in render pipeline needs the importer's built-in shader graphs; spec/gloss models need conversion.
2. Strip oversized plates (Mill `map bermuda_66`, Factory `Plane_0`, Clock Tower ground/base), re-centre pivots, atlas textures (mobile budget: a few MB per landmark, ASTC on Android/iOS), add colliders and LODs.
3. Fit each landmark to its pad (`IslandLayout` POIs carry `LandmarkSlot` = `mill`, `factory`, `clock_tower` and the pad height) and replace the placeholder in `IslandBuilder` only when `LandmarkAssetPolicy.CanUse` is true.
4. Add the attribution lines to the credits screen and keep the source URLs.

## Replacing the placeholders with your own original art

Keep the same sizes: cabin interior walkable floor is `BrCabinCfg` (about 26 x 6 m, the server enforces it), plane exterior about 40 m long, landmark footprints are the POI radii in `Island.cs`. Needed originals: aircraft interior and exterior, parachute, Mill, Factory, Clock Tower, building kits (houses, warehouses, towers), vegetation, loot crate, character set with run/jump/freefall/glide/parachute animations, weapon models, ocean material, sky.
