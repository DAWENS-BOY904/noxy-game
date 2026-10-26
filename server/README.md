# Noxy dev backend

A small development backend for the lobby: accounts, permanent 10-digit Player IDs, profiles, search, friends,
party invitations, presence, notifications, inventory/equip validation, settings and HUD-layout sync.

> **Development only.** It is plain HTTP, single process, SQLite, in-memory rate limiting, and has no email,
> password reset, or admin tooling. Put it behind TLS and replace the database before exposing it to the internet.

## Run

Requires Node 22.5+ (uses the built-in `node:sqlite`; no npm packages).

```powershell
cd server
node --no-warnings src/index.js          # http://127.0.0.1:8787, database file: noxy-dev.sqlite
$env:PORT=9000; $env:HOST="0.0.0.0"; node --no-warnings src/index.js   # reachable from a phone on your LAN
```

Environment: `PORT`, `HOST` (default `127.0.0.1`), `DB_PATH`.

### Account modes
| Mode | When | Behaviour |
|---|---|---|
| `supabase` | `SUPABASE_URL` and `SUPABASE_ANON_KEY` are set (or `NOXY_AUTH_MODE=supabase`) | The game signs in with Supabase and sends its access token. Every token is verified by Supabase itself (`GET /auth/v1/user`), the Player ID and username come from the user's own `players` row, and password register/login endpoints return 403. If Supabase cannot be reached the server answers 503; it never falls back to another identity. |
| `local` | neither is set | Dev-only username/password accounts stored (hashed) in SQLite. Never use on a public server. |

`SUPABASE_URL` must be https (loopback http is allowed for local testing). Only the **anon/publishable** key is used; the server warns if `SUPABASE_SERVICE_ROLE_KEY` is present in its environment and never reads it. `NOXY_SUPABASE_CACHE_SECONDS` (default 60) is how long a verified token is trusted before Supabase is asked again, which is also the worst-case delay before a sign-out elsewhere takes effect here.

### Dedicated-server registry
`NOXY_MATCH_SERVER_KEY` enables the game-server endpoints. `NOXY_SERVER_TTL_SECONDS` (default 30) is the heartbeat timeout; `NOXY_SERVER_PROBE=tcp` additionally requires a TCP connect to the registered address to succeed. See [docs/SUPABASE_AUDIT.md](../docs/SUPABASE_AUDIT.md#server-registry).

In the game: **Settings > Account**, enter the server address (for a phone use `http://<your-PC-LAN-IP>:8787`,
allow the port in the Windows firewall), then create an account.

## Test

```powershell
cd server
node --test --test-force-exit --no-warnings test/*.test.js     # 97 tests
```

The client library is also tested against this server: see `../tools/core-tests` (`dotnet test`).

## What is enforced server-side

- Player ID: 10 digits, first digit non-zero, `UNIQUE` + `CHECK` in the database, generated with `crypto.randomInt`, retried on collision. The ID is not a credential.
- Passwords: scrypt with per-user salt, constant-time compare, generic login errors, per-IP rate limit.
- Sessions: random 256-bit bearer tokens; only a SHA-256 hash is stored; 30-day expiry; logout deletes.
- Equipping: every slot/item is validated against the catalog and the player's inventory in one transaction. Clients cannot grant items or currency. `POST /api/shop/purchase` returns 501 on purpose.
- Social: no self/duplicate requests, only the recipient can accept/decline, blocks hide players both ways (404, indistinguishable from "not found"), privacy settings (friend requests, party invites, online status) are checked here, not in the client.
- Invitations: friends only, leader only, mode party-size rules, full-party and in-match checks, 15 s per-pair cooldown, max 5 pending, 60 s expiry with notifications.
- Search: exact ID or username (prefix, 3+ chars, max 10), rate limited, returns public fields only (never email/credentials).
- Input: 64 KB body limit, strict JSON object bodies, whitelisted setting keys/ranges and HUD element ids/ranges.

## API summary

All JSON. `Authorization: Bearer <token>` (a Supabase access token in `supabase` mode) except register/login/health.

| Area | Endpoints |
|---|---|
| Health | `GET /api/health` (no auth): `{ ok, authMode, supabaseConfigured }` |
| Auth | `POST /api/auth/register`, `/login` (local mode only), `/logout` |
| Servers | `GET /api/servers?mode=&region=` (players: only verified-online servers). Game servers, with `x-noxy-server-key`: `POST /api/internal/servers/register`, `/heartbeat`, `/deregister`, `GET /api/internal/servers` |
| Me | `GET/PATCH/DELETE /api/me`, `GET /api/me/inventory`, `PUT /api/me/loadout`, `GET/PUT /api/me/settings`, `GET/PUT /api/me/hud/:key` |
| Players | `GET /api/players/search?q=`, `GET /api/players/:publicId`, `POST /api/players/:publicId/report` |
| Friends | `GET /api/friends`, `POST /api/friends/requests`, `POST /api/friends/requests/:id/accept|decline|cancel`, `DELETE /api/friends/:publicId` |
| Blocks | `GET/POST /api/blocks`, `DELETE /api/blocks/:publicId` |
| Party | `GET /api/party`, `POST /api/party/invite`, `/invitations/:id/accept|decline`, `/leave`, `/ready`, `/mode` |
| Presence / notifications | `POST /api/presence`, `GET /api/notifications?after=`, `POST /api/notifications/read` |

Real-time updates are **polling** (the client polls every ~4 s and sends a heartbeat every 10 s). WebSockets are not implemented.

## Not implemented

Matchmaking and game servers, purchases, rewards/missions, email verification, password reset, OAuth/platform sign-in,
statistics updates from matches (the profile stats are zero for new accounts), voice chat.

## Moving to PostgreSQL

`src/schema.sql` is plain SQL with additive `CREATE TABLE IF NOT EXISTS`. For PostgreSQL: `INTEGER PRIMARY KEY AUTOINCREMENT` -> `BIGSERIAL`,
epoch-millisecond `INTEGER` columns -> `BIGINT`, `COLLATE NOCASE` on `username` -> `citext` or a `lower(username)` unique index,
`GLOB` check -> `~ '^[1-9][0-9]{9}$'`. The queries in `src/app.js` use `?` placeholders; switch to `$1…` with a Postgres driver.
Use a migration tool and never drop data in migrations.
