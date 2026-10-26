# Production-readiness audit: persistence and Supabase migration plan

Scope: Unity client + Node backend (`server/`). Read-only audit. **No migration was run, no account was created, no data was written to Supabase.** No secret or service-role key was read, printed or stored.

## 1. What was actually verified

| Check | Result |
|---|---|
| Backend tests (`node --test`, 116 tests) | Pass, but against SQLite + a fake Supabase + PGlite. Not evidence of real Supabase. |
| Read-only probe of project `llilhpvjgcgjloncplxl` (GET only, anon key) | Reachable. Key role is `anon`, matches the project, not expired. |
| `/auth/v1/settings` | Email provider on, Google/Apple off, signup open, **email auto-confirm off**, anonymous sign-in off. |
| Anonymous `GET` on `players`, `player_wallets`, `clans`, `player_reports`, `audit_log`, `rate_limits` | HTTP 401 / `42501` (permission denied). The tables **exist** and are not readable by anon. This is consistent with the repo's migrations 1-3 being applied and RLS/grants working. It is not proof of every policy. |
| Anonymous `GET match_performance` | 404 `PGRST205`: table does not exist in Supabase (expected, it only exists in SQLite). |
| Real sign-up / login / profile / settings / history / cross-session test | **NOT RUN.** Sign-up would create accounts in the live project (needs your approval) and email confirmation is on, so the existing `tools/supabase-live-check` cannot complete without a confirmable mailbox or a dedicated test project. |

Conclusion: **real Supabase integration is not verified.** Only connectivity and "anon is denied on private tables" are verified.

Secret hygiene: `.gitignore` already excluded `*.env`, `*.sqlite*` and the local `SupabaseConfig.json`. This audit added `.env.*` (with `!.env.example`) and `*.db*`. A repo scan found no `sb_secret_`, JWT or project URL committed (one unit-test fixture uses a fake `sb_secret_` string to prove the client rejects it). `SUPABASE_SECRET_KEY` / `SUPABASE_SERVICE_ROLE_KEY` are not read by any code; the server warns if the service-role variable is set. Env vars `SUPABASE_URL` / `SUPABASE_ANON_KEY` were **not set** in this shell, so the probe used the key you pasted, scoped to a single process and never printed. Because the secret key was also pasted in chat, rotate it.

## 2. Persistence inventory

### Backed by Supabase today (real, per code)
| Data | Table / service | Notes |
|---|---|---|
| Accounts, sessions, refresh tokens | Supabase Auth | Server verifies every token via `GET /auth/v1/user` (`supabaseauth.js`). |
| Player ID, username, avatar, (informational) level/xp | `public.players` | Read/written with the **user's own token** under RLS. Supabase owns the Player ID. |

### Persisted in SQLite only (`DB_PATH`, default `noxy-dev.sqlite`)
The Supabase migrations define Postgres equivalents for some of these, but **nothing writes to them**.

| Area | SQLite tables | Postgres table exists in migrations? |
|---|---|---|
| Identity mirror | `players` (public_id, credits, cores, rank, local password fields), `sessions` | Partly (`players`); password/session tables are dev-only |
| Statistics, inventory, equipment | `player_statistics`, `player_inventory`, `player_equipped_items` | No |
| Settings | `player_settings`, `player_hud_layouts` | No |
| Social | `friend_requests`, `friendships`, `player_blocks`, `parties`, `party_members`, `party_invitations`, `presence`, `notifications`, `reports` | No |
| Wallet / economy | `player_wallets`, `currency_transactions`, `economy_settings`, `diamond_packages`, `payment_transactions`, `cosmetic_catalog` | Yes (migration 2) |
| Match results | `matches_processed`, `match_rewards` | `match_rewards` only |
| **Analytics** | `match_performance` | **No** |
| Missions / rewards | `daily_missions`, `player_mission_progress`, `reward_claims`, `ad_reward_events` | No |
| Clans | `clans`, `clan_members`, `clan_invitations`, `clan_applications`, `clan_messages`, `clan_reports` | Yes except messages/reports |
| Clan wars | `clan_war_events`, `_registrations`, `_roster`, `_queue`, `_queue_players`, `clan_matches`, `clan_match_players`, `clan_match_results`, `clan_match_disputes` | Only matches/results |
| Companions | `companion_catalog`, `companion_variants`, `player_companions`, `player_companion_equipped`, `companion_xp_events` | Yes except xp_events |
| Map events | `map_definitions`, `map_event_configs`, `map_events`, `map_event_roster`, `map_event_progress`, `map_event_reports`, `map_event_claims` | Partly |
| Fair play | `moderators`, `player_reputation`, `reputation_events`, `moderation_actions`, `appeals`, `audit_log` | Yes (migration 3) |
| Anti-cheat | `cheat_strikes` | No |
| Server registry | `game_servers` | No (operational data) |

