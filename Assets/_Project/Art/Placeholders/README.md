# Placeholders and how to replace them

Everything visible is generated in code so the project runs with zero imported art. Replace as follows:

| Placeholder | Replace with | How |
|---|---|---|
| Primitive character (`PlaceholderCharacterView`) | Rigged character prefab with an Animator (idle) | Save it as `Assets/_Project/Resources/Characters/LobbyCharacter.prefab`. It is picked up automatically (`CharacterViewFactory`). Create a child object named exactly like a cosmetic id (e.g. `outfit_urban`, `mask_visor`) for each item; the active one per slot is shown. |
| Procedural stage (`LobbyStage`) | Authored environment | Remove the `LobbyStage.Create()` call in `LobbyBootstrap` and put your environment in the scene. |
| Generated UI sprites (`UISprites`) | Real UI art | Swap the sprites in `UISprites` / `UIKit.NewButton`, or migrate screens to prefabs. Colors/sizes live in `UITheme`. |
| Text (legacy `UnityEngine.UI.Text`, built-in font) | TextMeshPro + a brand font | Replace `UIKit.NewText`; all screens create text through it. |
| Currency names (Credits / Cores), item names | Final design names | `Cosmetics.cs`, `UITheme.cs`, `LobbyHud.cs`. |
| Cosmetic catalog (`DefaultCosmeticCatalog`) | ScriptableObject or backend data | Implement `ICosmeticCatalog`. |
| Application id / company / icon | Real values | Project Settings > Player. |
