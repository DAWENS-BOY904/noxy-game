// Real Node backend  <->  fake GoTrue/PostgREST backed by the REAL players migration in PGlite.
// What this proves: the bridge works against actual RLS, column grants, unique constraints and the Player-ID trigger.
// What it does NOT prove: anything about a real Supabase project, real tokens, real providers or the network.
// For that, run tools/supabase-live-check against your project.
import { PGlite } from '@electric-sql/pglite';
import { readFileSync } from 'node:fs';
import { createRequire } from 'node:module';
import http from 'node:http';
import crypto from 'node:crypto';
import { test, before, after } from 'node:test';
import assert from 'node:assert/strict';

const require = createRequire(import.meta.url);
const { createApp } = require('../../server/src/app.js');
const migration = readFileSync(new URL('../../supabase/migrations/20250101000000_players.sql', import.meta.url), 'utf8');

let db, app, server, base, queue = Promise.resolve();
const valid = new Set(); // tokens Supabase considers live
const b64 = o => Buffer.from(JSON.stringify(o)).toString('base64url');
const mkToken = sub => `${b64({ alg: 'ES256' })}.${b64({ sub, exp: Math.floor(Date.now() / 1000) + 10 * 24 * 3600 })}.${crypto.randomBytes(24).toString('base64url')}`;

// PGlite has one connection, so serialise "requests" and switch role per request like PostgREST does.
const exclusive = fn => (queue = queue.then(fn, fn));
async function asUser(sub, fn) {
  await db.exec(`set role authenticated; select set_config('request.jwt.claim.sub', '${sub}', false);`);
  try { return await fn(); } finally { await db.exec('reset role;'); }
}

async function signUp() {
  const id = crypto.randomUUID();
  await db.query('insert into auth.users (id) values ($1)', [id]);
  const token = mkToken(id); valid.add(token);
  return { id, token, newDevice: () => { const t = mkToken(id); valid.add(t); return t; } };
}

async function fakeFetch(url, init) {
  const u = new URL(url);
  const token = (init.headers.Authorization || '').replace('Bearer ', '');
  const sub = JSON.parse(Buffer.from(token.split('.')[1], 'base64url')).sub;
  const reply = (status, body) => ({ status, text: async () => (body === undefined ? '' : JSON.stringify(body)) });
  if (init.headers.apikey !== 'anon-key') return reply(401, {});
  if (!valid.has(token)) return reply(401, { msg: 'invalid JWT' });
  return exclusive(async () => {
    try {
      if (u.pathname === '/auth/v1/user') return reply(200, { id: sub, is_anonymous: false });
      if (u.pathname === '/rest/v1/rpc/ensure_player') return reply(200, (await asUser(sub, () => db.query('select (public.ensure_player()).*'))).rows[0]);
      if (u.pathname === '/rest/v1/players') {
        const filter = (u.searchParams.get('user_id') || '').replace(/^eq\./, '');
        if (init.method === 'GET') {
          const r = await asUser(sub, () => db.query('select player_id, username, avatar_url, level, xp from public.players where user_id = $1', [filter]));
          return reply(200, r.rows.map(x => ({ ...x, xp: Number(x.xp) })));
        }
        if (init.method === 'PATCH') {
          const body = JSON.parse(init.body);
          const r = await asUser(sub, () => db.query('update public.players set username = $1 where user_id = $2 returning player_id, username', [body.username, filter]));
          return reply(200, r.rows);
        }
      }
      return reply(404, {});
    } catch (e) {
      if (/unique|duplicate/i.test(e.message)) return reply(409, { code: '23505', message: e.message });
      if (/check/i.test(e.message)) return reply(400, { code: '23514', message: e.message });
      return reply(500, { message: e.message });
    }
  });
}

const get = async (token, path = '/api/me') => {
  const r = await fetch(base + path, { headers: { Authorization: 'Bearer ' + token } });
  return { status: r.status, body: await r.json() };
};
const patch = async (token, body) => {
  const r = await fetch(base + '/api/me', { method: 'PATCH', headers: { Authorization: 'Bearer ' + token, 'Content-Type': 'application/json' }, body: JSON.stringify(body) });
  return { status: r.status, body: await r.json() };
};

const clock = { t: Date.now() };

before(async () => {
  db = new PGlite();
  await db.exec(`
    create role anon nologin; create role authenticated nologin; create role service_role nologin;
    create schema auth;
    create table auth.users (id uuid primary key);
    create function auth.uid() returns uuid language sql stable
      as $$ select nullif(current_setting('request.jwt.claim.sub', true), '')::uuid $$;
    grant usage on schema auth to anon, authenticated, service_role;
    grant execute on function auth.uid() to anon, authenticated, service_role;
    grant usage on schema public to anon, authenticated, service_role;
  `);
  await db.exec(migration);
  app = createApp({ now: () => clock.t, fetchImpl: fakeFetch, config: { supabase: { url: 'https://x.supabase.co', anonKey: 'anon-key', cacheMs: 60_000 }, limits: { auth: [100000, 60000] } } });
  server = http.createServer((req, res) => app.handle(req, res));
  await new Promise(r => server.listen(0, '127.0.0.1', r));
  base = `http://127.0.0.1:${server.address().port}`;
});
after(async () => { await new Promise(r => server.close(r)); app.close(); });

