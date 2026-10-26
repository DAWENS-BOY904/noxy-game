# Battle Royale: match flow, how to run it, what is verified

Honest status first. **Nothing here has been run in Unity, on a device, or with more than one real client.** What was verified is
listed in [Verification](#verification). There is no dedicated game server; the Node backend runs the match rules.

## What exists

| Piece | Where | State |
|---|---|---|
| Match authority: queue, 10-20 player roster, 15 s cabin countdown, plane flight, jump window (opens 5 s after take-off, forced exit at the end of the route), freefall, parachute, landing, combat, loot, in-match currency pickups, weapon shops, results | `server/src/br.js` | Implemented. 18 tests plus a C# client driving a full drop against the real server |
| Island: deterministic 1200 m island, 14 POIs (Mill, Factory, Clock Tower + 11 original names), roads, river, bridges, buildings, loot, landing zones | `Core/Island.cs`, `Core/IslandExport.cs`, `server/data/maps/bermuda.json` | Implemented, validated, tested |
| Client models: DTOs, services, session/countdown logic, honest loading progress, jump kinematics, BR matchmaking, availability | `Core/BattleRoyale.cs` | Implemented, 16 + integration tests |
| Sensitivity: mouse/touch smoothing, touch acceleration, gyro, one pipeline | `Core/Settings.cs` (`LookPipeline`), Settings screen | Implemented, tested. Gyro axis signs are **unverified on a device** |
| Unity world and flow | `Lobby/BattleRoyale/*.cs` | Written and compile-checked against the Unity API only. **Never executed** |
| Scene | `Noxy > Build Battle Royale Scene` (also part of `Build Lobby + Training Scenes`) | Editor script, **never executed** |

### Match flow (what the client does)

1. Lobby: choose **Battle Royale**, press **PLAY**. The backend rejects the request (503 `no_server_available`) unless a verified online server is registered. It never invents players. If fewer than the minimum join before the deadline the match is **cancelled** and the player is told.
2. `BattleRoyale` scene loads. Loading screen stages (real progress, no timers): Connecting server, Finding players, Loading the Bermuda map, Preparing characters, Starting match. It shows percent, a "slow connection" note after 8 s, per-stage time-outs, **Cancel**, and error messages with **Try again / Lobby**.
3. The island is generated from the server's seed and its hash is compared with the server's (`/api/br/map-info`). A mismatch stops the match with a clear message instead of letting the client and server disagree.
4. Cabin phase (15 s): first-person walk inside a placeholder cabin with side windows; the plane flies the server's route from the server's clock. Other players appear at their server slots.
5. Flight: **JUMP** (button or the Jump key) appears at +5 s. Forced exit at the end of the route.
6. Freefall (steer with the move keys, camera look), parachute (**PARACHUTE** button/Jump key; opens by itself near the ground), landing. The client only proposes positions; the server validates each one and decides the landing (water landings are moved to the nearest land).
7. Combat opens when the route ends. Shots send only a direction; the server computes hits, headshots and damage. Loot is picked up with **E** (or the on-screen button) within 2.8 m; the server picks the contents. Currency pickups and weapon-shop purchases are also validated by the server (distance, phase and duplicate-protection). **H** uses a medkit.
8. Results: placement, eliminations, reward-processing status from the server. Spectating is **not built**.

Maps: minimap (north-up, 260 m window) and full map (**M** / touch map button) show the island image, POI names, the plane route and position, shop markers, currency pickup markers, and a local waypoint (click/tap the full map).

## Run it locally (PC)

```powershell
# 1. backend. The key is yours, never put it in the game.
cd server
$env:NOXY_MATCH_SERVER_KEY = "choose-a-long-random-string"
# solo/dev testing only: let one player start (the default is 10-20 players and a cancelled match if fewer join)
$env:NOXY_BR_CONFIG = '{"modes":{"battle_royale":{"minPlayers":1,"maxPlayers":20,"fillTimeoutMs":5000,"maxWaitMs":60000,"belowMin":"cancel","requireServer":true}}}'
node --no-warnings src/index.js

# 2. in another terminal: register the dev stand-in so the queue is allowed (see the note below)
$env:NOXY_MATCH_SERVER_KEY = "choose-a-long-random-string"
node tools/dev-br-server/register.mjs
```

Then in Unity: open the project, run `Noxy > Build Lobby + Training Scenes` (or `Noxy > Build Battle Royale Scene`), open `MainLobby`, press Play,
sign in (Settings > Account, backend address `http://127.0.0.1:8787`), select **Battle Royale** and press **PLAY**.
For a real multi-client test run a second Editor/build with a second account; the roster fills up to the configured minimum.

> `tools/dev-br-server/register.mjs` registers a **label** for the backend in the server registry and sends heartbeats. It is a development stand-in, not a dedicated server, and it proves nothing about one. `NOXY_BR_CONFIG` is read only by the dev backend and exists for testing; never use it on a public server.

### Test plan

PC (Editor or Windows build):
1. With no registered server, the Battle Royale card shows a Coming Soon note and PLAY is disabled. Start the stand-in: within about 15 s the card turns available.
2. PLAY: the lobby shows searching/progress and **Cancel** works while waiting. With the dev config (min 1) the match starts after the fill timeout.
3. Loading screen: five stages advance, percent only rises, Cancel and the failure screen (stop the backend during loading) work.
4. Cabin: look/walk with the Settings sensitivity; windows show the sea/island. Jump is hidden until the countdown reaches "Jump!" then works with Space or the button.
5. Freefall: steer, watch altitude/speed, open the parachute, land. The Console must not show "Position corrected by the server" in normal flight. Water landing should show the relocation message.
6. After landing: walk, open crates (E), collect match currency, use a shop, shoot, reload, switch weapons. With a second account verify the other player is visible and damage is applied. Press **M** for the full map and click to set a waypoint.
7. Quit mid-match from the pause menu: the text says it counts as abandoning.

Android / iOS: same flow with touch controls (move stick, look drag, JUMP/PARACHUTE/PICK UP buttons, MEDKIT). Point the game at `http://<PC LAN IP>:8787` and allow the port in the firewall; the backend and phone must share a network. Check text sizes in portrait and landscape, safe areas, and the gyro toggle in Settings (axis signs may need flipping: `LookPipeline.GyroSignYaw/Pitch`).

## Verification

Verified by automated tests:
- Server rules over real HTTP with simulated players: roster policy, cancellation, phases and timestamps, jump window and forced exit, freefall/parachute limits, landing and water relocation, damage/headshots/armor, fire-rate and ammo, loot uniqueness and range, server-authoritative currency pickups and shop purchases, interest management, disconnect and abandon handling, results and idempotent reward processing (`server/test/br.test.js`, backend suite 115/115).
- The generated island is valid (land share, POI spacing, reachable loot, bridges, landing zones) and the server's copy is fresh (`MapExportTests`). The server's coarse terrain agrees with the client's fine terrain to a median of 0.05 m, p99 1.38 m, worst 11 m.
- `JumpModel` was checked against the real server: 77 validated moves over a full drop in about 10 s, no rejections, landing on a landing pad (`BattleRoyaleIntegrationTests`). Core suite 222/222.
- Architecture guards: sensitivity is applied in exactly one place.

**Not verified:** running in Unity (scene build, UI layout, camera, character view, island meshes), any device, any frame-rate or memory measurement, more than a simulated set of clients, touch/gyro, a dedicated server, Supabase/Google/Apple (see [SUPABASE_AUDIT.md](SUPABASE_AUDIT.md)).

## Known gaps and limits

- **No dedicated server and no real-time netcode.** State goes over HTTP polling (about 4-5 Hz); movement of other players is smoothed, not predicted. Fast fights will feel laggy. A real game server needs the same rules (`br.js` is written to be transport-independent) and a UDP/WebSocket transport.
- Matches live in memory: a backend restart ends them.
- No safe zone / shrinking circle, no vehicles, no swimming (water landings are relocated; walking into water is not handled), no spectating, no squads/teams in the match.
- Building walls and doors are **not** enforced by the server's hit test (terrain only) and there is no lag compensation.
- Terrain disagreement between server (coarse) and client (fine) can cause an occasional correction on steep ground.
- Other players' pickups are only discovered when you try the same crate ("already taken").
- No LODs on buildings; occlusion culling must be baked in the editor; vegetation is merged per chunk with a LODGroup but nothing is measured. On mobile vegetation density is halved by default.
- The cabin and plane are **original primitive placeholders**; characters use the lobby's placeholder rig, and animation names are limited to the server's allow-list.
- The supplied GLB models are not used (see [ASSET_REPORT.md](ASSET_REPORT.md)).

## Tuning

Rules live in `DEFAULTS` and `WEAPONS` in `server/src/br.js` (cabin/flight timings, speeds, damage, ranges, roster policy). Client code reads the limits from `/api/br/config` so the two cannot drift. The island seed is `IslandGenerator.DefaultSeed` (2025); after changing `Island.cs` regenerate the server copy:

```powershell
$env:NOXY_WRITE_MAP = '1'; cd tools\core-tests; dotnet test --filter MapExportTests
```
