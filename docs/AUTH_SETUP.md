# Account system setup (Supabase Auth + PostgreSQL)

**Status: not tested against a real Supabase project, Google or Apple.** See [SUPABASE_AUDIT.md](SUPABASE_AUDIT.md) for the full audit, what was repaired, exactly which stand-ins the tests used, and `tools/supabase-live-check` to verify your real project.
What *is* tested: the SQL migration (16 tests on PGlite with a stubbed `auth` schema) and the C# auth state machine
(43 tests against a simulated GoTrue/PostgREST). Provider login must be verified by you with real credentials.

## Existing state found
No Supabase code, config or migrations existed before this work. Nothing was reused or replaced.

## 1. Database
```
supabase link --project-ref <ref>
supabase db push
```
Applies `supabase/migrations/20250101000000_players.sql`: `players` table (unique 10-digit `player_id`, unique username),
RLS (users read own row, update only `username`/`avatar_url`), an `auth.users` trigger that creates the profile exactly
once, `ensure_player()`, rate-limited `find_player()`, and `grant_progress()` for `service_role` only.

## 2. Client config
Copy `Assets/_Project/Resources/SupabaseConfig.example.json` to `SupabaseConfig.json` and fill in the project URL and the
**anon** key. Never put the service_role key in the client.

## 3. Supabase dashboard
- Authentication > URL Configuration > Redirect URLs, add both:
  - `http://127.0.0.1:53682/auth-callback` (Windows / Editor loopback)
  - `noxy://auth-callback` (Android / iOS)
- Authentication > Sign In / Providers: enable **Google** and **Apple**; enable **Anonymous sign-ins** (guest);
  enable **Manual linking**; keep e-mail provider on.
- Optionally enforce e-mail confirmation.

## 4. Google Cloud
- OAuth consent screen configured.
- Create a **Web** OAuth client; set its authorized redirect URI to `https://<ref>.supabase.co/auth/v1/callback`;
  paste client ID and secret into Supabase's Google provider.
- Native Google sign-in (optional, needs a plugin implementing `INativeSignIn`): also create Android (package + SHA-1)
  and iOS client IDs and add them to Supabase "Authorized Client IDs".

## 5. Apple Developer
- App ID with *Sign in with Apple*; a **Services ID** (web) with return URL `https://<ref>.supabase.co/auth/v1/callback`.
- A Sign in with Apple **key** (.p8); generate the client secret JWT from it (it expires at most every 6 months, so rotate).
- Enter Services ID, Team ID, Key ID and secret in Supabase's Apple provider.
- Apple may return a private relay e-mail; it is stored as-is and never used as the account identifier. Apple only
  sends the name on first authorization; the game does not rely on it.

## 6. Mobile deep link
Register the `noxy` URL scheme in Player Settings (iOS: URL Types; Android: intent filter for `noxy://auth-callback`).

## Known limits
- Browser PKCE flow is used on every platform. Native Apple/Google need an `INativeSignIn` plugin (hook exists, no implementation).
- `PrefsSessionStore` uses PlayerPrefs (**not secure**); replace with Keychain/Keystore before release.
- The game backend (`server/`) now verifies Supabase tokens: start it with `SUPABASE_URL` and `SUPABASE_ANON_KEY` (anon key only) and it adopts the Player ID from the Supabase profile. Gameplay data (wallet, inventory, friends, clans...) is stored in the backend's own database, not in Supabase Postgres.
- Account deletion for Supabase users is not available in-game (needs a service-role Edge Function).
- Linking never merges by e-mail; if the other identity already has an account, linking is refused and the user must sign in to it.
