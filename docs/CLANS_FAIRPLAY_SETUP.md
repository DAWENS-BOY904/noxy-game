# Clans, Clan Wars, Companions, Map Events and Fair Play

Status: implemented on the **Node dev backend** (`server/`) and in the Unity client. Backend behaviour is covered by automated tests that drive the HTTP API directly. **Nothing here has been run inside the Unity Editor, and there is no dedicated game server yet**, so clan-war and map-event flows were only exercised by simulating the game server with HTTP calls. Treat all of it as not production-ready.

## What exists

| Feature | Backend | Unity client |
|---|---|---|
| Clans (roles, invites, applications, chat, reports, leaderboard) | `server/src/clans.js` | `Core/Community.cs`, `Lobby/UI/Screens/CommunityScreens.cs` (`ClansScreen`) |
| Clan wars (events, queue, rosters, results, disputes, history, contributions) | `server/src/clanwars.js` | `ClanWarsScreen` |
| Companions (catalog, variants, unlock, equip, XP) | `server/src/companions.js` | `CompanionsScreen`, `Lobby/Character/CompanionFollower.cs` |
| Map events (configurable, deterministic planning, server-validated progress/claims) | `server/src/mapevents.js` | `Lobby/UI/MapEventHud.cs`, `MapEventClock` |
| Fair play (reports, reputation, moderation, appeals, audit, retention) | `server/src/fairplay.js` | `ReputationScreen` |
| Supabase schema + RLS | `supabase/migrations/20250301000000_social_fairplay.sql` | - |

Lobby buttons: CLANS, CLAN WARS, COMPANIONS, REPUTATION. All screens show "Sign in required" while offline instead of faking data.

## Trust model

* Clients send intents only. Roles, prices, rosters, scores, rewards and reputation are decided on the server.
* Game-server routes (`/api/internal/*`) require the `x-noxy-server-key` header (`NOXY_MATCH_SERVER_KEY`). The key must never ship in the client; with no key set these routes return 503.
* Moderator routes (`/api/admin/*`) require a row in `moderators`. There is no API to grant it:

```sql
INSERT INTO moderators(player_id, role, created_at) VALUES (<players.id>, 'admin', strftime('%s','now')*1000);
```
  `admin` can read the audit log and suspend; `moderator` can review reports and appeals.
* Reports never auto-punish. Only a moderator's confirmed action lowers reputation (event-sourced, idempotent). Reporting yourself, duplicate reports and report floods are rejected or rate-limited. Players cannot see reports about themselves.
* Rewards (clan-war coins, map-event coins, companion XP) go through the single idempotent ledger `economy.apply`. Retried or replayed result submissions do not pay twice.

## How a dedicated server integrates

All calls send `x-noxy-server-key`.

1. **Clan wars:** `POST /api/internal/clanwars/events` (schedule), `.../events/:id/pair`, `POST .../matches/:id/start`, then `POST .../matches/:id/result` with rounds, per-player kills/deaths/abandoned. Results are validated against the stored roster. `POST .../sweep` voids stale or crashed matches with no penalty. Disputed results go to `/api/admin/clanwars/*`.
2. **Map events:** `POST /api/internal/matches/:matchId/events/plan` plans events from the configs (positions are inside map bounds and outside blocked rectangles). Report objective progress with `POST /api/internal/map-events/:id/progress` (distance and rate checked) and loot with `.../claim`. Events persist in the database, so a restarted server resumes them. Operators tune maps and event types with `PUT /api/internal/maps/:map` and `PUT /api/internal/map-events/configs/:id`.
3. **Fair play:** `POST /api/internal/fairplay/abandon` and `/clean-match`, `GET /api/internal/players/:publicId/eligibility` before admitting a player to competitive play.
4. **Companions:** `GET /api/internal/players/:publicId/match-profile` returns the equipped companion for cosmetic spawning; `POST /api/internal/companions/xp` adds XP.
5. **Retention:** call `POST /api/internal/maintenance/purge` on a schedule (cron). It deletes old report evidence and resolved reports past the configured retention window.

Clients poll `GET /api/matches/:matchId/events`. Every event carries absolute timestamps plus the server's `serverNow`, and `MapEventClock` derives the countdown, so all clients show the same time regardless of local clock.

## Companion rules (cosmetic only)

`CompanionFollower` has no colliders or rigidbody, is never network-synchronised, and is pooled per companion id. In a match, spawn it only on the local client (and optionally for others' views as purely visual), on an ignore-raycast layer, so it cannot block movement, hide or reveal enemies, or intercept hits.

## Assets required (all current art is a placeholder)

| Asset | Placeholder now | Needed |
|---|---|---|
| 7 companion models (bolt, pip, orbit, glimmer, cinder, tide, nova) | Primitive cube/sphere/capsule + glow dots in `CompanionFollower` | Original low-poly meshes with 2-3 LODs, mobile-friendly materials |
| Companion animation controllers | Procedural bob/spin | Idle, follow-move, emote clips, one Animator Controller |
| Variant materials (ember, neon, ...) | Hue shift of the placeholder | Per-variant material sets |
| Clan emblems | `emblem` index only, no art | Emblem sprite atlas |
| Map-event markers and supply-drop prop | Text banner in `MapEventHud` | World marker prefab, minimap icons, crate model, VFX |
| Report/appeal/clan icons, sounds | None | Icon set, UI sounds |

## Configuration reference

Server env vars: `NOXY_MATCH_SERVER_KEY` (required for any `/api/internal/*` use), plus the economy variables in [ECONOMY_SETUP.md](ECONOMY_SETUP.md). Clan cost, member limits, war rewards, report limits, reputation thresholds, appeal window and retention are in the module `cfg` blocks (`clans.js`, `clanwars.js`, `fairplay.js`, `mapevents.js`).

Supabase: apply `supabase/migrations/20250301000000_social_fairplay.sql` after the earlier migrations. It adds the tables, RLS, an append-only audit trigger and service-role-only functions. It was validated on PGlite (`tools/sql-tests/social.test.mjs`), **not on a real Supabase project**. The Node backend is what currently implements the full gameplay rules; the SQL covers the data model and trusted write paths.

## Tests

```
cd server;            node --test --test-force-exit --no-warnings test/*.test.js     # 78 tests
cd tools\sql-tests;   node --test --test-force-exit --no-warnings economy.test.mjs players.test.mjs social.test.mjs
cd tools\core-tests;  dotnet test                                                      # client + real backend over HTTP
```

Covered: role permissions, duplicate names/tags, forged/replayed war results, simultaneous finishing and claims, reward caps, disputes and voiding, crash sweeps, reputation idempotency, report limits and privacy, appeals, audit trail, restart recovery (file database), signed-out client behaviour.

## Not done / manual

* No dedicated Linux server or multi-client playtest; reconnect behaviour beyond server-restart persistence is untested.
* Not opened or built in Unity; the `Assets/_Project/Tests/EditMode/CommunityTests.cs` tests run in the compile harness only.
* Nav grid on the lobby HUD got four more buttons; verify layout on small phones.
* Companion equip is shown in the lobby preview only while the Companions screen has been opened this session.
* Coins from a clan-war result later voided on dispute are not clawed back (XP/rating/wins are reverted).
* No controller-specific navigation work beyond the existing screen framework.
* Real moderation staff, privacy policy text and published reward/eligibility rules are required before launch.
