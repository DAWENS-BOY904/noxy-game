'use strict';
const test = require('node:test');
const assert = require('node:assert/strict');
const { startServer } = require('./helpers');

const KEY = 'unit-test-server-key';
async function internal(s, method, path, body, key = KEY) {
  const res = await fetch(s.base + path, { method, headers: { 'Content-Type': 'application/json', ...(key ? { 'x-noxy-server-key': key } : {}) }, body: body === undefined ? undefined : JSON.stringify(body) });
  const text = await res.text();
  return { status: res.status, body: text ? JSON.parse(text) : null };
}
const server = (dbPath) => startServer({ dbPath, config: { economy: { matchServerKey: KEY } } });
const wallet = async (s, u) => (await s.call('GET', '/api/me/wallet', { token: u.token })).body;

async function plan(s, users, matchId = 'match-evt-1', over = {}) {
  const r = await internal(s, 'POST', `/api/internal/matches/${matchId}/events/plan`, {
    map: 'Foundry Yard', durationSeconds: 1200, startedAt: s.clock.t,
    players: users.map((u, i) => ({ publicId: u.me.publicId, team: i % 2 })), ...over,
  });
  return r;
}
const byType = (events, t) => events.find(e => e.type === t);
const at = (s, e, ms) => { s.clock.t = e.startAt + ms; };

test('map events: planning is server-only, deterministic, idempotent and stays inside map bounds', async () => {
  const s = await server();
  const users = [await s.register('evt1a'), await s.register('evt1b')];
  assert.equal((await s.call('POST', '/api/internal/matches/match-evt-1/events/plan', { token: users[0].token, body: {} })).status, 401);
  const p1 = await plan(s, users);
  assert.equal(p1.status, 200, JSON.stringify(p1.body));
  assert.equal(p1.body.duplicate, false);
  const types = p1.body.events.map(e => e.type);
  for (const t of ['supply_drop', 'objective', 'loot_zone', 'environmental']) assert.ok(types.includes(t), t);
  for (const e of p1.body.events) {
    assert.ok(e.marker.x - e.marker.radius >= -150 && e.marker.x + e.marker.radius <= 150);
    assert.ok(e.announceAt <= e.startAt && e.startAt < e.endsAt);
    if (e.type !== 'environmental') assert.ok(!(Math.abs(e.marker.x) < 25 + e.marker.radius && Math.abs(e.marker.z) < 25 + e.marker.radius), 'not in blocked centre');
  }
  const p2 = await plan(s, users);
  assert.equal(p2.body.duplicate, true);
  assert.deepEqual(p2.body.events.map(e => e.id), p1.body.events.map(e => e.id));
  assert.equal((await plan(s, users, 'match-evt-2', { map: 'Nowhere' })).status, 404);
  assert.equal((await plan(s, users, 'match-evt-3', { startedAt: 5 })).status, 400);
  await s.close();
});

test('map events: clients see the same absolute schedule and countdown; scheduled events stay hidden', async () => {
  const s = await server();
  const [a, b] = [await s.register('evt2a'), await s.register('evt2b')];
  const { body } = await plan(s, [a, b], 'match-evt-4');
  const sup = body.events.find(e => e.type === 'supply_drop');
  s.clock.t = sup.announceAt + 1000;
  const va = (await s.call('GET', '/api/matches/match-evt-4/events', { token: a.token })).body;
  const vb = (await s.call('GET', '/api/matches/match-evt-4/events', { token: b.token })).body;
  assert.deepEqual(va, vb);
  const seen = va.events.find(e => e.id === sup.id);
  assert.equal(seen.phase, 'announced'); assert.equal(seen.countdownMs, sup.startAt - s.clock.t);
  assert.ok(va.events.every(e => e.phase !== 'scheduled'));
  const outsider = await s.register('evt2c');
  assert.equal((await s.call('GET', '/api/matches/match-evt-4/events', { token: outsider.token })).status, 404);
  await s.close();
});

