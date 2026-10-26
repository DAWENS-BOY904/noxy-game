'use strict';
// TEST TOOLING ONLY. A tiny in-memory stand-in for Supabase GoTrue + PostgREST, used by the C# integration tests so
// they can exercise the real AuthService/AccountService against the real Node backend without a Supabase project.
// It is NOT Supabase: it proves our client/server logic, not provider setup, RLS on a real database or the network.
const http = require('node:http');
const crypto = require('node:crypto');

const ANON = process.env.FAKE_ANON_KEY || 'test-anon-key-0123456789abcdef';
const users = new Map();      // id -> { id, email, password, is_anonymous, profile }
const byEmail = new Map();
const access = new Map();     // access token -> user id
const refresh = new Map();    // refresh token -> user id
let down = false;
let seq = 0;

const b64 = o => Buffer.from(JSON.stringify(o)).toString('base64url');
function mint(user, ttl = 3600) {
  const exp = Math.floor(Date.now() / 1000) + ttl;
  const at = `${b64({ alg: 'ES256', typ: 'JWT' })}.${b64({ sub: user.id, exp, role: 'authenticated', is_anonymous: user.is_anonymous })}.${crypto.randomBytes(24).toString('base64url')}`;
  const rt = crypto.randomBytes(12).toString('base64url');
  access.set(at, user.id); refresh.set(rt, user.id);
  return { access_token: at, refresh_token: rt, expires_in: ttl, token_type: 'bearer', user: publicUser(user) };
}
const publicUser = u => ({ id: u.id, email: u.email || '', is_anonymous: u.is_anonymous, identities: u.email ? [{ provider: 'email' }] : [] });
const newId = () => String(1000000000 + ((++seq * 7919 + 13) % 8999999999));

function newUser(email, password, anonymous) {
  const id = crypto.randomUUID();
  const u = { id, email, password, is_anonymous: anonymous, profile: { user_id: id, player_id: newId(), username: 'Operator_' + seq, avatar_url: null, level: 1, xp: 0 } };
  users.set(id, u); if (email) byEmail.set(email.toLowerCase(), u);
  return u;
}

http.createServer((req, res) => {
  let raw = '';
  req.on('data', c => raw += c);
  req.on('end', () => {
    const u = new URL(req.url, 'http://x');
    const body = raw ? JSON.parse(raw) : {};
    const reply = (status, obj) => { res.writeHead(status, { 'Content-Type': 'application/json' }); res.end(obj === undefined ? '' : JSON.stringify(obj)); };
    const token = (req.headers.authorization || '').replace('Bearer ', '');
    const bearerUser = () => { const id = access.get(token); return id ? users.get(id) : null; };

    // control channel for the tests
    if (u.pathname === '/__test/down') { down = !!body.down; return reply(200, { down }); }
    if (u.pathname === '/__test/revoke') { for (const [k, v] of access) if (v === body.userId) access.delete(k); for (const [k, v] of refresh) if (v === body.userId) refresh.delete(k); return reply(200, {}); }
    if (u.pathname === '/__test/profile') { const x = users.get(body.userId); return reply(x ? 200 : 404, x ? x.profile : {}); }

    if (down) return reply(503, { msg: 'service unavailable' });
    if (req.headers.apikey !== ANON) return reply(401, { message: 'No API key found in request' });

    if (u.pathname === '/auth/v1/settings') return reply(200, { external: { email: true, google: false, apple: false }, disable_signup: false, mailer_autoconfirm: true });
    if (u.pathname === '/auth/v1/signup') {
      if (!body.email) return reply(200, mint(newUser(null, null, true)));
      if (byEmail.has(String(body.email).toLowerCase())) return reply(422, { error_code: 'user_already_exists', msg: 'User already registered' });
      if (String(body.password || '').length < 8) return reply(422, { error_code: 'weak_password', msg: 'Password should be at least 8 characters.' });
      return reply(200, mint(newUser(body.email, body.password, false)));
    }
    if (u.pathname === '/auth/v1/token') {
      const grant = u.searchParams.get('grant_type');
      if (grant === 'password') {
        const user = byEmail.get(String(body.email || '').toLowerCase());
        if (!user || user.password !== body.password) return reply(400, { error_code: 'invalid_credentials', msg: 'Invalid login credentials' });
        return reply(200, mint(user));
      }
      if (grant === 'refresh_token') {
        const id = refresh.get(body.refresh_token);
        if (!id) return reply(400, { error_code: 'refresh_token_not_found', msg: 'Invalid Refresh Token: Refresh Token Not Found' });
        refresh.delete(body.refresh_token); // rotation
        return reply(200, mint(users.get(id)));
      }
      return reply(400, { msg: 'unsupported grant' });
    }
    if (u.pathname === '/auth/v1/logout') { const user = bearerUser(); if (user) { access.delete(token); } return reply(204); }
    if (u.pathname === '/auth/v1/user') {
      const user = bearerUser();
      return user ? reply(200, publicUser(user)) : reply(401, { code: 401, error_code: 'bad_jwt', msg: 'invalid JWT' });
    }
    if (u.pathname === '/rest/v1/rpc/ensure_player') {
      const user = bearerUser();
      return user ? reply(200, user.profile) : reply(401, { message: 'JWT expired', code: 'PGRST301' });
    }
    if (u.pathname === '/rest/v1/rpc/find_player') {
      const user = bearerUser();
      if (!user) return reply(401, { message: 'JWT expired', code: 'PGRST301' });
      const hit = [...users.values()].find(x => x.profile.player_id === body.p_player_id);
      return reply(200, hit ? [{ player_id: hit.profile.player_id, username: hit.profile.username, avatar_url: hit.profile.avatar_url, level: hit.profile.level }] : []);
    }
    if (u.pathname === '/rest/v1/rpc/grant_progress') return reply(403, { code: '42501', message: 'permission denied for function grant_progress' });
    if (u.pathname === '/rest/v1/players') {
      const user = bearerUser();
      if (!user) return reply(401, { message: 'JWT expired', code: 'PGRST301' });
      const wanted = (u.searchParams.get('user_id') || '').replace(/^eq\./, '');
      if (req.method === 'GET') return reply(200, wanted === user.id ? [user.profile] : []); // RLS: own row only
      if (req.method === 'PATCH') {
        if (wanted !== user.id) return reply(200, []);
        if (Object.keys(body).some(k => k !== 'username' && k !== 'avatar_url')) return reply(403, { code: '42501', message: 'permission denied' });
        if (body.username !== undefined) {
          if (!/^[A-Za-z0-9_]{3,20}$/.test(body.username)) return reply(400, { code: '23514', message: 'check violation' });
          for (const o of users.values()) if (o.id !== user.id && o.profile.username.toLowerCase() === body.username.toLowerCase()) return reply(409, { code: '23505', message: 'duplicate key' });
        }
        if (body.avatar_url !== undefined && body.avatar_url !== null && !/^https:\/\/\S+$/.test(body.avatar_url)) return reply(400, { code: '23514', message: 'check violation' });
        Object.assign(user.profile, body);
        return reply(200, [user.profile]);
      }
    }
    return reply(404, { msg: 'not found' });
  });
}).listen(Number(process.env.PORT || 0), '127.0.0.1', function () { console.log('READY ' + this.address().port); });
