// Runs the real migration against an in-process Postgres (PGlite) with a minimal stub of Supabase's
// `auth` schema and roles, then verifies Player ID, RLS and protected-column behaviour.
// This validates the SQL logic; it does NOT replace testing against a real Supabase project.
import { PGlite } from '@electric-sql/pglite';
import { readFileSync } from 'node:fs';
import { test, before } from 'node:test';
import assert from 'node:assert/strict';

const migration = readFileSync(new URL('../../supabase/migrations/20250101000000_players.sql', import.meta.url), 'utf8');
const usernameMigration = readFileSync(new URL('../../supabase/migrations/20250501000000_signup_username.sql', import.meta.url), 'utf8');
const A = '11111111-1111-1111-1111-111111111111';
const B = '22222222-2222-2222-2222-222222222222';
let db;

async function asUser(uid, fn) {
  await db.exec(`set role authenticated; select set_config('request.jwt.claim.sub', '${uid}', false);`);
  try { return await fn(); } finally { await db.exec('reset role;'); }
}
async function asAnon(fn) {
  await db.exec(`set role anon; select set_config('request.jwt.claim.sub', '', false);`);
  try { return await fn(); } finally { await db.exec('reset role;'); }
}
const addUser = (id, username = null) => db.query(
  'insert into auth.users (id, raw_user_meta_data) values ($1, $2::jsonb)',
  [id, JSON.stringify(username ? { username } : {})],
);

before(async () => {
  db = new PGlite();
  await db.exec(`
    create role anon nologin; create role authenticated nologin; create role service_role nologin;
    create schema auth;
    create table auth.users (id uuid primary key, raw_user_meta_data jsonb not null default '{}'::jsonb);
    create function auth.uid() returns uuid language sql stable
      as $$ select nullif(current_setting('request.jwt.claim.sub', true), '')::uuid $$;
    grant usage on schema auth to anon, authenticated, service_role;
    grant execute on function auth.uid() to anon, authenticated, service_role;
    grant usage on schema public to anon, authenticated, service_role;
  `);
  await db.exec(migration);
  await db.exec(usernameMigration);
  await addUser(A);
  await addUser(B);
});

test('new account gets exactly one profile with a 10-digit id', async () => {
  const r = await db.query('select * from public.players where user_id = $1', [A]);
  assert.equal(r.rows.length, 1);
  assert.match(r.rows[0].player_id, /^[1-9][0-9]{9}$/);
  assert.equal(r.rows[0].level, 1);
  assert.equal(Number(r.rows[0].xp), 0);
});

test('sign-up metadata sets a valid requested username', async () => {
  const id = '33333333-3333-3333-3333-333333333333';
  await addUser(id, 'Noxy_Ranger');
  const r = await db.query('select username from public.players where user_id = $1', [id]);
  assert.equal(r.rows[0].username, 'Noxy_Ranger');
});

test('invalid or taken sign-up names safely fall back to a generated username', async () => {
  const invalidId = '44444444-4444-4444-4444-444444444444';
  const duplicateId = '55555555-5555-5555-5555-555555555555';
  await addUser(invalidId, 'not a valid name');
  await addUser(duplicateId, 'Noxy_Ranger');
  const r = await db.query('select username from public.players where user_id = any($1::uuid[])', [[invalidId, duplicateId]]);
  assert.equal(r.rows.length, 2);
  assert.ok(r.rows.every(row => /^Operator_[1-9][0-9]{9}$/.test(row.username)));
});

test('ensure_player is idempotent and keeps the same id', async () => {
  const before = (await db.query('select player_id from public.players where user_id=$1', [A])).rows[0].player_id;
  for (let i = 0; i < 3; i++) {
    const r = await asUser(A, () => db.query('select (public.ensure_player()).player_id as pid'));
    assert.equal(r.rows[0].pid, before);
  }
  assert.equal((await db.query('select count(*)::int c from public.players where user_id=$1', [A])).rows[0].c, 1);
});

test('ensure_player rejects unauthenticated callers', async () => {
  await assert.rejects(asAnon(() => db.query('select public.ensure_player()')));
});

test('player_id is unique and format is enforced by constraints', async () => {
  const a = (await db.query('select player_id from public.players where user_id=$1', [A])).rows[0].player_id;
  await assert.rejects(db.query(`update public.players set player_id=$1 where user_id=$2`, [a, B]), /unique|duplicate/i);
  await assert.rejects(db.query(`update public.players set player_id='123' where user_id=$1`, [B]), /check/i);
  await assert.rejects(db.query(`update public.players set player_id='0123456789' where user_id=$1`, [B]), /check/i);
});

test('collision is retried: many sign-ups all get distinct ids', async () => {
  const ids = new Set();
  for (let i = 0; i < 60; i++) {
    const id = `aaaaaaaa-0000-0000-0000-${String(i).padStart(12, '0')}`;
    await addUser(id);
    ids.add((await db.query('select player_id from public.players where user_id=$1', [id])).rows[0].player_id);
  }
  assert.equal(ids.size, 60);
});