const sqlPlayer = async id => (await db.query('select * from public.players where user_id = $1', [id])).rows[0];

test('registration: the trigger-created Supabase profile is what the game sees (Test A)', async () => {
  const u = await signUp();
  const row = await sqlPlayer(u.id);
  assert.match(row.player_id, /^[1-9][0-9]{9}$/);
  const me = await get(u.token);
  assert.equal(me.status, 200);
  assert.equal(me.body.publicId, row.player_id, 'game Player ID == database Player ID');
  assert.equal(me.body.username, row.username);
  assert.equal((await db.query('select count(*)::int c from public.players where user_id = $1', [u.id])).rows[0].c, 1);
});

test('login on a second device keeps the same Player ID and one profile (Test B/D)', async () => {
  const u = await signUp();
  const first = await get(u.token);
  const second = await get(u.newDevice());
  assert.equal(second.body.publicId, first.body.publicId);
  assert.equal((await db.query('select count(*)::int c from public.players where user_id = $1', [u.id])).rows[0].c, 1);
  assert.equal(app.db.prepare('SELECT COUNT(*) c FROM players WHERE supabase_user_id = ?').get(u.id).c, 1);
});

test('profile persistence: rename is stored in Postgres and survives sign-out/in; other device sees it (Test C/D)', async () => {
  const u = await signUp();
  const dev2 = u.newDevice();
  await get(u.token); await get(dev2);
  const r = await patch(u.token, { username: 'Persist_Me' });
  assert.equal(r.status, 200);
  assert.equal((await sqlPlayer(u.id)).username, 'Persist_Me', 'row in the database');
  // sign out: token no longer valid at Supabase; sign back in with a brand-new token
  valid.delete(u.token);
  clock.t += 61_000; // device 2's verification cache window has passed
  const back = await get(u.newDevice());
  assert.equal(back.body.username, 'Persist_Me');
  const other = await get(dev2);
  assert.equal(other.body.username, 'Persist_Me', 'second device refreshed from Supabase');
  assert.equal((await get(u.token)).status, 401, 'signed-out token is refused');
});

test('uniqueness: Supabase refuses a taken username (case-insensitive) and the game reports it (Test E)', async () => {
  const a = await signUp(); const b = await signUp();
  await get(a.token); await get(b.token);
  assert.equal((await patch(a.token, { username: 'Unique_Name' })).status, 200);
  clock.t += 25 * 3600 * 1000;
  const dup = await patch(b.token, { username: 'unique_name' });
  assert.equal(dup.status, 409);
  assert.notEqual((await sqlPlayer(b.id)).username, 'unique_name');
});

test('authorization: RLS hides other players; protected fields cannot be changed by a client (Test E)', async () => {
  const a = await signUp(); const b = await signUp();
  await get(a.token); await get(b.token);
  const bRow = await sqlPlayer(b.id);

  // A's own token cannot read B's profile through the data API ...
  const seen = await asUser(a.id, () => db.query('select * from public.players where user_id = $1', [b.id]));
  assert.equal(seen.rows.length, 0);
  // ... cannot rename B ...
  const upd = await asUser(a.id, () => db.query('update public.players set username = $1 where user_id = $2 returning 1', ['Hijacked_B', b.id]));
  assert.equal(upd.rows.length, 0);
  // ... and cannot touch xp / level / coins / gems / player_id on itself either
  for (const col of ['xp', 'level', 'coins', 'gems', 'player_id', 'user_id']) {
    await assert.rejects(asUser(a.id, () => db.query(`update public.players set ${col} = ${col} where user_id = $1`, [a.id])), /permission denied/i, col);
  }
  assert.equal((await sqlPlayer(b.id)).username, bRow.username);

  // Through the game API, extra fields in the body are ignored.
  const r = await patch(a.token, { username: 'Honest_Name', level: 99, xp: 99999, publicId: '1234567890' });
  assert.equal(r.status, 200);
  const me = await get(a.token);
  assert.equal(me.body.level, 1); assert.equal(me.body.xp, 0);
  assert.notEqual(me.body.publicId, '1234567890');
  const row = await sqlPlayer(a.id);
  assert.equal(row.level, 1); assert.equal(Number(row.xp), 0);
});

test('signed-out / unknown tokens never create a player (Test B)', async () => {
  const before = app.db.prepare('SELECT COUNT(*) c FROM players').get().c;
  const ghost = mkToken(crypto.randomUUID()); // never registered with Supabase
  assert.equal((await get(ghost)).status, 401);
  assert.equal(app.db.prepare('SELECT COUNT(*) c FROM players').get().c, before);
});