test('map events: supply loot — one claim per player, capacity, range, phase and rewards once', async () => {
  const s = await server();
  const users = [];
  for (let i = 0; i < 8; i++) users.push(await s.register('loot' + i));
  const { body } = await plan(s, users, 'match-evt-5');
  const sup = body.events.find(e => e.type === 'supply_drop');
  const claim = (u, x = sup.marker.x, z = sup.marker.z) => internal(s, 'POST', `/api/internal/map-events/${sup.id}/claim`, { publicId: u.me.publicId, x, z });
  s.clock.t = sup.startAt - 1000;
  assert.equal((await claim(users[0])).status, 409, 'not active yet');
  at(s, sup, 1000);
  assert.equal((await internal(s, 'POST', `/api/internal/map-events/${sup.id}/claim`, { token: users[0].token, body: {} }, null)).status, 401);
  assert.equal((await claim(users[0], sup.marker.x + 100, sup.marker.z)).status, 422);
  const first = await claim(users[0]);
  assert.equal(first.status, 201); assert.equal(first.body.coins, 25); assert.ok(first.body.loot.length === 1);
  assert.equal((await claim(users[0])).status, 409);
  assert.equal((await wallet(s, users[0])).coins, 25);
  // capacity (6): simultaneous claims from the remaining 7 players
  const rs = await Promise.all(users.slice(1).map(u => claim(u)));
  assert.equal(rs.filter(r => r.status === 201).length, 5);
  assert.equal(rs.filter(r => r.status === 409).length, 2);
  at(s, sup, 91000);
  assert.equal((await claim(users[1])).status, 409, 'event expired');
  await s.close();
});

test('map events: objective — progress validated, replay safe, first team to finish wins and rewards once', async () => {
  const s = await server();
  const users = [];
  for (let i = 0; i < 4; i++) users.push(await s.register('obj' + i)); // teams 0,1,0,1
  const { body } = await plan(s, users, 'match-evt-6');
  const ob = body.events.find(e => e.type === 'objective');
  const prog = (u, rid, amount = 20, x = ob.marker.x, z = ob.marker.z, id = ob.id) => internal(s, 'POST', `/api/internal/map-events/${id}/progress`, { publicId: u.me.publicId, reportId: 'rpt-' + rid, amount, x, z });
  s.clock.t = ob.startAt - 1;
  assert.equal((await prog(users[0], 'r-early')).status, 409);
  at(s, ob, 1000);
  assert.equal((await prog(users[0], 'r-far', 20, ob.marker.x + 90, ob.marker.z)).status, 422);
  assert.equal((await prog(users[0], 'r-big', 90)).status, 422);
  const outsider = await s.register('objghost');
  assert.equal((await prog(outsider, 'r-ghost')).status, 422);
  assert.equal((await prog(users[0], 'r-1')).body.progress, 20);
  assert.equal((await prog(users[0], 'r-1')).body.duplicate, true);
  assert.equal((await prog(users[0], 'r-1')).body.duplicate, true);
  // team 1 progresses separately
  assert.equal((await prog(users[1], 'r-t1')).body.progress, 20);
  // team 0 finishes (target 100): 4 more reports of 20
  let last;
  for (const [u, id] of [[users[0], 'r-2'], [users[2], 'r-3'], [users[0], 'r-4'], [users[2], 'r-5']]) last = await prog(u, id);
  assert.equal(last.body.completed, true); assert.equal(last.body.winnerTeam, 0);
  assert.equal(last.body.rewarded.length, 2);
  assert.equal((await wallet(s, users[0])).coins, 60);
  assert.equal((await wallet(s, users[2])).coins, 60);
  assert.equal((await wallet(s, users[1])).coins, 0);
  // finished: no more progress or second completion
  assert.equal((await prog(users[1], 'r-late')).status, 409);
  assert.equal((await wallet(s, users[0])).coins, 60);
  await s.close();
});

