'use strict';
// Supabase identity for the game backend.
//
// The game client signs in with Supabase Auth and sends its access token (JWT) to this server. We never trust the token
// locally: every token is verified by Supabase itself (GET /auth/v1/user), which checks signature, expiry and that the
// session was not revoked. The canonical Player ID / username come from the user's own `public.players` row, read with
// the USER'S token and the public anon key, so RLS applies and no service-role key is needed here.
// If Supabase cannot be reached we fail with 503. There is no fallback that pretends the user is signed in.
const crypto = require('node:crypto');

const JWT_RE = /^[A-Za-z0-9_-]{10,2048}\.[A-Za-z0-9_-]{10,4096}\.[A-Za-z0-9_-]{10,2048}$/;
const UUID_RE = /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i;
const PLAYER_ID_RE = /^[1-9][0-9]{9}$/;
const USERNAME_RE = /^[A-Za-z0-9_]{3,20}$/;

class SupabaseError extends Error {
  constructor(status, code, message) { super(message); this.status = status; this.code = code; }
}

const sha256 = s => crypto.createHash('sha256').update(s).digest('hex');

function create({ url, anonKey, fetchImpl, now = () => Date.now(), cacheMs = 60_000, timeoutMs = 6000 } = {}) {
  const base = String(url || '').replace(/\/+$/, '');
  const doFetch = fetchImpl || ((...a) => fetch(...a));
  // Tokens and the anon key must not travel in clear text: require https, except for a loopback address (dev/tests).
  const enabled = /^(https:\/\/[^\s/]+|http:\/\/(127\.0\.0\.1|localhost|\[::1\])(:\d+)?)(\/|$)/.test(base) && !!anonKey;
  const cache = new Map(); // sha256(token) -> { userId, isAnonymous, profile, exp, checkedAt }

  function payloadOf(token) {
    try { return JSON.parse(Buffer.from(token.split('.')[1], 'base64url').toString('utf8')); } catch { return null; }
  }

  async function request(method, path, token, body, extraHeaders = {}) {
    const ctl = new AbortController();
    const timer = setTimeout(() => ctl.abort(), timeoutMs);
    try {
      const res = await doFetch(base + path, {
        method, signal: ctl.signal,
        headers: { apikey: anonKey, Authorization: 'Bearer ' + token, Accept: 'application/json', ...(body !== undefined ? { 'Content-Type': 'application/json' } : {}), ...extraHeaders },
        body: body !== undefined ? JSON.stringify(body) : undefined,
      });
      const text = await res.text();
      let json = null;
      try { json = text ? JSON.parse(text) : null; } catch { /* non-JSON error page */ }
      return { status: res.status, json };
    } catch {
      throw new SupabaseError(503, 'auth_unavailable', 'The account service could not be reached. Please try again.');
    } finally { clearTimeout(timer); }
  }

  const looksLikeJwt = token => typeof token === 'string' && JWT_RE.test(token);

  async function fetchProfile(token, userId) {
    let r = await request('GET', `/rest/v1/players?select=player_id,username,avatar_url,level,xp&user_id=eq.${userId}`, token);
    if (r.status >= 500 || r.status === 0) throw new SupabaseError(503, 'auth_unavailable', 'The account service is unavailable.');
    if (r.status === 401 || r.status === 403) throw new SupabaseError(401, 'invalid_token', 'Your session is no longer valid. Please sign in again.');
    if (r.status !== 200 || !Array.isArray(r.json)) throw new SupabaseError(502, 'profile_unavailable', 'Your profile could not be loaded from the account service.');
    let row = r.json[0];
    if (!row) {
      // The sign-up trigger normally created it already. ensure_player() is idempotent, so this can never duplicate.
      r = await request('POST', '/rest/v1/rpc/ensure_player', token, {});
      if (r.status !== 200 || !r.json) throw new SupabaseError(502, 'profile_unavailable', 'Your profile could not be created by the account service.');
      row = Array.isArray(r.json) ? r.json[0] : r.json;
    }
    if (!row || !PLAYER_ID_RE.test(String(row.player_id)) || !USERNAME_RE.test(String(row.username))) {
      throw new SupabaseError(502, 'profile_invalid', 'The account service returned an invalid profile.');
    }
    return { playerId: String(row.player_id), username: String(row.username), avatarUrl: row.avatar_url || null, level: Number(row.level) || 1, xp: Number(row.xp) || 0 };
  }

  /** Verifies the token with Supabase and returns { userId, isAnonymous, profile, fresh }. Throws SupabaseError. */
  async function identify(token) {
    if (!enabled) throw new SupabaseError(503, 'supabase_not_configured', 'Supabase is not configured on this server (SUPABASE_URL / SUPABASE_ANON_KEY).');
    if (!looksLikeJwt(token)) throw new SupabaseError(401, 'invalid_token', 'Sign in required.');
    const key = sha256(token);
    const t = now();
    const hit = cache.get(key);
    if (hit && t - hit.checkedAt < cacheMs && t < hit.exp) return { ...hit, fresh: false };
    cache.delete(key);

    const payload = payloadOf(token);
    if (!payload || !UUID_RE.test(String(payload.sub || ''))) throw new SupabaseError(401, 'invalid_token', 'Sign in required.');
    const exp = Number(payload.exp) * 1000;
    if (!(exp > t)) throw new SupabaseError(401, 'token_expired', 'Your session expired. Please sign in again.');

    const r = await request('GET', '/auth/v1/user', token);
    if (r.status === 401 || r.status === 403) throw new SupabaseError(401, 'invalid_token', 'Your session is no longer valid. Please sign in again.');
    if (r.status !== 200 || !r.json) throw new SupabaseError(r.status >= 500 || r.status === 0 ? 503 : 502, 'auth_unavailable', 'The account service could not verify your session.');
    const user = r.json;
    if (String(user.id).toLowerCase() !== String(payload.sub).toLowerCase()) throw new SupabaseError(401, 'invalid_token', 'Session mismatch.');

    const profile = await fetchProfile(token, user.id);
    const entry = { userId: String(user.id).toLowerCase(), isAnonymous: user.is_anonymous === true, profile, exp, checkedAt: t };
    cache.set(key, entry);
    if (cache.size > 5000) for (const k of cache.keys()) { cache.delete(k); if (cache.size <= 4000) break; }
    return { ...entry, fresh: true };
  }

  /** Changes the username in Supabase with the user's own token (RLS applies), then reads it back to confirm it persisted. */
  async function updateUsername(token, userId, username) {
    if (!USERNAME_RE.test(username)) throw new SupabaseError(400, 'invalid_username', 'Username must be 3-20 letters, numbers or underscores.');
    const r = await request('PATCH', `/rest/v1/players?user_id=eq.${userId}`, token, { username }, { Prefer: 'return=representation' });
    if (r.status === 409 || (r.json && r.json.code === '23505')) throw new SupabaseError(409, 'username_taken', 'That username is already taken.');
    if (r.status === 400 || (r.json && r.json.code === '23514')) throw new SupabaseError(400, 'invalid_username', 'That username is not allowed.');
    if (r.status === 401 || r.status === 403) throw new SupabaseError(401, 'invalid_token', 'Your session is no longer valid. Please sign in again.');
    if (r.status >= 500 || r.status === 0) throw new SupabaseError(503, 'auth_unavailable', 'The account service is unavailable.');
    if (r.status !== 200 && r.status !== 204) throw new SupabaseError(502, 'update_failed', 'The account service rejected the update.');
    const after = await fetchProfile(token, userId);
    if (after.username !== username) throw new SupabaseError(502, 'update_not_persisted', 'The account service did not save the new username.');
    for (const [k, v] of cache) if (v.userId === String(userId).toLowerCase()) cache.delete(k);
    return after;
  }

  const forget = token => { if (typeof token === 'string') cache.delete(sha256(token)); };

  return { enabled, looksLikeJwt, identify, updateUsername, forget, SupabaseError };
}

module.exports = { create, SupabaseError, JWT_RE };
