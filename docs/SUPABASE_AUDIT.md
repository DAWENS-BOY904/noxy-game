# Supabase integration audit, repair and verification

**Read this first.** No Supabase project, anon key, Google/Apple credentials or dedicated game server were available in the environment where this work was done. Nothing below was run against your real Supabase project. Everything that *was* run used one of these stand-ins, and the report says which:

| Stand-in | What it really is | What it can prove | What it cannot prove |
|---|---|---|---|
| **PGlite** (`tools/sql-tests`) | The real SQL migrations in an in-process Postgres with a stubbed `auth` schema and roles | Constraints, triggers, RLS policies, column grants, function privileges | That your project's roles, extensions or dashboard settings match |
| **Fake Supabase, Node** (`server/test/supabase.test.js`) | In-memory GoTrue + PostgREST stand-in, test-only | Our server's handling of good, bad, expired, revoked and unreachable Supabase responses | Anything about real Supabase |
| **Fake Supabase, C#** (`tools/core-tests/fake-supabase.js`) | Same idea, driven by the real C# `AuthService` against the real Node backend | Client + server logic end to end | Anything about real Supabase |

To get real evidence, run [`tools/supabase-live-check`](../tools/supabase-live-check/run.mjs) against your project (see "How to run the live check"). It was smoke-tested only against the fake.

---

## 1. Bugs and fake / demo behaviour found

Search for `MockAuthService`, `FakeAuthService`, `MockSupabaseService`, `DemoPlayer`, `TestPlayer`, hardcoded profiles, fake server lists and empty save functions found **none**. The real problems were structural:

| # | Severity | Finding | Status |
|---|---|---|---|
| 1 | **Critical** | **Two disconnected identity systems.** Supabase login opened the lobby, but wallet, store, inventory, friends, parties, clans, settings and missions live on the Node backend, which only accepted its *own* username/password accounts and never verified Supabase tokens. A Supabase-signed-in player could use none of it; the Settings screen asked for a second account. | **Fixed.** The backend verifies the Supabase session; password accounts are disabled in `supabase` mode. |
| 2 | High | Two Player-ID generators (Supabase trigger and Node) would give one person two IDs. | **Fixed.** Supabase owns the ID; Node adopts it and never generates one for Supabase accounts. |
| 3 | High | Node stored password hashes and its own sessions in the game database. | **Fixed for production mode** (disabled, no password stored). Still exists in dev-only `local` mode. |
| 4 | Medium | The shipped example config (`YOUR-PUBLIC-ANON-KEY...`) counted as *configured*, so an unedited copy made the game call a fake host. | **Fixed.** Exact problem text, shown on the sign-in gate. |
| 5 | High | A service-role/secret key pasted into the client config would have been used silently. | **Fixed.** The client refuses `service_role` JWTs and `sb_secret_` keys and says why. The live-check script refuses them too. |
| 6 | Medium | Username rule differed (Supabase 3-20, Node 3-16): default names like `Operator_1234567890` (19 chars) could not be found by username search. | **Fixed** (Node now 3-20). |
| 7 | High | `grant_progress()` credited XP/coins/gems with no ledger entry, idempotency key or audit trail. Unused, but executable by `service_role`. | **Fixed.** Execute revoked (migration `20250401000000`). |
| 8 | Medium | `avatar_url` accepted any string (`javascript:`, `file:`, `data:`). | **Fixed.** https-only for new writes (`NOT VALID`, existing rows untouched). |
| 9 | High | The game-server URL could be plain `http://` to any host, so the account token would travel in clear text. | **Fixed.** `http` only for localhost / private networks; otherwise https. The backend also requires an https Supabase URL. |
| 10 | Medium | When Supabase login worked but the game server did not, screens said "Sign in to an account", which was false and misleading. | **Fixed.** Screens now say the account is verified but the game server is unavailable, with the reason, and it retries. |
| 11 | Medium | No server registry existed (and matchmaking correctly shows "unavailable"; it never listed fake servers). | **Implemented** (section 6). |
| 12 | Low | `players.coins/gems/xp/level` duplicate data the backend owns. | **Documented** in the schema (`comment on column`). Not removed. |
| 13 | **Open** | The Supabase **refresh token is stored in `PlayerPrefs` in plain text** (`PrefsSessionStore`, and the legacy `PrefsTokenStore`). | **Not fixed.** Needs Keychain / Keystore / Credential Manager behind the existing `ISessionStore`. Do not ship without it. |
| 14 | **Open** | Account deletion is not possible for Supabase users from the game: deleting an `auth.users` row needs the service role. `DELETE /api/me` answers 501 instead of faking success. Apple requires in-app account deletion for apps with account creation. | **Not fixed.** Needs a deployed Edge Function. |
| 15 | **Open** | Saved outfit "Looks" (Vault) are in `PlayerPrefs` only: not synced across devices. Training best score likewise. | **Not fixed**, classified below. |