test('map events: simultaneous finishing reports complete the objective exactly once', async () => {
  const s = await server();
  const users = [await s.register('race0'), await s.register('race1')];
  const { body } = await plan(s, users, 'match-evt-7');
  const ob = body.events.find(e => e.type === 'objective');
  at(s, ob, 1000);
  const bodyOf = (u, i) => ({ publicId: u.me.publicId, reportId: 'rpt-race-' + i, amount: 20, x: ob.marker.x, z: ob.marker.z });
  const rs = await Promise.all(Array.from({ length: 12 }, (_, i) => internal(s, 'POST', `/api/internal/map-events/${ob.id}/progress`, bodyOf(users[i % 2], i))));
  assert.equal(rs.filter(r => r.body && r.body.completed).length, 1);
  assert.equal((await wallet(s, users[0])).coins + (await wallet(s, users[1])).coins, 60);
  await s.close();
});

test('map events: operator config rejects out-of-bounds and blocked locations; per-match reward cap holds', async () => {
  const s = await server();
  const put = (id, body) => internal(s, 'PUT', `/api/internal/map-events/configs/${id}`, body);
  const base = { map: 'Foundry Yard', type: 'supply_drop', title: 'Test Drop', count: 1, earliestS: 60, latestS: 300, durationS: 60, announceLeadS: 10, rewardCoins: 10, capacity: 3, locations: [{ id: 'ok', x: 80, y: 0, z: 80, radius: 5 }] };
  assert.equal((await put('t_ok', base)).status, 200);
  assert.equal((await put('t_oob', { ...base, locations: [{ id: 'o', x: 149, y: 0, z: 0, radius: 5 }] })).status, 422);
  assert.equal((await put('t_blocked', { ...base, locations: [{ id: 'b', x: 0, y: 0, z: 0, radius: 5 }] })).status, 422);
  assert.equal((await put('t_nocap', { ...base, capacity: 0 })).status, 400);
  assert.equal((await put('t_badwin', { ...base, earliestS: 400, latestS: 100 })).status, 400);
  assert.equal((await internal(s, 'PUT', '/api/internal/map-events/configs/t_ok', base, null)).status, 401);
  assert.equal((await internal(s, 'PUT', '/api/internal/maps/Tiny', { minX: 0, maxX: -5, minZ: 0, maxZ: 5 })).status, 400);
  await s.close();
});

test('map events: state recovers after a server restart (file database) and cancelled events stop paying', async () => {
  const path = require('node:path'), os = require('node:os'), fs = require('node:fs');
  const dir = fs.mkdtempSync(path.join(os.tmpdir(), 'noxy-evt-'));
  const file = path.join(dir, 'db.sqlite');
  let s = await server(file);
  const users = [await s.register('rec0'), await s.register('rec1')];
  const { body } = await plan(s, users, 'match-evt-8');
  const ob = body.events.find(e => e.type === 'objective');
  at(s, ob, 1000);
  await internal(s, 'POST', `/api/internal/map-events/${ob.id}/progress`, { publicId: users[0].me.publicId, reportId: 'rpt-rec-1', amount: 20, x: ob.marker.x, z: ob.marker.z });
  const clockT = s.clock.t, tokens = users.map(u => u.token);
  await s.close();
  s = await server(file);
  s.clock.t = clockT;
  const view = (await internal(s, 'GET', '/api/internal/matches/match-evt-8/events')).body;
  const again = view.events.find(e => e.id === ob.id);
  assert.equal(again.phase, 'active'); assert.equal(again.progress[0].progress, 20);
  assert.equal(again.endsAt, ob.endsAt);
  assert.equal((await internal(s, 'POST', `/api/internal/map-events/${ob.id}/progress`, { publicId: users[0].me.publicId, reportId: 'rpt-rec-1', amount: 20, x: ob.marker.x, z: ob.marker.z })).body.duplicate, true);
  assert.equal((await internal(s, 'POST', `/api/internal/map-events/${ob.id}/cancel`, {})).status, 200);
  assert.equal((await internal(s, 'POST', `/api/internal/map-events/${ob.id}/progress`, { publicId: users[1].me.publicId, reportId: 'rpt-rec-9', amount: 20, x: ob.marker.x, z: ob.marker.z })).status, 409);
  assert.ok(tokens.length);
  await s.close();
  fs.rmSync(dir, { recursive: true, force: true });
});
