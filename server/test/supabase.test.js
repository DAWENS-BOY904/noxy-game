'use strict';
// Supabase identity bridge: failure modes and invariants. The Supabase side here is a minimal in-memory TEST DOUBLE
// (isolated to this file); tools/sql-tests/bridge.test.mjs runs the same bridge against the real SQL migration.
const test = require('node:test');
const assert = require('node:assert/strict');
const crypto = require('node:crypto');
const http = require('node:http');
const { createApp } = require('../src/app');

const URL_ = 'https://fake-project.supabase.co';
const uuid = () => crypto.randomUUID();
const b64 = o => Buffer.from(JSON.stringify(o)).toString('base64url');
const jwt = (sub, exp = Math.floor(Date.now() / 1000) + 3600) => `${b64({ alg: 'ES256', typ: 'JWT' })}.${b64({ sub, exp, role: 'authenticated' })}.${crypto.randomBytes(24).toString('base64url')}`;

function fakeSupabase() {
  const st = { users: new Map(), profiles: new Map(), revoked: new Set(), down: false, calls: 0, ignoreUpdates: false, profileCalls: 0 };
  const nextId = () => String(1000000000 + st.profiles.size * 7919 + 13);
  st.signUp = (anonymous = false) => {
    const id = uuid();
    st.users.set(id, { id, is_anonymous: anonymous });
    st.profiles.set(id, { user_id: id, player_id: nextId(), username: 'Operator_' + nextId(), avatar_url: null, level: 1, xp: 0 });
    return { id, token: jwt(id, Math.floor(Date.now() / 1000) + 3 * 24 * 3600) };
  };
  st.fetch = async (url, init) => {
    st.calls++;
    if (st.down) throw new Error('network down');
    const u = new URL(url);
    const token = (init.headers.Authorization || '').replace('Bearer ', '');
    const sub = JSON.parse(Buffer.from(token.split('.')[1], 'base64url')).sub;
    const reply = (status, body) => ({ status, text: async () => (body === undefined ? '' : JSON.stringify(body)) });
    if (init.headers.apikey !== 'anon-key') return reply(401, { message: 'no api key' });
    if (u.pathname === '/auth/v1/user') {
      if (st.revoked.has(token) || !st.users.has(sub)) return reply(401, { msg: 'invalid JWT' });
      return reply(200, st.users.get(sub));
    }
    if (u.pathname === '/rest/v1/players') {
      st.profileCalls++;
      const row = st.profiles.get(sub);
      if (init.method === 'GET') return reply(200, row ? [{ player_id: row.player_id, username: row.username, avatar_url: row.avatar_url, level: row.level, xp: row.xp }] : []);
      if (init.method === 'PATCH') {
        const { username } = JSON.parse(init.body);
        if ([...st.profiles.values()].some(p => p.user_id !== sub && p.username.toLowerCase() === username.toLowerCase())) return reply(409, { code: '23505' });
        if (!st.ignoreUpdates) row.username = username;
        return reply(200, [row]);
      }
    }
    return reply(404, {});
  };
  return st;
}

async function boot(sbState, extra = {}) {
  const clock = { t: 1_700_000_000_000 };
  const app = createApp({
    now: () => clock.t, fetchImpl: sbState.fetch,
    config: { supabase: { url: URL_, anonKey: 'anon-key', cacheMs: 60_000 }, limits: { auth: [100000, 60000] }, ...extra },
  });
  // The JWT exp is wall-clock; align the app clock with it.
  clock.t = Date.now();
  const server = http.createServer((req, res) => app.handle(req, res));
  await new Promise(r => server.listen(0, '127.0.0.1', r));
  const base = `http://127.0.0.1:${server.address().port}`;
  const call = async (method, path, { token, body } = {}) => {
    const res = await fetch(base + path, { method, headers: { ...(token ? { Authorization: 'Bearer ' + token } : {}), ...(body ? { 'Content-Type': 'application/json' } : {}) }, body: body ? JSON.stringify(body) : undefined });
    const text = await res.text();
    return { status: res.status, body: text ? JSON.parse(text) : null };
  };
  return { app, clock, call, close: () => new Promise(r => server.close(() => { app.close(); r(); })) };
}

