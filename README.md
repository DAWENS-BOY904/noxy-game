# Noxy (Unity 6)

Original third-person shooter project. This repo currently contains: the **main lobby** (3D character preview, mode selection, profile, Vault, social, settings, HUD editor), an **offline Training range** prototype (movement, camera sensitivity, weapon slots, radar, touch controls), and a **development backend** (accounts, Player IDs, friends, party invites).

> **Verification status.** The scenes have **not been opened or played in the Unity Editor yet**. What *was* verified:
> - All C# (Core, Lobby, Tests) compiles against real `UnityEngine` + uGUI assemblies (`dotnet build` harness); the Editor script (`Scripts/Editor`) is not compile-checked.
> - Core logic and client/backend integration: 156 NUnit tests pass under plain .NET (`tools/core-tests`).
> - Backend: 28 Node tests pass (`server/`).
> - Supabase migration/RLS logic: 16 tests pass on PGlite with a stubbed Supabase `auth` schema (`tools/sql-tests`).
>
> Not verified: anything visual or input-related in Unity (layout, touch, rotation, gestures), real Google/Apple/Supabase provider flows, Android/iOS builds, performance on devices.

## Quick start

1. Install Unity 6 (6000.0+) with the Windows / Android / iOS modules you need. Unity Hub > Add > select this folder. Keep the project outside OneDrive.
2. On first open, `Noxy > Build Lobby + Training Scenes` runs automatically (or run it from the menu). It creates `Assets/_Project/Scenes/MainLobby.unity`, `Training.unity` and `BattleRoyale.unity`, registers them in Build Settings and applies project settings.
3. Press **Play** in `MainLobby`. Everything works offline. For online features start the backend (below) and sign in under **Settings > Account**.

If anything fails to compile, send me the Console output.

## Feature status

