// Runs the social/fair-play migration on PGlite with a stub of Supabase's auth schema and roles.
// Validates SQL logic only; it does not replace testing on a real Supabase project.
import { PGlite } from '@electric-sql/pglite';
import { readFileSync } from 'node:fs';
import { test, before } from 'node:test';
import assert from 'node:assert/strict';

const migration = readFileSync(new URL('../../supabase/migrations/20250301000000_social_fairplay.sql', import.meta.url), 'utf8');
const U = n => `${n}`.repeat(8) + '-1111-1111-1111-' + `${n}`.repeat(12);
const [A, B, C] = [U(1), U(2), U(3)];
let db;

async function asUser(uid, fn) {
  await db.exec(`set role authenticated; select set_config('request.jwt.claim.sub', '${uid}', false);`);
  try { return await fn(); } finally { await db.exec('reset role;'); }
}
async function denied(fn) { try { await fn(); } catch (e) { return String(e.message); } assert.fail('expected the statement to be rejected'); }

before(async () => {
  db = new PGlite();
  await db.exec(`
    create role anon nologin; create role authenticated nologin; create role service_role nologin;
    create schema auth;
    create table auth.users (id uuid primary key);
    create function auth.uid() returns uuid language sql stable as $$ select nullif(current_setting('request.jwt.claim.sub', true), '')::uuid $$;
    grant usage on schema auth to anon, authenticated, service_role;
    grant execute on function auth.uid() to anon, authenticated, service_role;
    grant usage on schema public to anon, authenticated, service_role;
  `);
  await db.exec(migration);
  await db.query('insert into auth.users (id) values ($1), ($2), ($3)', [A, B, C]);
});

test('clan names and tags are unique ignoring case/whitespace; one clan per player; one leader', async () => {
  const id = (await db.query('select create_clan($1, $2, $3) id', [A, 'Iron Wolves', 'IRON'])).rows[0].id;
  assert.ok(id);
  assert.match(await denied(() => db.query('select create_clan($1, $2, $3)', [B, '  iron   WOLVES', 'ZZZ'])), /name_key|duplicate/i);
  assert.match(await denied(() => db.query('select create_clan($1, $2, $3)', [B, 'Other Name', 'iron'])), /clans_tag_key|duplicate/i);
  assert.match(await denied(() => db.query('select create_clan($1, $2, $3)', [A, 'Second Clan', 'SEC'])), /already_in_clan/);
  assert.match(await denied(() => db.query("insert into clan_members(clan_id, user_id, role) values ($1, $2, 'leader')", [id, B])), /clan_one_leader|duplicate/i);
});

test('RLS: clan members are visible only to the clan; invitations and applications only to those involved', async () => {
  const clan = (await db.query('select id from clans limit 1')).rows[0].id;
  await db.query("insert into clan_invitations(clan_id, from_user, to_user, expires_at) values ($1, $2, $3, now() + interval '1 day')", [clan, A, C]);
  assert.equal((await asUser(A, () => db.query('select * from clan_members'))).rows.length, 1);
  assert.equal((await asUser(B, () => db.query('select * from clan_members'))).rows.length, 0);
  assert.equal((await asUser(C, () => db.query('select * from clan_invitations'))).rows.length, 1);
  assert.equal((await asUser(B, () => db.query('select * from clan_invitations'))).rows.length, 0);
  assert.equal((await asUser(B, () => db.query('select * from clans'))).rows.length, 1, 'clans are browsable');
});

test('clients cannot write clans, results, companions, reputation, or call trusted functions', async () => {
  const clan = (await db.query('select id from clans limit 1')).rows[0].id;
  await asUser(B, async () => {
    await denied(() => db.query("update clans set xp = 99999 where id = $1", [clan]));
    await denied(() => db.query("insert into clan_members(clan_id, user_id, role) values ($1, $2, 'member')", [clan, B]));
    await denied(() => db.query("insert into player_companions(user_id, companion_id) values ($1, 'nova')", [B]));
    await denied(() => db.query('update player_reputation set score = 100 where user_id = $1', [B]));
    await denied(() => db.query("select create_clan($1, 'Hack', 'HAX')", [B]));
    await denied(() => db.query("select reputation_apply($1, 'x', 50, 'k')", [B]));
    await denied(() => db.query('select * from audit_log'));
    await denied(() => db.query('select * from moderators'));
  });
});

test('clan result: applied once, replay is a no-op, conflicting payload and unstarted match are rejected', async () => {
  const a = (await db.query('select id from clans limit 1')).rows[0].id;
  const b = (await db.query("select create_clan($1, 'Blue Crew', 'BLUE') id", [B])).rows[0].id;
  await db.query("insert into clan_matches(id, clan_a, clan_b, clan_a_name, clan_b_name, team_size, round_limit, status) values ('m1', $1, $2, 'Iron Wolves', 'Blue Crew', 2, 3, 'pending')", [a, b]);
  assert.match(await denied(() => db.query("select record_clan_match_result('m1','h1',$1,3,0,true,150,50)", [a])), /match_not_live/);
  await db.query("update clan_matches set status = 'live' where id = 'm1'");
  assert.equal((await db.query("select record_clan_match_result('m1','h1',$1,3,0,true,150,50) r", [a])).rows[0].r, true);
  assert.equal((await db.query("select record_clan_match_result('m1','h1',$1,3,0,true,150,50) r", [a])).rows[0].r, false);
  assert.match(await denied(() => db.query("select record_clan_match_result('m1','h2',$1,0,3,true,150,50)", [b])), /conflicting_result/);
  const row = (await db.query('select xp, wins from clans where id = $1', [a])).rows[0];
  assert.equal(Number(row.xp), 150); assert.equal(row.wins, 1);
  assert.equal((await db.query('select count(*)::int c from clan_match_results')).rows[0].c, 1);
  // match history survives the clan being disbanded
  await db.query('delete from clans where id = $1', [b]);
  assert.equal((await db.query("select clan_b_name from clan_matches where id = 'm1'")).rows[0].clan_b_name, 'Blue Crew');
});