## 2. Persistent-data classification (where it really lives now)

Architecture decision: **Supabase is the identity provider and holds the canonical profile; the Node backend is the system of record for gameplay data and is the only thing that writes to it.** The Node backend's own database is SQLite. The Supabase migrations for economy and social features (`20250201...`, `20250301...`) describe the intended Postgres schema and are tested on PGlite, but **nothing writes to those tables yet**. Moving the Node backend's storage to Supabase Postgres is the largest remaining task; until then, gameplay data persists in the backend's SQLite file (back it up, keep it on persistent disk).

| Data | Class | Authoritative store today | Real-service verified? |
|---|---|---|---|
| Account, Auth UUID, sessions | Persistent | Supabase Auth | Fake / PGlite only |
| Player ID, username, avatar | Persistent | Supabase `players` | Fake / PGlite only |
| Level, XP | Persistent | Node SQLite (Supabase columns informational) | Real Node, fake Supabase |
| Wallet, transactions, purchases, rewards, missions | Persistent | Node SQLite ledger | Real Node |
| Inventory, owned cosmetics, equipped loadout | Persistent | Node SQLite | Real Node |
| Settings, privacy, HUD layouts, control prefs | Persistent | Node SQLite | Real Node |
| Friends, party invitations, blocks, notifications | Persistent | Node SQLite | Real Node |
| Clans, wars, companions, map events, reports, reputation | Persistent | Node SQLite | Real Node |
| Statistics | Persistent | Node SQLite (`player_statistics`, written by the trusted match-result endpoint) | Real Node |
| Dedicated-server registry | Operational | Node SQLite | Real Node |
| Saved outfit Looks, Training best score | Persistent but **device-only** | `PlayerPrefs` | Open gap #15 |
| Supabase session / refresh token | Secret | `PlayerPrefs` plaintext | Open gap #13 |
| Offline profile ("Play offline") | Local mode, labelled as such | local JSON file | n/a, no account data |
| Movement, animation, unfinished match state | Temporary | not stored | by design |

## 3. How the repaired flow works

1. The client signs in with Supabase (`AuthService`). The lobby opens only when Supabase returns a verified session **and** `ensure_player()` returns a profile.
2. `AccountService.SyncWithAuth` starts a game-server session that sends the **live, auto-refreshed Supabase access token** (`ApiClient.TokenProvider`). The token is never written to the legacy token store.
3. The backend (`server/src/supabaseauth.js`) verifies every token with Supabase (`GET /auth/v1/user`, which checks signature, expiry and revocation), reads the user's own `players` row with the user's token (so RLS applies; no service key), and adopts that Player ID. Verification is cached for `NOXY_SUPABASE_CACHE_SECONDS` (60 s by default). That is also the worst-case delay before a sign-out elsewhere is noticed.
4. If Supabase cannot be reached, the backend answers **503** and the client shows the reason and retries every 15 s. There is no fallback identity.
5. A 401 from the backend makes the client force a Supabase refresh; if Supabase also refuses, everything returns to the sign-in screen.
6. Renaming goes **to Supabase first** with the user's token, is read back, and only then mirrored locally. A backend that accepts but does not persist is reported as an error (`update_not_persisted`).

## 4. Files changed

**Backend (`server/`)**: new `src/supabaseauth.js`, `src/servers.js`; changed `src/app.js`, `src/index.js`, `README.md`; new tests `test/supabase.test.js`, `test/servers.test.js`.