test('collision path: id generator skips an id that is already taken', async () => {
  // Force the unique_violation path by making the first generated candidates collide.
  await db.exec(`
    create or replace function public.generate_player_id() returns text language plpgsql set search_path='' as $$
    declare c text; begin
      if current_setting('test.calls', true) is null or current_setting('test.calls', true) = '' then
        perform set_config('test.calls', '1', false);
        select player_id into c from public.players limit 1;   -- an already-used id -> unique_violation
        return c;
      end if;
      return (1000000000 + floor(random()*8999999999))::bigint::text;
    end $$;`);
  const id = 'bbbbbbbb-0000-0000-0000-000000000001';
  await addUser(id);
  const r = await db.query('select player_id from public.players where user_id=$1', [id]);
  assert.match(r.rows[0].player_id, /^[1-9][0-9]{9}$/);
  // restore the real generator
  await db.exec(migration.match(/create or replace function public\.generate_player_id[\s\S]*?\$\$;/)[0]);
});

test('RLS: a user sees only their own profile', async () => {
  const r = await asUser(A, () => db.query('select user_id from public.players'));
  assert.equal(r.rows.length, 1);
  assert.equal(r.rows[0].user_id, A);
});

test('RLS: anonymous role cannot read profiles', async () => {
  await assert.rejects(asAnon(() => db.query('select * from public.players')), /permission denied/i);
});

test('user can update own username/avatar but not another user\'s', async () => {
  await asUser(A, () => db.query(`update public.players set username='Alpha_One', avatar_url='https://x/y.png' where user_id=$1`, [A]));
  const r = await asUser(A, () => db.query('select username from public.players where user_id=$1', [A]));
  assert.equal(r.rows[0].username, 'Alpha_One');
  const other = await asUser(A, () => db.query(`update public.players set username='Hacked_B' where user_id=$1 returning 1`, [B]));
  assert.equal(other.rows.length, 0);
  const b = await db.query('select username from public.players where user_id=$1', [B]);
  assert.notEqual(b.rows[0].username, 'Hacked_B');
});

test('protected columns cannot be changed by clients', async () => {
  for (const col of ['level', 'xp', 'coins', 'gems', 'player_id', 'user_id']) {
    const val = col === 'user_id' ? `'${B}'::uuid` : col === 'player_id' ? `'1234567890'` : '999';
    await assert.rejects(
      asUser(A, () => db.query(`update public.players set ${col} = ${val} where user_id=$1`, [A])),
      /permission denied/i, col);
  }
});

test('clients cannot insert or delete profiles', async () => {
  await assert.rejects(asUser(A, () => db.query(`insert into public.players (user_id, player_id, username) values ($1,'9999999999','Dup_User')`, [A])), /permission denied/i);
  await assert.rejects(asUser(A, () => db.query('delete from public.players where user_id=$1', [A])), /permission denied/i);
});

test('username validation and case-insensitive uniqueness', async () => {
  await assert.rejects(asUser(A, () => db.query(`update public.players set username='x' where user_id=$1`, [A])), /check/i);
  await assert.rejects(asUser(A, () => db.query(`update public.players set username='bad name!' where user_id=$1`, [A])), /check/i);
  await asUser(B, () => db.query(`update public.players set username='Beta_Two' where user_id=$1`, [B]));
  await assert.rejects(asUser(A, () => db.query(`update public.players set username='beta_two' where user_id=$1`, [A])), /unique|duplicate/i);
});

test('updated_at moves on update', async () => {
  const t0 = (await db.query('select updated_at from public.players where user_id=$1', [A])).rows[0].updated_at;
  await new Promise((r) => setTimeout(r, 15));
  await asUser(A, () => db.query(`update public.players set avatar_url=null where user_id=$1`, [A]));
  const t1 = (await db.query('select updated_at from public.players where user_id=$1', [A])).rows[0].updated_at;
  assert.ok(t1 > t0);
});

test('find_player returns only public fields and is rate limited', async () => {
  const pid = (await db.query('select player_id from public.players where user_id=$1', [B])).rows[0].player_id;
  const r = await asUser(A, () => db.query('select * from public.find_player($1)', [pid]));
  assert.deepEqual(Object.keys(r.rows[0]).sort(), ['avatar_url', 'level', 'player_id', 'username']);
  const bad = await asUser(A, () => db.query(`select * from public.find_player('abc')`));
  assert.equal(bad.rows.length, 0);
  let limited = false;
  for (let i = 0; i < 30 && !limited; i++) {
    try { await asUser(A, () => db.query('select * from public.find_player($1)', [pid])); }
    catch (e) { limited = /rate limited/.test(e.message); }
  }
  assert.ok(limited, 'expected rate limit to trigger');
});

test('rate_limits and internal functions are not reachable by clients', async () => {
  await assert.rejects(asUser(A, () => db.query('select * from public.rate_limits')), /permission denied/i);
  await assert.rejects(asUser(A, () => db.query(`select public.grant_progress($1, 100, 100, 100)`, [A])), /permission denied/i);
  await assert.rejects(asUser(A, () => db.query(`select public.create_player_for($1)`, [B])), /permission denied/i);
});

test('deleting the auth user cascades to the profile', async () => {
  const id = 'cccccccc-0000-0000-0000-000000000001';
  await addUser(id);
  await db.query('delete from auth.users where id=$1', [id]);
  assert.equal((await db.query('select count(*)::int c from public.players where user_id=$1', [id])).rows[0].c, 0);
});