test('reputation: idempotent by key and clamped; reports are private and never auto-punish', async () => {
  assert.equal((await db.query("select reputation_apply($1,'abandon',-3,'abandon:m1') s", [C])).rows[0].s, 97);
  assert.equal((await db.query("select reputation_apply($1,'abandon',-3,'abandon:m1') s", [C])).rows[0].s, 97);
  assert.equal((await db.query("select reputation_apply($1,'bonus',50,'b:1') s", [C])).rows[0].s, 100);
  await db.query("select submit_player_report($1,$2,'harassment','Abusive messages','log','m1')", [A, C]);
  await db.query("select submit_player_report($1,$2,'harassment','Abusive again','log','m2')", [B, C]);
  assert.equal((await db.query('select score from player_reputation where user_id = $1', [C])).rows[0].score, 100);
  assert.equal((await asUser(C, () => db.query('select * from player_reports'))).rows.length, 0, 'target cannot see reports about them');
  assert.equal((await asUser(A, () => db.query('select * from player_reports'))).rows.length, 1);
  assert.match(await denied(() => db.query("select submit_player_report($1,$1,'afk','x x x',null,null)", [A])), /cannot_report_self|check/i);
  assert.match(await denied(() => db.query("select submit_player_report($1,$2,'afk','limit me',null,null,1)", [A, B])), /report_limit/);
  assert.match(await denied(() => db.query("select submit_player_report($1,$2,'invalid','bad cat',null,null)", [B, A])), /check/i);
});

test('audit log is append-only; moderation notes are hidden from the player; one appeal per action', async () => {
  await db.query("insert into audit_log(actor_kind, action) values ('system', 'test')");
  assert.match(await denied(() => db.query("update audit_log set action = 'x'")), /append-only/);
  assert.match(await denied(() => db.query('delete from audit_log')), /append-only/);
  const warn = (await db.query("insert into moderation_actions(target_id, action, public_reason, internal_note) values ($1,'warn','Language','secret note') returning id", [C])).rows[0].id;
  await db.query("insert into moderation_actions(target_id, action, internal_note) values ($1,'note','private')", [C]);
  const seen = (await asUser(C, () => db.query('select id, action, public_reason from moderation_actions'))).rows;
  assert.equal(seen.length, 1);
  assert.equal('internal_note' in seen[0], false);
  await asUser(C, () => db.query("select internal_note from moderation_actions").then(() => assert.fail('column should be hidden')).catch(e => assert.match(String(e.message), /permission denied/)));
  await db.query("insert into appeals(action_id, user_id, message) values ($1,$2,'This was a joke between friends.')", [warn, C]);
  assert.match(await denied(() => db.query("insert into appeals(action_id, user_id, message) values ($1,$2,'Second appeal attempt here.')", [warn, C])), /duplicate|unique/i);
});

test('companions: variant foreign keys, equipped must be owned; map events: one claim per player, valid windows', async () => {
  await db.query("insert into companion_catalog(id, name, kind, rarity, unlock_kind) values ('bolt','Bolt','drone','common','default')");
  await db.query("insert into companion_variants(companion_id, variant_id, name) values ('bolt','default','Standard')");
  assert.match(await denied(() => db.query("insert into player_companions(user_id, companion_id, variant_id) values ($1,'bolt','missing')", [A])), /foreign key|violates/i);
  assert.match(await denied(() => db.query("insert into player_companion_equipped(user_id, companion_id, variant_id) values ($1,'bolt','default')", [A])), /foreign key|violates/i);
  await db.query("insert into player_companions(user_id, companion_id) values ($1,'bolt')", [A]);
  await db.query("insert into player_companion_equipped(user_id, companion_id, variant_id) values ($1,'bolt','default')", [A]);
  assert.match(await denied(() => db.query("insert into map_events(id, match_id, slot, type, x,y,z,radius, announce_at,start_at,ends_at) values ('e0','m','s','objective',0,0,0,5, now(), now() + interval '1 min', now())")), /check/i);
  await db.query("insert into map_events(id, match_id, slot, type, x,y,z,radius, announce_at,start_at,ends_at, capacity) values ('e1','m','s','supply_drop',0,0,0,5, now(), now(), now() + interval '1 min', 3)");
  await db.query("insert into map_event_claims(event_id, user_id) values ('e1',$1)", [A]);
  assert.match(await denied(() => db.query("insert into map_event_claims(event_id, user_id) values ('e1',$1)", [A])), /duplicate|unique|primary/i);
  assert.match(await denied(() => db.query("insert into map_events(id, match_id, slot, type, x,y,z,radius, announce_at,start_at,ends_at) values ('e2','m','s','objective',0,0,0,5, now(), now(), now() + interval '1 min')")), /duplicate|unique/i);
});