**Client (`Assets/_Project/Scripts/`)**: changed `Core/Social.cs` (`ApiClient.TokenProvider`, `UnavailableDetail`), `Core/AccountService.cs` (Supabase-bound session, retry, https rule, `UnavailableReason`), `Core/Auth.cs` (`Revalidate`, precise config problems, privileged-key refusal), `Core/Economy.cs`, `Core/Community.cs` (server list), `Lobby/App/AuthRuntime.cs`, `Lobby/App/AppServices.cs`, `Lobby/UI/AuthGate.cs`, `Lobby/UI/Screens/SettingsScreen.cs` (no second sign-in form; game-server status + retry), `Lobby/UI/Screens/CommunityScreens.cs` (`ServersScreen`), `Lobby/UI/ScreenManager.cs`, `LobbyHud.cs`, `LobbyBootstrap.cs`.

**Database**: new `supabase/migrations/20250401000000_identity_hardening.sql`.

**Tests / tooling**: new `tools/sql-tests/bridge.test.mjs`, `tools/sql-tests/identity.test.mjs`, `tools/core-tests/SupabaseBridgeTests.cs`, `tools/core-tests/fake-supabase.js`, `tools/supabase-live-check/run.mjs`; changed `tools/core-tests/CoreTests.csproj`, `Assets/_Project/Tests/EditMode/CommunityTests.cs`, `.gitignore`.

## 5. Migrations and RLS

Apply in order: `20250101000000_players.sql`, `20250201000000_economy.sql`, `20250301000000_social_fairplay.sql`, `20250401000000_identity_hardening.sql`.

`players` (verified on PGlite): FK `user_id -> auth.users(id) on delete cascade`; unique 10-digit `player_id` (format check + unique constraint + collision retry); unique case-insensitive username (3-20, `[A-Za-z0-9_]`); RLS **forced**; select/update own row only; **column-level grants** so clients can update `username` and `avatar_url` only; no insert/delete policy (profiles come from the sign-up trigger); `ensure_player()` idempotent and rate limited; `find_player()` exposes only `player_id, username, avatar_url, level`; `grant_progress()` no longer executable by any API role. RLS is not disabled anywhere.

## 6. Server registry

Implemented in `server/src/servers.js`; **no dedicated game server exists in this repo, no server is deployed, and no real server address is configured or invented.**

* Only a holder of `x-noxy-server-key` can `register`, `heartbeat` or `deregister`; clients can only read `GET /api/servers`.
* `register` returns a per-instance token; heartbeats must present it, so a server id cannot be hijacked. Re-registering (a restart) rotates the token and resets to `starting`.
* A record existing does **not** mean online. `online` requires: the server reported `online`, a heartbeat within `NOXY_SERVER_TTL_SECONDS` (30), and, if `NOXY_SERVER_PROBE=tcp`, a recent successful TCP connect to its address. Otherwise it is `starting`, `draining` or `offline`, and players are only shown `online` servers. Records stale for 24 h are deleted.
* Validation: host, port, capacity, mode (must be a known game mode), region, `players <= capacity`; registry size capped.
* The probe is TCP only. If your game transport is UDP-only, rely on heartbeats and leave the probe off.
* The Unity **Servers** screen lists verified servers and says plainly that joining is not connected yet. Matchmaking still reports "unavailable"; it never claims a server.

## 7. Tests actually executed

Run on the author's machine (Windows, Node 24, .NET 8), final run:

| Suite | Command | Result |
|---|---|---|
| Backend | `cd server; node --test --test-force-exit --no-warnings test/*.test.js` | **97 / 97 passed** |
| SQL (PGlite) | `cd tools\sql-tests; node --test --test-force-exit --no-warnings economy.test.mjs players.test.mjs social.test.mjs identity.test.mjs bridge.test.mjs` | **42 / 42 passed** |
| Client + real backend | `cd tools\core-tests; dotnet test` | **180 / 180 passed** |
| Unity-code compile harness | `dotnet build` in the harness project | **0 errors, 0 warnings** |
| Live-check script | against the fake + real Node (`--registry`, TTL 3 s) | ran to completion: 30 passed, 0 failed, 3 blocked (Google and Apple reported "NOT CONFIGURED", correct for the fake; network-failure injection is not scriptable). The first run exposed one script bug (fixed). **This proves the script works; it is not evidence about any real project.** |

**Your requested tests, and their honest status**

