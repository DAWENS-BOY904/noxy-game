# Placeholders and how to replace them

Everything visible is generated in code so the project runs with zero imported art. Replace as follows:

| Placeholder | Replace with | How |
|---|---|---|
| Original Noxy Pathfinder character (`PlaceholderCharacterView`) | Rigged character prefab with an Animator (idle) | The current lobby character is an original, code-built field scout with teal signal details and an idle animation. To replace it with a production model, save a rigged prefab as `Assets/_Project/Resources/Characters/LobbyCharacter.prefab`. It is picked up automatically (`CharacterViewFactory`). Create a child object named exactly like a cosmetic id (e.g. `outfit_urban`, `mask_visor`) for each item; the active one per slot is shown. |
| Original Noxy 3D staging environment (`LobbyStage`) | Authored environment | The lobby builds a lit launch platform, signal gateway, distant outpost skyline, and subtle ambient effects at runtime. `LobbyStage.Create()` reuses an existing stage to avoid duplicates. To replace it with an authored scene environment, add a `LobbyStage` component to that root or remove the `LobbyStage.Create()` call in `LobbyBootstrap`. |
| Lobby music | Original synthesized ambient loop | `Assets/_Project/Resources/Audio/LobbyMusic.wav` is an original 16-second stereo synth loop generated for Noxy; it uses no sampled or third-party audio. `LobbyAudioService` loads it from Resources, loops it, fades between scenes, and respects Master / Music levels and mute. Replace only with an original or properly licensed clip. |
| UI sound effects | Original or properly licensed UI sounds | The lobby currently uses short generated tones for button clicks and mode selection. Replace these with authored clips when available; route them through `LobbyAudioService` so SFX mute and volume remain consistent. |
| Character selection / animations | Original rigged character prefabs and animation clips | No alternate character models or production animation clips are included. Use one compatible humanoid rig, then add character definitions, ownership validation and animation mappings before exposing selection. |
| Weapons and attachments | Original weapon meshes and compatible attachment points | Current weapon presentation is a placeholder. Add attachments only when the mesh, data definition, and gameplay behavior all support them. |
| Generated UI sprites (`UISprites`) | Real UI art | Swap the sprites in `UISprites` / `UIKit.NewButton`, or migrate screens to prefabs. Colors/sizes live in `UITheme`. |
| Text (legacy `UnityEngine.UI.Text`, built-in font) | TextMeshPro + a brand font | Replace `UIKit.NewText`; all screens create text through it. |
| Currency names (Credits / Cores), item names | Final design names | `Cosmetics.cs`, `UITheme.cs`, `LobbyHud.cs`. |
| Cosmetic catalog (`DefaultCosmeticCatalog`) | ScriptableObject or backend data | Implement `ICosmeticCatalog`. |
| Application id / company / icon | Real values | Project Settings > Player. |