### In memory only (lost on restart)
Battle Royale match state: positions, loot, shops, in-match coins, pickup state, purchase idempotency keys (`server/src/br.js`). The match-end result is persisted via `processMatch` (SQLite). Purchases and pickups are intentionally match-only.

### On the device
| Data | Where | Risk |
|---|---|---|
| Supabase session / refresh token | `PlayerPrefs` plaintext (`PrefsSessionStore`, `PrefsTokenStore`) | **Release blocker:** move to Keychain / Keystore / Credential Manager. |
| Dev server URL | `PlayerPrefs` | Low |
| Auto-rotate, simulate-error flag | `PlayerPrefs` (`LobbySettings`) | Low, cosmetic |
| Outfit Looks (Vault) | `PlayerPrefs` | Not synced across devices |
| Training best score | `PlayerPrefs` | Not synced, non-competitive |
| Offline profile | local JSON file (`LocalProfileService`, `LocalConfigStore`) | Labelled offline mode, holds no account data |

## 3. Architectural finding that shapes the migration

The Node backend uses synchronous `node:sqlite` transactions throughout (`db.exec('BEGIN')`, `INSERT OR IGNORE`, `INTEGER` ids, `strftime`). Postgres access is asynchronous and uses different SQL. A line-by-line port of ~60 tables would be a rewrite of every module, so the migration needs a **data-access layer**, not a find-and-replace. Also, the backend only holds the user's JWT and anon key. It has **no privileged credential** by design, so it cannot write wallet, match, or reward rows under RLS (and must not use a service-role key from a game server that can be compromised).

## 4. Recommended migration plan

Goal: Supabase Postgres is the system of record for persistent player data; the trusted game server remains the only writer of authoritative values; the client never writes them.

### Decision needed (not made for you)
How does the trusted server write authoritative rows without a service-role key in the client or in shared code?
- **Option A (recommended): SECURITY DEFINER RPCs.** One narrow function per operation (`apply_match_result(match_id, ...)`, `purchase_item(...)`, `grant_reward(...)`). Execute is granted only to a dedicated `game_server` Postgres role, authenticated by a **server-only JWT/credential kept in the server's secret store** (never in the repo, never in the client). Each function is idempotent, validates its inputs and writes the ledger and audit rows in one transaction.
- Option B: direct Postgres connection from the trusted server with a least-privilege role. Simpler SQL, but needs a connection string secret and a Postgres driver.
- Option C (rejected): service-role key on the server. It bypasses RLS entirely, and the existing design deliberately refuses it.

Either A or B means the **server** holds one scoped secret, which is a different thing from the Unity client holding one. Pick before Phase 2.

### Phases (each additive and reversible)
**Phase 0 - Test environment.** Create a separate Supabase project (or local `supabase start`) for testing. Set email auto-confirm on there. Run `tools/supabase-live-check` and `tools/sql-tests` against it. Do not use production for this.

**Phase 1 - Close schema gaps.** New migrations (never edit applied ones): `20250501_stats_inventory_settings.sql`, `20250601_match_performance_missions.sql`, `..._social.sql`, `..._clanwars_mapevents_gaps.sql`. Key everything by `user_id uuid references auth.users` (or `players.player_id`) instead of SQLite's integer id. Add: foreign keys, CHECK constraints copied from SQLite, unique `(match_id, player_id)` on `match_performance`, indexes (`(player_id, ended_at desc)`), RLS on every table (owner `select` only for private data; **no client `insert/update/delete` on stats, wallet, match, inventory, mission, reward tables**), and `revoke all` from `anon`/`authenticated` on server-only tables. Extend `tools/sql-tests` to assert each policy.