test('supabase mode: a verified token maps to one local player whose Player ID comes from Supabase', async () => {
  const sb = fakeSupabase(); const s = await boot(sb);
  try {
    const u = sb.signUp();
    const me = await s.call('GET', '/api/me', { token: u.token });
    assert.equal(me.status, 200);
    assert.equal(me.body.publicId, sb.profiles.get(u.id).player_id);
    assert.equal(me.body.username, sb.profiles.get(u.id).username);
    // wallet and inventory exist and are server-owned
    assert.equal((await s.call('GET', '/api/me/wallet', { token: u.token })).status, 200);
    assert.ok((await s.call('GET', '/api/me/inventory', { token: u.token })).body.owned.length > 0);
    // the same account on a "second device" (new token, same user) keeps the same id and the same local row
    const second = jwt(u.id);
    const me2 = await s.call('GET', '/api/me', { token: second });
    assert.equal(me2.body.publicId, me.body.publicId);
    assert.equal(s.app.db.prepare('SELECT COUNT(*) c FROM players').get().c, 1);
    // local passwords are never stored for these accounts
    assert.equal(s.app.db.prepare('SELECT password_hash h FROM players').get().h, '');
  } finally { await s.close(); }
});

test('supabase mode: no token, garbage, forged and local-style tokens are rejected', async () => {
  const sb = fakeSupabase(); const s = await boot(sb);
  try {
    assert.equal((await s.call('GET', '/api/me')).status, 401);
    assert.equal((await s.call('GET', '/api/me', { token: 'not-a-jwt' })).status, 401);
    assert.equal((await s.call('GET', '/api/me', { token: crypto.randomBytes(32).toString('base64url') })).status, 401);
    const forged = jwt(uuid()); // signed by nobody: Supabase does not know this user
    const r = await s.call('GET', '/api/me', { token: forged });
    assert.equal(r.status, 401); assert.equal(r.body.error.code, 'invalid_token');
    assert.equal(s.app.db.prepare('SELECT COUNT(*) c FROM players').get().c, 0);
    // password endpoints are off, so there is no second identity system
    const reg = await s.call('POST', '/api/auth/register', { body: { username: 'someone', password: 'password123' } });
    assert.equal(reg.status, 403); assert.equal(reg.body.error.code, 'local_auth_disabled');
    assert.equal((await s.call('POST', '/api/auth/login', { body: { username: 'x', password: 'y' } })).status, 403);
  } finally { await s.close(); }
});

test('supabase mode: expired and revoked sessions are refused; revocation is honoured after the cache window', async () => {
  const sb = fakeSupabase(); const s = await boot(sb);
  try {
    const u = sb.signUp();
    const expired = jwt(u.id, Math.floor(Date.now() / 1000) - 10);
    const e = await s.call('GET', '/api/me', { token: expired });
    assert.equal(e.status, 401); assert.equal(e.body.error.code, 'token_expired');

    assert.equal((await s.call('GET', '/api/me', { token: u.token })).status, 200);
    sb.revoked.add(u.token); // signed out elsewhere
    assert.equal((await s.call('GET', '/api/me', { token: u.token })).status, 200, 'still inside the short cache window');
    s.clock.t += 61_000;
    const after = await s.call('GET', '/api/me', { token: u.token });
    assert.equal(after.status, 401);
  } finally { await s.close(); }
});

test('supabase mode: when Supabase is unreachable the server answers 503 and never signs anyone in', async () => {
  const sb = fakeSupabase(); const s = await boot(sb);
  try {
    const u = sb.signUp();
    sb.down = true;
    const r = await s.call('GET', '/api/me', { token: u.token });
    assert.equal(r.status, 503); assert.equal(r.body.error.code, 'auth_unavailable');
    assert.equal(s.app.db.prepare('SELECT COUNT(*) c FROM players').get().c, 0);
    sb.down = false;
    assert.equal((await s.call('GET', '/api/me', { token: u.token })).status, 200);
  } finally { await s.close(); }
});

test('supabase mode: verification is cached briefly and concurrent first requests create exactly one player', async () => {
  const sb = fakeSupabase(); const s = await boot(sb);
  try {
    const u = sb.signUp();
    const rs = await Promise.all(Array.from({ length: 8 }, () => s.call('GET', '/api/me', { token: u.token })));
    assert.ok(rs.every(r => r.status === 200));
    assert.equal(new Set(rs.map(r => r.body.publicId)).size, 1);
    assert.equal(s.app.db.prepare('SELECT COUNT(*) c FROM players').get().c, 1);
    const before = sb.calls;
    await s.call('GET', '/api/me', { token: u.token });
    await s.call('GET', '/api/me', { token: u.token });
    assert.equal(sb.calls, before, 'no extra Supabase calls inside the cache window');
  } finally { await s.close(); }
});