| Test | Evidence obtained | Status |
|---|---|---|
| **A** Registration | PGlite: trigger creates exactly one profile, 10-digit ID, ID stable (`bridge.test.mjs`, `players.test.mjs`). C#: real `AuthService` sign-up -> same Player ID in the game (`VerifiedSupabaseLogin...`). | Logic **passed on stand-ins**; real Supabase **not tested** |
| **B** Login / session | Invalid password, forged/garbage/expired/revoked tokens, outage (503, no session), restore after restart, refresh, revoked-session drop-to-signed-out: `supabase.test.js` and `SupabaseBridgeTests.cs`. | **Passed on stand-ins**; real **not tested**; network interruption of a live project **blocked** (cannot be induced by script) |
| **C** Profile persistence | Rename stored in Postgres (PGlite), read back, survives sign-out/in; lost-write detected. | **Passed on stand-ins**; real **not tested** |
| **D** Cross-device | Two independent sessions: same Player ID, one profile; rename visible on the other after the cache window. | **Passed on stand-ins**; real **not tested** |
| **E** Authorization | PGlite with the real policies: other player's row invisible and unmodifiable; `xp/level/coins/gems/player_id/user_id` updates refused; insert/delete refused; `grant_progress` refused; API ignores extra body fields. | **Passed on PGlite**; real **not tested** |
| **F** Server registry | Register -> not listed; heartbeat -> listed; wrong key/token refused; stale -> unlisted; purge; probe; restart rotation (`servers.test.js`, 8 tests + the C# list test). | **Passed over HTTP with a simulated game server**; **no real dedicated server exists**, so "start a configured dedicated test server" is **not tested** |
| **G** Regression | All suites above; offline startup and failure messages covered by the C# tests. | Passed; **never run inside the Unity Editor or on a device** |
| Google / Apple sign-in | The live-check preflight reports whether each provider is *enabled*. Credential correctness needs an interactive browser/native sign-in. | **Not tested** |
| Builds for PC/Android/iOS | Not run (no Unity here). | **Not tested** |

## 8. How to run the live check

```powershell
# disposable accounts only; use the anon/publishable key, never the service-role key
$env:SUPABASE_URL = "https://<ref>.supabase.co"
$env:SUPABASE_ANON_KEY = "<anon or publishable key>"
# optional: also prove the game backend accepts the same session (start it with the same SUPABASE_* variables)
$env:NOXY_SERVER_URL = "https://<your game backend>"
# optional: registry + heartbeat expiry (waits about one TTL)
$env:NOXY_MATCH_SERVER_KEY = "<server key>"
cd tools\supabase-live-check; node run.mjs --registry
```

It runs the A-E scenarios on your project, plus F (backend bridge) and G (registry) when configured. It writes `evidence-<time>.json` with no tokens, passwords or full e-mails. If e-mail confirmation is on, sign-up cannot return a session and those steps are reported **BLOCKED**: use a test project with "Confirm email" off, or the local Supabase CLI stack. The script cannot delete the accounts it creates; remove them in Dashboard > Authentication > Users.

## 9. Still required from you

1. A Supabase project; push the four migrations; confirm `players` RLS is on.
2. `Assets/_Project/Resources/SupabaseConfig.json` with the project URL and **anon/publishable** key (the file is git-ignored).
3. Dashboard: redirect URLs (`http://127.0.0.1:53682/auth-callback`, `noxy://auth-callback`), providers (Google, Apple, e-mail), optional anonymous sign-ins and manual linking, e-mail confirmation policy. Provider setup: [AUTH_SETUP.md](AUTH_SETUP.md).
4. Deploy the Node backend over **https** with `SUPABASE_URL`, `SUPABASE_ANON_KEY`, `NOXY_MATCH_SERVER_KEY` and a persistent, backed-up `DB_PATH`. Set the game-server address in Settings > Account (or ship it as the default).
5. Replace `PrefsSessionStore` with platform secure storage (gap #13) and deploy an account-deletion Edge Function (gap #14).
6. Run `tools/supabase-live-check`, then test Google and Apple by hand on real devices.
7. Decide whether to move the Node backend's storage to Supabase Postgres (the largest remaining item). Until then, gameplay data is in the backend's SQLite file, not in Supabase.
8. Build and run in Unity 6 on PC, Android and iOS (not done here).

## 10. Not verified

Real Supabase behaviour of every item above; Google and Apple login; email verification flow; account linking against real providers; a real dedicated server; the Unity Editor and device builds; multi-process/production load; the 60 s sign-out propagation window on the backend; secure token storage (known gap); data durability of the SQLite file.