**Phase 2 - Data-access layer.** Introduce a `Store` interface in `server/src` (async) with `SqliteStore` (existing behaviour, default) and `PostgresStore`. Port one vertical at a time, starting with the lowest risk: settings -> inventory/equipment -> statistics + `match_performance` -> wallet/ledger -> missions/rewards -> social -> clans/wars/companions/events -> fair play. Money paths go last and use the RPC from Option A.

**Phase 3 - Identity mapping.** Add `auth_user_id uuid` to the SQLite `players` row (already adopted from Supabase for Supabase-mode accounts). The migration key is `(Supabase user_id, player_id)`. Local-mode dev accounts have no Supabase identity and are **not** migrated.

**Phase 4 - Data migration (no loss).** Write `tools/migrate-sqlite-to-supabase` that:
1. opens SQLite **read-only** after a file copy backup;
2. runs in `--dry-run` by default, printing row counts per table and rejected rows (no values);
3. maps SQLite player rows to Supabase users by `public_id = players.player_id`; unmapped rows are reported, not dropped;
4. inserts with `ON CONFLICT DO NOTHING` keyed on natural ids (`match_id`, `request_id`, `transaction id`), so re-running is idempotent;
5. migrates ledgers as-is, then **reconciles**: `sum(currency_transactions) = wallet balance` per player, and row counts match before cutover;
6. is never destructive: SQLite stays untouched as the rollback.

**Phase 5 - Dual-run and cutover.** Shadow-write to Postgres while reading from SQLite; compare; switch reads per vertical behind `NOXY_STORE_<AREA>=postgres`; keep SQLite as fallback for one release.

**Phase 6 - Client.** Replace plaintext token storage with platform secure storage. Optionally sync Looks and best score through `player_settings`. Implement account deletion via an Edge Function (Apple requirement).

### Authorization rules to hold through the migration
- Client may read its own rows only (`user_id = auth.uid()`); public data (leaderboards, clan profiles) via narrow views exposing only public columns.
- Client may never write: wallet, ledger, statistics, match rows, rewards, inventory grants, reputation, moderation.
- Clan membership/role changes, purchases, reports and appeals go through server-validated endpoints/RPCs with rate limits (the Node rate limiter must be replaced by `rate_limits` in Postgres once the process is horizontally scaled).
- BR purchases and pickups stay in the authoritative server's memory; only final results are persisted, via an idempotent `match_id` RPC.

## 5. Remaining work and risks
1. Real Supabase sign-up/login/profile/username/cross-device tests: **blocked pending your approval** and a test project.
2. No Postgres backend exists for stats, match history, analytics, settings, wallet, social, clans, wars, companions, events or fair play. Today they are SQLite on one disk, with no replication; **back up the file** and keep it on persistent storage.
3. Supabase email confirmation is enabled in the probed project; the live-check needs a test project with auto-confirm.
4. Unity C# for the analytics screen and BR shop is uncompiled (no .NET SDK / Unity run).
5. Plaintext refresh token in `PlayerPrefs`; no in-app account deletion.
6. Match state and shop idempotency keys do not survive a server restart; a crash mid-match loses the match.
7. The BR transport is HTTP polling, not real-time netcode, and no dedicated game server binary exists in the repo.
8. Rotate the pasted `sb_secret_...` key.

## 6. Deployment plan
1. Rotate the exposed secret key. Do not commit or paste keys.
2. Create a **test** Supabase project; apply `supabase/migrations/*` there (`supabase db push` against the test ref); run `tools/sql-tests` and `tools/supabase-live-check` with `SUPABASE_URL` / `SUPABASE_ANON_KEY` from the environment.
3. Review the diff between the migrations and production (`supabase db diff`/`migration list`). Production shows tables 1-3 present; confirm 4 (`identity_hardening`) is applied. **Await your explicit approval before touching production.**
4. Back up production (point-in-time recovery or `pg_dump`), then apply any new migrations with `supabase db push`. Never use `db reset`.
5. Deploy the Node backend with `SUPABASE_URL`, `SUPABASE_ANON_KEY`, `NOXY_AUTH_MODE=supabase`, `DB_PATH` on persistent storage, HTTPS in front, and secrets (if Option A/B) in the host's secret manager.
6. Ship the client with `SupabaseConfig.json` containing only the URL and anon/publishable key, and secure token storage.
7. Run the migration tool in `--dry-run`, reconcile, then cut over one vertical at a time per Phase 5.
8. Monitor 4xx/5xx and rate-limit counters; keep SQLite as rollback for one release.