test('supabase mode: username changes are saved in Supabase first and verified by reading them back', async () => {
  const sb = fakeSupabase(); const s = await boot(sb);
  try {
    const a = sb.signUp(); const b = sb.signUp();
    await s.call('GET', '/api/me', { token: a.token });
    await s.call('GET', '/api/me', { token: b.token });

    const ok = await s.call('PATCH', '/api/me', { token: a.token, body: { username: 'Ghost_Ace' } });
    assert.equal(ok.status, 200);
    assert.equal(sb.profiles.get(a.id).username, 'Ghost_Ace', 'persisted in Supabase');
    assert.equal(ok.body.username, 'Ghost_Ace');

    // someone else's name is refused by Supabase's own uniqueness, not just ours
    s.clock.t += 25 * 3600 * 1000;
    const dup = await s.call('PATCH', '/api/me', { token: b.token, body: { username: 'ghost_ace' } });
    assert.equal(dup.status, 409);

    // a backend that accepts but does not persist must be reported as a failure, not success
    sb.ignoreUpdates = true;
    const lost = await s.call('PATCH', '/api/me', { token: b.token, body: { username: 'Lost_Write' } });
    assert.equal(lost.status, 502); assert.equal(lost.body.error.code, 'update_not_persisted');
    assert.notEqual(s.app.db.prepare('SELECT username FROM players WHERE supabase_user_id = ?').get(b.id).username, 'Lost_Write');
  } finally { await s.close(); }
});

test('supabase mode: account deletion is honestly unsupported instead of faking success', async () => {
  const sb = fakeSupabase(); const s = await boot(sb);
  try {
    const u = sb.signUp();
    const r = await s.call('DELETE', '/api/me', { token: u.token, body: { password: 'whatever' } });
    assert.equal(r.status, 501);
    assert.equal((await s.call('GET', '/api/me', { token: u.token })).status, 200);
  } finally { await s.close(); }
});

test('supabase mode: an invalid profile from the account service is rejected', async () => {
  const sb = fakeSupabase(); const s = await boot(sb);
  try {
    const u = sb.signUp();
    sb.profiles.get(u.id).player_id = '123';
    const r = await s.call('GET', '/api/me', { token: u.token });
    assert.equal(r.status, 502); assert.equal(r.body.error.code, 'profile_invalid');
  } finally { await s.close(); }
});

test('health endpoint reports the auth mode; misconfigured supabase mode refuses to start', async () => {
  const sb = fakeSupabase(); const s = await boot(sb);
  try {
    const h = await s.call('GET', '/api/health');
    assert.equal(h.body.authMode, 'supabase'); assert.equal(h.body.supabaseConfigured, true);
  } finally { await s.close(); }
  assert.throws(() => createApp({ config: { authMode: 'supabase' } }), /not set/);
});

test('local mode (no Supabase configured) keeps the dev password accounts working and ignores JWT-shaped tokens', async () => {
  const { startServer } = require('./helpers');
  const s = await startServer();
  try {
    assert.equal(s.app.authMode, 'local');
    const a = await s.register('local_dev_a');
    assert.equal((await s.call('GET', '/api/me', { token: a.token })).status, 200);
    assert.equal((await s.call('GET', '/api/me', { token: jwt(uuid()) })).status, 401);
  } finally { await s.close(); }
});

test('supabase url must be https (loopback excepted), so tokens and the anon key never travel in clear text', () => {
  const { create } = require('../src/supabaseauth');
  assert.equal(create({ url: 'http://project.supabase.co', anonKey: 'k' }).enabled, false);
  assert.equal(create({ url: 'ftp://project.supabase.co', anonKey: 'k' }).enabled, false);
  assert.equal(create({ url: 'https://project.supabase.co', anonKey: '' }).enabled, false, 'anon key is required');
  assert.equal(create({ url: 'https://project.supabase.co', anonKey: 'k' }).enabled, true);
  assert.equal(create({ url: 'https://project.supabase.co/', anonKey: 'k' }).enabled, true);
  assert.equal(create({ url: 'http://127.0.0.1:54321', anonKey: 'k' }).enabled, true);
  assert.equal(create({ url: 'http://localhost:54321', anonKey: 'k' }).enabled, true);
  assert.equal(create({ url: 'http://127.0.0.1.evil.com', anonKey: 'k' }).enabled, false);
  assert.throws(() => createApp({ config: { authMode: 'supabase', supabase: { url: 'http://project.supabase.co', anonKey: 'k' } } }), /not set/);
});