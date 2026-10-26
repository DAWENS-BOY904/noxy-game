// Identity hardening migration (20250401000000) on top of the players migration, in PGlite with a stub auth schema.
// Validates SQL logic only; it does not replace running the migrations on a real Supabase project.
import { PGlite } from '@electric-sql/pglite';
import { readFileSync } from 'node:fs';
import { test, before } from 'node:test';
import assert from 'node:assert/strict';

const read = f => readFileSync(new URL(`../../supabase/migrations/${f}`, import.meta.url), 'utf8');
const A = '11111111-1111-1111-1111-111111111111';
const B = '22222222-2222-2222-2222-222222222222';
let db;
async function as(role, uid, fn) {
  await db.exec(`set role ${role}; select set_config('request.jwt.claim.sub', '${uid || ''}', false);`);
  try { return await fn(); } finally { await db.exec('reset role;'); }
}

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
  await db.exec(read('20250101000000_players.sql'));
  await db.query('insert into auth.users (id) values ($1), ($2)', [A, B]);
  await db.exec(read('20250401000000_identity_hardening.sql'));
});

test('migration is idempotent (safe to run twice)', async () => {
  await db.exec(read('20250401000000_identity_hardening.sql'));
});

test('avatar_url accepts https and null, rejects other schemes', async () => {
  await as('authenticated', A, () => db.query("update public.players set avatar_url = 'https://cdn.example.com/a.png' where user_id = $1", [A]));
  await as('authenticated', A, () => db.query('update public.players set avatar_url = null where user_id = $1', [A]));
  for (const bad of ['javascript:alert(1)', 'http://insecure.example.com/a.png', 'file:///etc/passwd', 'data:text/html;base64,AAAA', 'https://has space.example.com/a.png']) {
    await assert.rejects(as('authenticated', A, () => db.query('update public.players set avatar_url = $1 where user_id = $2', [bad, A])), /check|violates/i, bad);
  }
});

test('users still cannot touch protected columns or other players after the migration', async () => {
  for (const col of ['xp', 'level', 'coins', 'gems', 'player_id', 'user_id']) {
    await assert.rejects(as('authenticated', A, () => db.query(`update public.players set ${col} = ${col} where user_id = $1`, [A])), /permission denied/i, col);
  }
  const other = await as('authenticated', A, () => db.query("update public.players set username = 'Hijack_B' where user_id = $1 returning 1", [B]));
  assert.equal(other.rows.length, 0);
  const seen = await as('authenticated', A, () => db.query('select * from public.players where user_id = $1', [B]));
  assert.equal(seen.rows.length, 0);
});

test('grant_progress is no longer executable by service_role, anon or authenticated', async () => {
  for (const role of ['service_role', 'authenticated', 'anon']) {
    await assert.rejects(as(role, A, () => db.query('select public.grant_progress($1, 10, 10, 10)', [A])), /permission denied/i, role);
  }
  const p = (await db.query('select xp, coins, gems from public.players where user_id = $1', [A])).rows[0];
  assert.equal(Number(p.xp) + Number(p.coins) + Number(p.gems), 0);
});

test('columns and the Player ID trigger are intact', async () => {
  const cols = (await db.query("select column_name from information_schema.columns where table_schema='public' and table_name='players'")).rows.map(r => r.column_name);
  for (const c of ['user_id', 'player_id', 'username', 'avatar_url', 'level', 'xp', 'coins', 'gems', 'created_at', 'updated_at']) assert.ok(cols.includes(c), c);
  await db.query('insert into auth.users (id) values ($1)', ['33333333-3333-3333-3333-333333333333']);
  const r = await db.query("select player_id from public.players where user_id = '33333333-3333-3333-3333-333333333333'");
  assert.match(r.rows[0].player_id, /^[1-9][0-9]{9}$/);
});
