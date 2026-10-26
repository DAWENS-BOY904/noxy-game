# Economy setup (wallet, store, Booyah, missions)

**Status: NOT production-ready.** Implemented and tested against the Node dev backend and PGlite only. Nothing has been run in Unity, against real Apple/Google billing, a real ad provider, a real Supabase project or a dedicated game server.

## What exists
- Server-authoritative wallet (coins + diamonds) with an idempotent ledger. Clients can only read it.
- Missions (daily/weekly/achievement) with server-side claims and daily coin caps.
- Booyah: configurable promotional **in-game** reward (default 500 diamonds). It has no cash value and cannot be withdrawn. It is granted only by the trusted game server.
- Diamond packages (configurable), payment states (pending/succeeded/cancelled/failed/refunded), purchase history.
- Cosmetic purchases with coins or diamonds, priced by the server.
- Rewarded ads: disabled until `NOXY_ADS_SECRET` is set; generic signed-callback adapter only.

## Environment variables (server only, never in Unity)
| Variable | Purpose |
|---|---|
| `NOXY_MATCH_SERVER_KEY` | Shared secret the dedicated server sends as `x-noxy-server-key` to `POST /api/internal/match-results`. Unset = endpoint returns 503. |
| `NOXY_TEST_PAYMENTS=1` | Enables sandbox `test-confirm` purchases. Never enable in production. |
| `NOXY_WEBHOOK_SECRET_APPLE/GOOGLE/TEST` | HMAC secrets for `POST /api/payments/webhook/:provider` (`x-noxy-signature` over the raw body). |
| `NOXY_ADS_SECRET` | Enables the rewarded-ad callback. |

## Operator settings
Edit the `economy_settings` table: Booyah (`enabled`, `diamonds`, `min_players`, `min_seconds`, `daily_cap`, `modes`), match coins, `coin_daily_cap`, training and ad rewards. Packages live in `diamond_packages`.

## Match results
The dedicated server posts the final result per player with its key. Replays are idempotent: one reward per player per match (unique constraint). Flagged players' rewards are held.

## Still required before launch
1. Real receipt verification: implement `config.economy.verifiers.apple` (App Store Server API) and `.google` (Play Developer API) and register Apple/Google products matching `noxy.diamonds.N`. Prices must come from the store SDK, not the reference prices.
2. Unity IAP integration on the client (currently the Shop uses the sandbox test flow only).
3. A dedicated game server that posts match results.
4. Ad provider server-side verification, consent flow and platform privacy review.
5. Supabase: apply `supabase/migrations/20250201000000_economy.sql` and port the Node routes to Edge Functions calling `wallet_apply`, `grant_booyah`, `settle_payment` with the service-role key (server side only). The migration is tested in PGlite only; cosmetic purchase and mission logic are not yet ported to SQL.
6. Legal review of the promotional reward rules, refunds and regional consumer-law requirements.

## Tests
- `cd server; node --test --no-warnings test/*.test.js`
- `cd tools\sql-tests; node --test economy.test.mjs players.test.mjs`
- Covered: forged victories, duplicate and concurrent claims, failed/forged payments, replays, restart persistence, negative balance, RLS.
