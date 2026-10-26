
# Noxy - Main Lobby (Unity 6)

First vertical slice of an original third-person multiplayer shooter: a playable main lobby with a 3D character preview, game-mode selection, profile/wardrobe/inventory, navigation screens and a PLAY flow.

> Status: written without access to a Unity Editor, so it has **not been compiled or run yet**. Open it in Unity 6 and follow "First run" below; report any compile error and it will be fixed.

## What is real vs. mocked

| Area | State |
|---|---|
| Lobby scene, 3D character, idle animation, drag/auto rotation | Implemented (procedural placeholder character, no imported art) |
| Mode selection (Battle Royale / Lone Wolf / Training) | Implemented. Selection is real state, saved to the local profile |
| Battle Royale, Lone Wolf | **Coming Soon**: PLAY is disabled, no server exists |
| Training | **Offline prototype**: PLAY runs a simulated prepare/load flow and loads the placeholder `Training` scene. No gameplay |
| Profile, XP, stats, equipped items | Implemented on a **local mock profile** (`local_profile.json` in `Application.persistentDataPath`) |
| Wardrobe / Inventory | Implemented: preview, rotate, confirm/revert, equip. Unowned items can be tried on but not equipped |
| Friends, Party, Missions, Shop | Screens exist with clearly labelled sample data; invites and purchases are **disabled** |
| Rewards | Daily reward claim works locally (device-only) |
| Notifications | Implemented (toast + inbox + badges) |
| Matchmaking against a server, authentication, purchases, multiplayer | **Not implemented** |

## First run

1. Install Unity 6 (6000.0 LTS or newer) with Windows, Android and iOS modules as needed.
2. Unity Hub -> Add -> select this folder (`noxy`). If Hub offers an Editor upgrade, accept it.
   Tip: keep the project outside OneDrive (the `Library` folder syncs badly).
3. On first open the script `Noxy > Build Lobby + Training Scenes` runs automatically and creates `Assets/_Project/Scenes/MainLobby.unity` and `Training.unity`, adds both to Build Settings and opens the lobby. If it didn't, run that menu item manually.
4. Press Play.

## Controls

- Mouse/touch: drag empty space to rotate the character; tap buttons.
- Keyboard: `1` `2` `3` select mode, `Enter` = PLAY, `A`/`D` or arrows rotate, `Esc` = back / cancel (Android back button maps to Esc).
- Settings has a debug toggle "Simulate matchmaking error" to test the error/retry UI.

## Structure

```
Assets/_Project/
  Scripts/Core/      Noxy.Core  - engine-light logic: modes, profile, cosmetics, matchmaking, notifications
  Scripts/Lobby/     Noxy.Lobby - App (bootstrap/services), Stage, Character, UI (+Screens), Matchmaking, Training
  Scripts/Editor/    Noxy.Editor - scene generator + project setup
  Tests/EditMode/    NUnit tests for Core
  Scenes/            generated scenes
  Resources/Characters/  drop LobbyCharacter.prefab here to replace the placeholder character
  Art/Placeholders/  notes on replacing placeholders
```

## Connecting a backend / dedicated server later

- `IProfileService` -> replace `LocalProfileService` (see `AppServices.EnsureInitialized`). Make calls async at that point.
- `IMatchmakingService` -> add e.g. `DedicatedServerMatchmakingService` before `UnavailableMatchmakingService` in `AppServices`. It returns a `MatchAssignment` (address/port/session token).
- `DefaultGameModes` -> flip `Availability` to `Available` per mode when its server exists, or load modes from the backend.
- Never put server credentials or secret keys in the client. The client should only hold short-lived tokens issued by your backend.

## Replacing placeholders

See `Assets/_Project/Art/Placeholders/README.md`.
=======
# noxy-game