| Feature | Status |
|---|---|
| Lobby: 3D character (idle, drag-rotate, emotes, weapon prop), PLAY, mode select, responsive layouts, safe areas | Implemented (unverified in Unity) |
| Player ID, rank, level/XP, currencies, notifications, party slots on the lobby | Implemented; the permanent 10-digit ID exists **only after signing in** (offline shows "OFFLINE PROFILE") |
| Battle Royale | **Playable only against the dev backend** while a verified server is registered (otherwise Coming Soon, PLAY disabled): queue, 10-20 player cabin, 15 s countdown, plane, jump, freefall, parachute, Bermuda-style island, loot, server-decided combat, map, results, server-authoritative in-match currency pickups, and weapon shops (match-coins only). **Never run in Unity or on a device; no dedicated server; HTTP polling, not real-time netcode; no safe zone.** See [docs/BATTLE_ROYALE.md](docs/BATTLE_ROYALE.md) |
| Lone Wolf | **Coming Soon** (no game server). PLAY disabled. |
| Training | **Offline prototype**: playable range with local practice targets, no opponents |
| Vault (outfit, shoes, mask, headgear, backpack, weapon skin, emote): grid, rarity, owned/locked, search/sort/filter, 3D try-on, rotate, confirm/revert | Implemented. Purchasing **not implemented** (locked items are clearly marked, cannot be equipped). Weapon skins show on a placeholder rifle; emotes play placeholder animations. No parachutes. |
| Profile + Copy ID + rename; other players' public profile (add friend / invite / block / report) | Implemented (online features need the backend) |
| Performance analytics (dashboard, trends, match history + details) | Implemented against server-validated match records (`/api/me/performance/*`). Offline profiles show "sign in required". |
| Friends: search by ID/username, requests, accept/decline/cancel, remove, block | Implemented against the dev backend; offline shows an explanation, never fake data |
| Party + invitations: invite, accept/decline, ready, leave, rules (friends only, leader only, mode size, full, cooldown, expiry) | Implemented against the dev backend. **Party matchmaking does not exist** (no game server) |
| Settings: General, Graphics, Audio, Gameplay, Controls, Privacy, Account | Implemented. Music/SFX levels affect supported playback; lobby music asset is not included. Voice chat is not implemented; post-processing toggle has no effect (no stack installed) |
| Sensitivity (general, ADS, touch, horizontal %, vertical %, invert Y, smoothing, touch acceleration, gyro) | Applied to the Training and Battle Royale cameras through one `LookPipeline`. Gyro axis signs unverified on a device |
| FPS limit + VSync + presets | Applied via `DisplayApplier`/`FramePolicy` (PC: 60/120/144/240/300/Unlimited; mobile: 30/45/60, +90/120 if the display supports it). It is a cap, not a guarantee. VSync overrides the cap on PC (shown in the UI). |
| HUD editor (drag, size, opacity, preview, defaults, per-orientation, safe-area clamp, sync) | Implemented; edits never trigger gameplay |
| PC key bindings (conflict detection, restore defaults) and weapon slots 1/2/3 | Implemented (legacy Input Manager) |
| Radar | Training only: rotate or north-up, expand (M / button), size and corner in Settings (touch devices use the HUD-editor position). Policy forbids showing enemies in every mode. |
| Accounts / online sync | **Dev backend only**: plain HTTP, SQLite, polling (no WebSockets). Not production-ready. |
| Matchmaking, game server, purchases, rewards claiming online, voice chat, gyro/controller, localization | **Not implemented** |
| Supabase accounts (Google, Apple, e-mail, guest, Player ID, linking, session restore, sign-out) | **Code + SQL implemented and tested against simulators only** (122+ C# tests, 16 SQL/RLS tests on PGlite). **Not tested with real Supabase, Google or Apple.** Browser PKCE flow on all platforms; native Apple/Google need a plugin. Session storage is PlayerPrefs (dev only). See `docs/AUTH_SETUP.md` |
| Sign-in gate | Blocks the lobby until a verified session or an explicit, labelled "Play offline" |
| Backgrounds | All lobby/menu/sign-in backgrounds are images (`Resources/Backgrounds/{lobby,menu,auth}`); generated placeholders until you export or replace them (Noxy > Export Placeholder Backgrounds) |
| Anti-cheat | **Server-ready validators** (speed, teleport, fire rate, ammo, reload, hit range, through-wall, damage) with tests, plus categorised player reports with dedupe, rate limit and a review flag on the dev backend. **There is no game server yet, so nothing is enforced in play and cheating is not prevented.** A client cannot be trusted; real anti-cheat needs the authoritative server running these checks |
| Customization extras | Randomize and 3 saved looks (on-device) in the Vault; only owned items are ever applied |
| Training 3D upgrade | Static, strafing and pop-up targets, headshots (2x), recoil and spread bloom with a live crosshair, 60 s scored drill (enter the cyan zone) with combo, accuracy, rank and local best score |
| Lobby audio | Persistent single-owner music/SFX service, fades across scene transitions, generated original UI click/mode tones, and master/music/SFX mute and volume support. **No lobby music or production audio assets are included.** |

## Structure

```
Assets/_Project/
  Scripts/Core/       Noxy.Core   engine-light logic: modes, profile, cosmetics, settings, key bindings, HUD layout,
                                  sensitivity, weapons, radar math/policy, matchmaking, social + account clients, MiniJson
  Scripts/Lobby/      Noxy.Lobby  App (services, networking, display), Stage, Character, UI (+Screens, HUD controls),
                                  Matchmaking, Training (range, player, radar), BattleRoyale (island builder, cabin/plane,
                                  air controller, HUD, match flow)
  Scripts/Editor/     Noxy.Editor scene generator and project setup
  Tests/EditMode/     NUnit tests for Core (run in Unity Test Runner or via tools/core-tests)
server/               Node dev backend + tests (see server/README.md)
tools/core-tests/     Runs Core tests + client<->backend integration tests under plain .NET
```

## Run the backend

```powershell
cd server
node --no-warnings src/index.js          # http://127.0.0.1:8787
```

In the game: Settings > Account > server address, username, password > CREATE ACCOUNT. On a phone use `http://<PC LAN IP>:8787` (open the port in the firewall; development builds allow http). Use https for anything real. Details and the security model: `server/README.md`.

## Run the tests

```powershell
cd server;            node --test --no-warnings test/*.test.js     # backend, 28 tests
cd tools/core-tests;  dotnet test                                   # 156 tests: Core logic + client<->backend (needs Node on PATH)
cd tools/sql-tests;   node --test players.test.mjs                  # 16 migration/RLS tests (PGlite)
```
In Unity: Window > General > Test Runner > EditMode.

## Test plan (PC)

1. Play `MainLobby`. Drag to rotate the character; `1`/`2`/`3` select modes; Battle Royale/Lone Wolf show COMING SOON and PLAY is disabled; Training enables PLAY.
2. **Vault**: switch categories, search, sort, filter. Pick locked items (preview works, EQUIP disabled with an explanation). Pick an emote (plays). Confirm/Revert. Check Profile shows equipped items.
3. **Settings > Controls**: change a key, create a conflict (button turns red, SAVE refuses), restore defaults, save. **Edit HUD layout**: drag controls, tap one and change size/opacity, PREVIEW, SAVE, DEFAULTS.
4. PLAY Training: WASD + mouse look, LMB fire, RMB aim, R reload, 1/2/3 switch weapons (switch is delayed ~0.35 s; reload cancels on switch), M expands the radar, Esc pauses (sensitivity sliders change the camera immediately). Targets respawn after 3 s. Back to lobby.
5. Settings > Graphics: change FPS limit/VSync (the note explains the override), preset, window mode, resolution.
6. Start the backend; **Settings > Account**: create two accounts (second one in a second Editor/build or after signing out). Check the 10-digit ID in the lobby badge and Profile (COPY ID). Friends > ADD: search the other player by ID, send a request; on the other account Friends > REQUESTS > ACCEPT. Friends > INVITE (Battle Royale selected); the other account sees it under INVITES and the lobby party slots update within ~4 s. Block/unblock, Privacy settings (set "Party invites from: NOBODY" and try again). Sign out and in: same ID. Stop the server: the game keeps working offline and shows a "Server unreachable" notice.

## Test plan (Android / iOS)

- Editor first: **Window > General > Device Simulator** (phone, rotate). Training: Pause > **Touch controls ON** to drive the on-screen controls with the mouse.
- Android: install Android Build Support, set an application id, Build Settings > Android > both scenes (MainLobby first) > Build And Run with **Development Build** (needed for http to the dev server). Check portrait + landscape, safe areas, the back gesture (acts as Esc/pause), the HUD editor with real touches, and the Training controls (joystick, look area, fire, aim, jump, crouch, sprint, reload, 1/2/3, radar button).
- iOS: build on a Mac with Xcode. Same checks. iOS blocks plain http by default; use https or an ATS exception for development.
- Mobile FPS options are 30/45/60 (plus 90/120 when the screen supports it). Thermal/battery adaptation beyond the lower caps is not implemented.

## Replacing placeholders

See `Assets/_Project/Art/Placeholders/README.md` (character prefab, stage, UI art, fonts, catalog). A real character prefab at `Resources/Characters/LobbyCharacter.prefab` is picked up automatically; give it an Animator with triggers `Emote0..3` and an int `WeaponSlot` to drive emotes and held weapons.

## Economy
Wallet, missions, Booyah promo reward, diamond store (sandbox) and cosmetic purchases are implemented server-side. Not production-ready, see [docs/ECONOMY_SETUP.md](docs/ECONOMY_SETUP.md).

## Battle Royale and the supplied 3D models
Flow, local run steps, test plan, verification and gaps: [docs/BATTLE_ROYALE.md](docs/BATTLE_ROYALE.md). The supplied `.glb` models are **quarantined, not imported**: they are labelled CC-BY but look extracted from a commercial game (details and measurements in [docs/ASSET_REPORT.md](docs/ASSET_REPORT.md)); original placeholders are used instead.

## Supabase accounts and the game backend
Supabase is the only sign-in: the game backend verifies the Supabase session and uses the Player ID stored in your Supabase profile. **Not verified against a real Supabase project, Google or Apple** (no credentials were available); all tests used a fake or PGlite. Run `tools/supabase-live-check` against your project. Known open gaps (plain-text session storage, no in-game account deletion, gameplay data in the backend's SQLite file rather than Supabase Postgres) are listed in [docs/SUPABASE_AUDIT.md](docs/SUPABASE_AUDIT.md).

## Clans, clan wars, companions, map events, fair play
Implemented on the dev backend and in the lobby (CLANS, CLAN WARS, COMPANIONS, REPUTATION). No dedicated game server exists yet, so war and map-event results were only tested by simulating the server over HTTP; not run in Unity; not production-ready. See [docs/CLANS_FAIRPLAY_SETUP.md](docs/CLANS_FAIRPLAY_SETUP.md).
