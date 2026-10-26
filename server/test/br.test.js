'use strict';
// Battle Royale match authority. Clients here are AUTOMATED test accounts calling the real HTTP API of the real backend.
// That is evidence about the server's rules (phases, validation, authority), NOT evidence that real devices on a real
// network play correctly. Test setup occasionally pokes server memory (inventory, time); assertions go through the API.
const test = require('node:test');
const assert = require('node:assert/strict');
const { startServer } = require('./helpers');

const KEY = 'br-test-key-12345';
const HIGH = [1e6, 1000];
const limits = { brState: HIGH, brMove: HIGH, brAction: HIGH, internal: HIGH };
const internal = (s, method, path, body) => fetch(s.base + path, { method, headers: { 'Content-Type': 'application/json', 'x-noxy-server-key': KEY }, body: body === undefined ? undefined : JSON.stringify(body) }).then(async r => ({ status: r.status, body: await r.json() }));

async function world({ min = 4, max = 6, fill = 30_000, maxWait = 120_000, br = {}, registerServer = true } = {}) {
  const s = await startServer({ config: { economy: { matchServerKey: KEY }, limits, br: { modes: { battle_royale: { minPlayers: min, maxPlayers: max, fillTimeoutMs: fill, maxWaitMs: maxWait, belowMin: 'cancel', requireServer: true } }, ...br } } });
  if (registerServer) await gameServer(s);
  return s;
}
async function gameServer(s) {
  const r = await internal(s, 'POST', '/api/internal/servers/register', { serverId: 'br-eu-1', name: 'BR EU 1', region: 'eu', mode: 'battle_royale', host: 'gs.example.test', port: 7777, capacity: 100 });
  await internal(s, 'POST', '/api/internal/servers/heartbeat', { serverId: 'br-eu-1', instanceToken: r.body.instanceToken, players: 0 });
}
async function players(s, n, prefix = 'br') {
  const out = [];
  for (let i = 0; i < n; i++) {
    const p = await s.register(`${prefix}_${i}`);
    p.get = path => s.call('GET', path, { token: p.token });
    p.post = (path, body) => s.call('POST', path, { token: p.token, body: body || {} });
    p.del = path => s.call('DELETE', path, { token: p.token });
    out.push(p);
  }
  return out;
}
const queueAll = async ps => { for (const p of ps) assert.equal((await p.post('/api/br/queue', { mode: 'battle_royale' })).status, 201); };
const stateOf = async p => (await p.get('/api/br/state')).body;
const matchOf = (s) => [...s.app.br._matches.values()][0];
const adv = (s, ms) => { s.clock.t += ms; };

// puts the plane over `where` for the whole flight so that jumps land on known ground
function overhead(map, id) { const poi = map.pois.find(p => p.id === id); return { ax: poi.x - 200, az: poi.z, bx: poi.x + 200, bz: poi.z }; }
async function startToCombatReady(s, ps) { await queueAll(ps); adv(s, 31_000); await stateOf(ps[0]); }   // fill timeout passes, cabin starts

async function fall(s, p, st, { step = 0.5, freeRate = 45, chuteRate = 9 } = {}) {
  let { x, y, z, state } = st;
  for (let i = 0; i < 400 && state !== 'landed' && state !== 'eliminated'; i++) {
    const ground = s.app.br.map.heightAt(x, z);
    const ny = Math.max(ground, y - (state === 'freefall' ? freeRate : chuteRate) * step);
    adv(s, step * 1000);
    const r = await p.post('/api/br/move', { x, y: ny, z, anim: state === 'freefall' ? 'freefall' : 'glide' });
    assert.equal(r.status, 200, JSON.stringify(r.body));
    ({ x, y, z, state } = r.body);
  }
  return { x, y, z, state };
}

test('br: no verified game server means no queue and an honest error', async () => {
  const s = await world({ registerServer: false });
  try {
    const [a] = await players(s, 1);
    const r = await a.post('/api/br/queue', { mode: 'battle_royale' });
    assert.equal(r.status, 503); assert.equal(r.body.error.code, 'no_server_available');
    assert.equal((await a.get('/api/br/state')).status, 404, 'nothing was queued');
    // a registered but never-healthy server is not enough either
    await internal(s, 'POST', '/api/internal/servers/register', { serverId: 'br-cold-1', name: 'cold', region: 'eu', mode: 'battle_royale', host: 'x.example.test', port: 7777, capacity: 10 });
    assert.equal((await a.post('/api/br/queue', {})).status, 503);
  } finally { await s.close(); }
});

test('br: roster policy uses only real players: waits, starts at the timeout with enough, cancels when never enough', async () => {
  const s = await world({ min: 4, max: 6, fill: 30_000, maxWait: 120_000 });
  try {
    const ps = await players(s, 3);
    await queueAll(ps);
    let st = await stateOf(ps[0]);
    assert.equal(st.status, 'waiting'); assert.equal(st.waiting.players, 3); assert.equal(st.server.id, 'br-eu-1');
    adv(s, 31_000);
    st = await stateOf(ps[0]);
    assert.equal(st.status, 'waiting', 'below the minimum the server keeps waiting; it never invents players');
    assert.equal(st.players.length, 3);
    adv(s, 100_000);
    st = await stateOf(ps[0]);
    assert.equal(st.status, 'cancelled'); assert.equal(st.endReason, 'not_enough_players');
    assert.equal((await ps[0].post('/api/br/jump')).status, 409);
    // after a cancellation the same players can queue again
    await gameServer(s);
    assert.equal((await ps[0].post('/api/br/queue', {})).status, 201);
  } finally { await s.close(); }
});

test('br: the match starts only when the start condition holds, at the timeout with the minimum, or immediately when full', async () => {
  const s = await world({ min: 4, max: 6, fill: 30_000 });
  try {
    const ps = await players(s, 6);
    await queueAll(ps.slice(0, 4));
    assert.equal((await stateOf(ps[0])).status, 'waiting');
    adv(s, 31_000);
    assert.equal((await stateOf(ps[0])).status, 'cabin', '4 real players + timeout');
    const t = (await stateOf(ps[0])).times;
    assert.equal(t.cabinEndsAt - t.cabinStartsAt, 15_000, 'cabin phase is 15 s');
    assert.equal(t.jumpOpensAt - t.flightStartsAt, 5_000, '5 s quiet period');
    // a new arrival does not join a running match; it starts a fresh one
    await gameServer(s);
    const late = await ps[4].post('/api/br/queue', {});
    assert.equal(late.status, 201); assert.equal(late.body.status, 'waiting');
    assert.notEqual(late.body.matchId, (await stateOf(ps[0])).matchId);
  } finally { await s.close(); }
  const full = await world({ min: 4, max: 5, fill: 600_000 });
  try {
    const ps = await players(full, 5);
    await queueAll(ps);
    assert.equal((await stateOf(ps[0])).status, 'cabin', 'full roster starts without waiting for the timeout');
  } finally { await full.close(); }
});

test('br: 20 real accounts share one roster, valid non-overlapping cabin spawns and the same server-time schedule', async () => {
  const s = await startServer({ config: { economy: { matchServerKey: KEY }, limits } });   // default policy: 10-20 players
  try {
    await gameServer(s);
    const ps = await players(s, 20, 'big');
    await queueAll(ps);
    const views = [];
    for (const p of ps) views.push(await stateOf(p));
    assert.ok(views.every(v => v.status === 'cabin'));
    const roster = v => v.players.map(x => x.publicId + ':' + x.slot).join(',');
    assert.equal(new Set(views.map(roster)).size, 1, 'every client sees the same roster');
    assert.equal(new Set(views.map(v => JSON.stringify(v.times))).size, 1, 'same schedule for everyone');
    assert.equal(new Set(views.map(v => v.matchId)).size, 1);
    const pts = views[0].players;
    assert.equal(new Set(pts.map(p => p.slot)).size, 20);
    for (const p of pts) { assert.ok(Math.abs(p.x) <= 13 && Math.abs(p.z) <= 3, 'inside the cabin'); assert.equal(p.space, 'cabin'); }
    for (let i = 0; i < pts.length; i++) for (let j = i + 1; j < pts.length; j++) assert.ok(Math.hypot(pts[i].x - pts[j].x, pts[i].z - pts[j].z) >= 1.0, 'no overlapping characters');
  } finally { await s.close(); }
});

test('br: cabin phase - harmless movement and punches, bounds and speed enforced, no combat', async () => {
  const s = await world();
  try {
    const ps = await players(s, 4); await startToCombatReady(s, ps);
    const [a, b] = ps;
    const me = (await stateOf(a)).you;
    assert.equal((await a.post('/api/br/move', { x: me.x + 0.4, y: 0, z: me.z, anim: 'run' })).status, 200);
    adv(s, 500);
    assert.equal((await a.post('/api/br/move', { x: me.x + 0.5, y: 0, z: me.z, anim: 'punch', yaw: 90 })).status, 200);
    const seenByB = (await stateOf(b)).players.find(p => p.publicId === me.publicId);
    assert.equal(seenByB.anim, 'punch', 'animations are synchronised through the server');
    const tele = await a.post('/api/br/move', { x: 12, y: 0, z: 0, anim: 'run' });
    assert.equal(tele.status, 409); assert.equal(tele.body.error.code, 'too_fast'); assert.ok(tele.body.correction);
    assert.equal((await a.post('/api/br/move', { x: 99, y: 0, z: 0 })).body.error.code, 'outside_cabin');
    const bad = await a.post('/api/br/move', { x: me.x + 0.5, y: 0, z: me.z, anim: 'godmode' });
    assert.equal(bad.status, 200); assert.notEqual((await stateOf(a)).players.find(p => p.publicId === me.publicId).anim, 'godmode', 'unknown animations are ignored');
    // no weapon use and no damage before combat; a punch cannot hurt
    const f = await a.post('/api/br/fire', { dir: [1, 0, 0], damage: 9999, target: b.me.publicId });
    assert.equal(f.status, 409); assert.equal(f.body.error.code, 'combat_disabled');
    assert.equal((await stateOf(b)).you.health, 100);
    assert.equal((await a.post('/api/br/jump')).body.error.code, 'jump_not_open');
  } finally { await s.close(); }
});

test('br: jump window - closed in the cabin and the quiet period, opens at +5 s, and one jump never forces anyone else', async () => {
  const s = await world();
  try {
    const ps = await players(s, 4); await startToCombatReady(s, ps);
    const [a, b, c] = ps;
    const early = await a.post('/api/br/jump');
    assert.equal(early.status, 409); assert.equal(early.body.error.code, 'jump_not_open'); assert.ok(early.body.error.opensInMs > 0);
    adv(s, 15_000);                                         // flight begins
    let st = await stateOf(a);
    assert.equal(st.status, 'flight'); assert.equal(st.jump.open, false, 'quiet period: no jump button yet');
    assert.equal((await a.post('/api/br/jump')).status, 409);
    adv(s, 4_900); assert.equal((await stateOf(a)).jump.open, false);
    adv(s, 200);
    st = await stateOf(a); assert.equal(st.jump.open, true);
    assert.equal((await a.post('/api/br/jump')).status, 200);
    // others are untouched and choose their own moment
    for (const p of [b, c]) assert.equal((await stateOf(p)).you.state, 'aircraft');
    assert.equal((await stateOf(b)).players.find(x => x.publicId === a.me.publicId).state, 'freefall');
    assert.equal((await a.post('/api/br/jump')).body.error.code, 'already_jumped');
    adv(s, 10_000);
    assert.equal((await b.post('/api/br/jump')).status, 200);
    assert.equal((await stateOf(c)).you.state, 'aircraft', 'c still aboard');
    const ja = (await a.get('/api/br/state')).body.you, jb = (await b.get('/api/br/state')).body.you;
    assert.notDeepEqual([Math.round(ja.x), Math.round(ja.z)], [Math.round(jb.x), Math.round(jb.z)], 'players do not exit at the same point');
  } finally { await s.close(); }
});

test('br: server-enforced drop deadline ejects stragglers; combat starts for everyone at the same server time', async () => {
  const s = await world();
  try {
    const ps = await players(s, 4); await startToCombatReady(s, ps);
    adv(s, 30_000);            // 30 s after the cabin phase began: inside the jump window
    assert.equal((await ps[0].post('/api/br/jump')).status, 200);
    adv(s, 46_000);            // 76 s after cabin start: past flightEnd (75 s)
    const st = await stateOf(ps[1]);
    assert.equal(st.status, 'combat'); assert.equal(st.combat.enabled, true);
    assert.equal(st.you.state, 'freefall'); assert.equal(st.you.forced, true, 'ejected by the deadline, not by choice');
    assert.equal((await stateOf(ps[0])).you.forced, false);
    const times = new Set(); for (const p of ps) times.add(JSON.stringify((await stateOf(p)).times.combatStartsAt));
    assert.equal(times.size, 1);
  } finally { await s.close(); }
});

test('br: freefall -> parachute -> landing is validated (speed, climbing, deploy height, water relocation, hard landing)', async () => {
  const map = (await startServer({})).app.br.map;
  const s = await world({ br: { fixedRoute: overhead(map, 'clock') } });
  try {
    const ps = await players(s, 4); await startToCombatReady(s, ps);
    const [a, b] = ps;
    adv(s, 15_000 + 30_000);                                   // plane is over the Clock Tower plateau
    const j = await a.post('/api/br/jump');
    assert.equal(j.status, 200); assert.equal(j.body.state, 'freefall');
    assert.equal((await b.post('/api/br/jump')).status, 200);
    const poi = map.pois.find(p => p.id === 'clock');
    assert.ok(Math.hypot(j.body.x - poi.x, j.body.z - poi.z) < 40, 'exits where the plane is');

    // cheating attempts are rejected and answered with the authoritative position
    adv(s, 500);
    const tp = await a.post('/api/br/move', { x: j.body.x + 300, y: j.body.y - 10, z: j.body.z });
    assert.equal(tp.status, 409); assert.equal(tp.body.error.code, 'too_fast'); assert.equal(tp.body.correction.state, 'freefall');
    assert.equal((await a.post('/api/br/move', { x: j.body.x, y: j.body.y + 5, z: j.body.z })).body.error.code, 'cannot_climb');
    assert.equal((await a.post('/api/br/move', { x: j.body.x, y: j.body.y - 400, z: j.body.z })).body.error.code, 'too_fast');
    assert.equal((await a.post('/api/br/parachute')).status, 200, 'player may open the chute during freefall');
    assert.equal((await stateOf(a)).you.state, 'parachute');
    assert.equal((await a.post('/api/br/parachute')).body.error.code, 'not_in_freefall');

    const end = await fall(s, a, { ...j.body, state: 'parachute' });
    assert.equal(end.state, 'landed');
    const me = (await stateOf(a)).you;
    assert.equal(me.state, 'landed');
    assert.ok(Math.abs(me.y - map.heightAt(me.x, me.z)) < 0.5, 'lands on the ground, never through it');
    assert.equal(me.health, 100, 'gentle parachute landing costs nothing');

    // b: auto-deploy below the configured altitude, then a landing over the sea is moved to land
    const mm = matchOf(s), pb = mm.players.get(b.me.publicId);
    pb.lastSeen = s.clock.t;
    assert.equal(pb.state, 'freefall', 'b jumped by choice and is still in the air');
    pb.x = map.half - 20; pb.z = 0; pb.y = 60; pb.state = 'freefall'; pb.moveAt = s.clock.t;     // set up: falling toward open sea
    const endB = await fall(s, b, { x: pb.x, y: pb.y, z: pb.z, state: 'freefall' });
    assert.equal(endB.state, 'landed');
    const vb = (await stateOf(b)).you;
    assert.equal(vb.relocated, true, 'water landing is relocated');
    assert.ok(map.isValidLanding(vb.x, vb.z), 'to valid land');
  } finally { await s.close(); }
});

test('br: an over-fast freefall landing hurts, and cannot be survived by lying about speed', async () => {
  const map = (await startServer({})).app.br.map;
  const s = await world({ br: { fixedRoute: overhead(map, 'clock') } });
  try {
    const ps = await players(s, 4); await startToCombatReady(s, ps);
    adv(s, 45_000);
    const [a, b] = ps; await a.post('/api/br/jump'); await b.post('/api/br/jump');
    const m = matchOf(s), poi = map.pois.find(p => p.id === 'clock');
    const g = map.heightAt(poi.x, poi.z);
    s.app.br.cfg.freefall.autoDeployAltitude = 0;    // the normal auto-deploy would catch these dives; disable it for the test
    const setUp = (p, drop) => { const pl = m.players.get(p.me.publicId); pl.x = poi.x; pl.z = poi.z; pl.y = g + drop; pl.state = 'freefall'; pl.moveAt = s.clock.t; pl.lastSeen = s.clock.t; };
    setUp(a, 20); setUp(b, 60);
    adv(s, 1000);
    const hurt = await a.post('/api/br/move', { x: poi.x, y: g, z: poi.z, anim: 'freefall' });
    assert.equal(hurt.status, 200); assert.equal(hurt.body.state, 'landed');
    assert.ok(hurt.body.health < 100 && hurt.body.health > 0, 'a 20 m/s landing hurts but is survivable (' + hurt.body.health + ')');
    const dead = await b.post('/api/br/move', { x: poi.x, y: g, z: poi.z, anim: 'freefall' });
    assert.equal(dead.status, 200); assert.equal(dead.body.state, 'eliminated', 'a 60 m/s landing is fatal');
    const ev = (await stateOf(a)).events.find(e => e.type === 'elimination');
    assert.equal(ev.reason, 'fall');
  } finally { await s.close(); }
});

test('br: loot is validated by the server: distance, ownership, uniqueness; contents are server-decided', async () => {
  const map = (await startServer({})).app.br.map;
  const spawn = map.loot.has('L0001') ? [...map.loot.values()].find(l => l.kind === 'weapon' && l.tier >= 2 && l.y - map.heightAt(l.x, l.z) < 1.5) : null;
  assert.ok(spawn, 'a ground-floor weapon spawn exists in the exported map');
  const s = await world({ br: { fixedRoute: { ax: spawn.x - 200, az: spawn.z, bx: spawn.x + 200, bz: spawn.z } } });
  try {
    const ps = await players(s, 4); await startToCombatReady(s, ps);
    adv(s, 45_000);
    const [a, b] = ps;
    const ja = await a.post('/api/br/jump');
    assert.equal((await a.post('/api/br/loot/pickup', { lootId: spawn.id })).body.error.code, 'not_landed');
    const pa = matchOf(s).players.get(a.me.publicId);
    pa.x = spawn.x + 20; pa.z = spawn.z; pa.y = spawn.y + 0.5; pa.state = 'landed'; pa.space = 'world';
    const far = await a.post('/api/br/loot/pickup', { lootId: spawn.id });
    assert.equal(far.status, 409); assert.equal(far.body.error.code, 'too_far');
    pa.x = spawn.x + 1; pa.y = map.heightAt(pa.x, spawn.z);
    assert.equal((await a.post('/api/br/loot/pickup', { lootId: 'L-NOPE' })).body.error.code, 'unknown_loot');
    const ok = await a.post('/api/br/loot/pickup', { lootId: spawn.id, item: { type: 'weapon', id: 'rifle_ar', amount: 9999 } });
    assert.equal(ok.status, 200);
    assert.equal(ok.body.item.id, 'rifle_ar', 'tier-2 weapon spawn gives the server-chosen rifle');
    assert.equal(ok.body.you.weapons.primary.id, 'rifle_ar');
    assert.equal(ok.body.you.ammo.rifle, 0, 'client-supplied amounts are ignored');
    assert.equal((await a.post('/api/br/loot/pickup', { lootId: spawn.id })).body.error.code, 'already_taken');
    void ja; void b;
  } finally { await s.close(); }
});

// two or more players landed together with weapons, in combat phase
async function combatReady(s, ps, where = 'clock') {
  await startToCombatReady(s, ps);
  adv(s, 45_000);
  const m = matchOf(s), map = s.app.br.map, poi = map.pois.find(p => p.id === where);
  for (const [i, p] of ps.entries()) {
    const pl = m.players.get(p.me.publicId);
    pl.x = poi.x + i * 2; pl.z = poi.z; pl.y = map.heightAt(pl.x, pl.z); pl.state = 'landed'; pl.space = 'world'; pl.moveAt = s.clock.t; pl.landedAt = s.clock.t;
    pl.weapons.primary = { id: 'rifle_ar', mag: 30 }; pl.ammo.rifle = 90; pl.selected = 'primary';
  }
  adv(s, 31_000);          // pass the deadline: combat phase
  for (const p of ps) { p.pos = () => { const pl = m.players.get(p.me.publicId); return { x: pl.x, y: pl.y, z: pl.z }; }; p.hp = () => m.players.get(p.me.publicId).health; }
  return m;
}
const aim = (from, to, h = 1.0) => { const dx = to.x - from.x, dy = to.y + h - (from.y + 1.6), dz = to.z - from.z; return [dx, dy, dz]; };

test('br: combat is server-authoritative: damage, headshots, fire rate, ammo, reload, range', async () => {
  const map = (await startServer({})).app.br.map;
  const s = await world({ br: { fixedRoute: overhead(map, 'clock') } });
  try {
    const ps = await players(s, 4);
    const m = await combatReady(s, ps);
    const [a, b] = ps;
    assert.equal((await stateOf(a)).status, 'combat');
    // the client tries to dictate the outcome; the server ignores all of it
    const f1 = await a.post('/api/br/fire', { dir: aim(a.pos(), b.pos()), damage: 9999, kill: true, target: b.me.publicId, headshot: true });
    assert.equal(f1.status, 200); assert.equal(f1.body.hit.target, b.me.publicId);
    assert.equal(f1.body.hit.head, false);
    assert.ok(f1.body.hit.damage >= 19 && f1.body.hit.damage <= 20, 'rifle body damage with falloff, computed by the server: ' + f1.body.hit.damage);
    assert.equal(b.hp(), 100 - f1.body.hit.damage);
    assert.equal(f1.body.ammo.mag, 29);
    // fire rate
    const again = await a.post('/api/br/fire', { dir: aim(a.pos(), b.pos()) });
    assert.equal(again.status, 429); assert.equal(again.body.error.code, 'fire_rate');
    adv(s, 120);
    const head = await a.post('/api/br/fire', { dir: aim(a.pos(), b.pos(), 1.7) });
    assert.equal(head.body.hit.head, true); assert.ok(head.body.hit.damage >= 38, 'headshot multiplier ' + head.body.hit.damage);
    // a miss still costs ammo
    adv(s, 120);
    const miss = await a.post('/api/br/fire', { dir: [0, 1, 0.001] });
    assert.equal(miss.body.hit, null); assert.equal(miss.body.ammo.mag, 27);
    // magazine, reload, reserve
    m.players.get(a.me.publicId).weapons.primary.mag = 1;
    adv(s, 120); assert.equal((await a.post('/api/br/fire', { dir: [0, 1, 0.001] })).body.ammo.mag, 0);
    adv(s, 120); assert.equal((await a.post('/api/br/fire', { dir: [0, 1, 0.001] })).body.error.code, 'empty');
    assert.equal((await a.post('/api/br/reload')).status, 200);
    assert.equal((await a.post('/api/br/fire', { dir: [0, 1, 0.001] })).body.error.code, 'reloading');
    adv(s, 2100);
    const st = (await stateOf(a)).you;
    assert.equal(st.weapons.primary.mag, 30); assert.equal(st.ammo.rifle, 60);
    // invalid aim data and knives are range limited
    assert.equal((await a.post('/api/br/fire', { dir: 'forward' })).body.error.code, 'invalid_direction');
    assert.equal((await a.post('/api/br/fire', { dir: [0, 0, 0] })).body.error.code, 'invalid_direction');
    assert.equal((await a.post('/api/br/select', { slot: 'melee' })).status, 200);
    adv(s, 400);
    // aimed at the player 6 m away, the knife reaches only the player standing 2 m in the line of fire (range 2.2 m)
    const stab = await a.post('/api/br/fire', { dir: aim(a.pos(), ps[3].pos()) });
    assert.equal(stab.body.hit.target, b.me.publicId); assert.ok(stab.body.hit.distance <= 2.2);
    m.players.get(b.me.publicId).x += 5;          // now nobody is within knife reach
    adv(s, 600);
    assert.equal((await a.post('/api/br/fire', { dir: aim(a.pos(), ps[3].pos()) })).body.hit, null);
  } finally { await s.close(); }
});

test('br: eliminations, kill feed, placements and the final result feed the reward pipeline once', async () => {
  const map = (await startServer({})).app.br.map;
  const s = await world({ br: { fixedRoute: overhead(map, 'clock') } });
  try {
    const ps = await players(s, 4);
    await combatReady(s, ps);
    const [a, b, c, d] = ps;
    const kill = async (shooter, target) => {
      for (let i = 0; i < 40 && target.hp() > 0; i++) { adv(s, 150); const r = await shooter.post('/api/br/fire', { dir: aim(shooter.pos(), target.pos(), 1.7) }); if (r.status !== 200) { assert.ok(['empty', 'reloading', 'fire_rate'].includes(r.body.error.code), r.body.error.code); if (r.body.error.code === 'empty') { await shooter.post('/api/br/reload'); adv(s, 2100); } } }
      assert.ok(target.hp() <= 0);
    };
    await kill(a, b);
    let st = await stateOf(a);
    assert.equal(st.players.find(p => p.publicId === b.me.publicId).alive, false);
    const ev = st.events.find(e => e.type === 'elimination');
    assert.equal(ev.killer, a.me.publicId); assert.equal(ev.victim, b.me.publicId); assert.equal(ev.placement, 4);
    assert.equal((await b.post('/api/br/fire', { dir: [1, 0, 0] })).body.error.code, 'eliminated');
    assert.equal((await stateOf(c)).status, 'combat');
    await kill(a, c);
    await kill(a, d);
    st = await stateOf(a);
    assert.equal(st.status, 'ended'); assert.equal(st.endReason, 'last_standing');
    assert.deepEqual(st.results.map(r => [r.publicId, r.placement]), [[a.me.publicId, 1], [d.me.publicId, 2], [c.me.publicId, 3], [b.me.publicId, 4]].sort((x, y) => x[1] - y[1]));
    assert.equal(st.results[0].kills, 3);
    assert.equal(st.rewards.processed, true);
    const wins = s.app.db.prepare('SELECT wins, matches, kills FROM player_statistics WHERE player_id = (SELECT id FROM players WHERE public_id = ?)').get(a.me.publicId);
    assert.deepEqual({ ...wins }, { wins: 1, matches: 1, kills: 3 }, 'statistics come from the server result only');
    // the match is over: gameplay calls are refused, results stay readable, and the player can queue again
    assert.equal((await a.post('/api/br/jump')).body.error.code, 'match_over');
    assert.equal((await stateOf(b)).status, 'ended');
    await gameServer(s);
    assert.equal((await a.post('/api/br/queue', {})).status, 201);
    // replaying the same result through the trusted endpoint cannot pay twice
    const replay = await internal(s, 'POST', '/api/internal/match-results', { matchId: matchOf(s).id, mode: 'battle_royale', playerCount: 4, durationSeconds: 60, players: [{ publicId: a.me.publicId, placement: 1, kills: 3 }] });
    assert.equal(replay.body.duplicate, true);
  } finally { await s.close(); }
});

test('br: interest management hides distant players, disconnects are marked then abandoned and reported', async () => {
  const map = (await startServer({})).app.br.map;
  const s = await world({ br: { fixedRoute: overhead(map, 'clock') } });
  try {
    const ps = await players(s, 4);
    const m = await combatReady(s, ps);
    const [a, b, c, d] = ps;
    const far = m.players.get(d.me.publicId); far.x += 900; far.z += 900;
    const view = await stateOf(a);
    assert.equal(view.players.find(p => p.publicId === d.me.publicId).x, undefined, 'no position for a player 1 km away');
    assert.ok(view.players.find(p => p.publicId === b.me.publicId).x !== undefined, 'nearby players are visible');
    assert.equal(view.you.publicId, a.me.publicId);
    // c goes silent
    adv(s, 50_000);
    await stateOf(a); await stateOf(b); await stateOf(d);
    assert.equal((await stateOf(a)).players.find(p => p.publicId === c.me.publicId).connected, false);
    adv(s, 110_000);
    await stateOf(a); await stateOf(b); await stateOf(d);
    const after = await stateOf(a);
    assert.equal(after.players.find(p => p.publicId === c.me.publicId).alive, false);
    const abandoned = s.app.db.prepare("SELECT COUNT(*) c FROM reputation_events WHERE kind = 'abandon_seen' AND player_id = (SELECT id FROM players WHERE public_id = ?)").get(c.me.publicId).c;
    assert.equal(abandoned, 1, 'confirmed abandon recorded once through the fair-play system');
  } finally { await s.close(); }
});

test('br: cancelling the queue frees the slot and keeps cabin spawns unique; the operator endpoint is key-protected', async () => {
  const s = await world({ min: 4, max: 6 });
  try {
    const ps = await players(s, 3);
    await queueAll(ps);
    assert.equal((await ps[0].del('/api/br/queue')).status, 200);
    const st = await stateOf(ps[1]);
    assert.equal(st.waiting.players, 2);
    assert.deepEqual(st.players.map(p => p.slot), [0, 1]);
    assert.equal((await ps[0].get('/api/br/state')).status, 404);
    assert.equal((await s.call('GET', '/api/internal/br/matches', { token: ps[0].token })).status, 401);
    const ops = await internal(s, 'GET', '/api/internal/br/matches');
    assert.equal(ops.status, 200); assert.equal(ops.body.matches.length, 1);
    // after the match starts, leaving the queue is no longer possible
    const more = await players(s, 2, 'extra'); await queueAll(more);
    adv(s, 31_000); await stateOf(ps[1]);
    assert.equal((await ps[1].del('/api/br/queue')).status, 409);
  } finally { await s.close(); }
});

test('br: server publishes its rules so clients use the same numbers; map integrity info matches the exported map', async () => {
  const s = await world();
  try {
    const [a] = await players(s, 1);
    const cfg = (await a.get('/api/br/config')).body;
    assert.equal(cfg.cabinSeconds, 15); assert.equal(cfg.quietSeconds, 5); assert.ok(cfg.weapons.rifle_ar.damage > 0);
    const info = (await a.get('/api/br/map-info')).body;
    assert.equal(info.mapId, 'bermuda'); assert.match(info.hash, /^[0-9a-f]{16}$/); assert.equal(info.worldSize, 1200);
    assert.equal((await s.call('GET', '/api/br/config')).status, 401);
  } finally { await s.close(); }
});

test('br: opening the parachute allows a short deceleration window, then the normal parachute speed limit applies', async () => {
  const map = (await startServer({})).app.br.map;
  const s = await world({ br: { fixedRoute: overhead(map, 'clock') } });
  try {
    const ps = await players(s, 4); await startToCombatReady(s, ps);
    adv(s, 45_000);
    const a = ps[0]; await a.post('/api/br/jump');
    const pa = matchOf(s).players.get(a.me.publicId), poi = map.pois.find(p => p.id === 'clock'), g = map.heightAt(poi.x, poi.z);
    pa.x = poi.x; pa.z = poi.z; pa.y = g + 300; pa.state = 'freefall'; pa.moveAt = s.clock.t; pa.lastSeen = s.clock.t;
    assert.equal((await a.post('/api/br/parachute')).status, 200);
    // still falling at about 47 m/s for the first moments after opening: allowed
    adv(s, 500);
    assert.equal((await a.post('/api/br/move', { x: poi.x, y: g + 300 - 23, z: poi.z })).status, 200);
    // past the transition window the chute limit (12 m/s) applies, so a 47 m/s "chute" is a cheat
    adv(s, 2500);
    const cheat = await a.post('/api/br/move', { x: poi.x, y: g + 300 - 23 - 118, z: poi.z });
    assert.equal(cheat.status, 409); assert.equal(cheat.body.error.code, 'too_fast');
    adv(s, 500);
    assert.equal((await a.post('/api/br/move', { x: poi.x, y: g + 300 - 23 - 5, z: poi.z })).status, 200);
  } finally { await s.close(); }
});
test('br: a player who stops just above the (coarser) server terrain still touches down; one hovering high does not', async () => {
  const map = (await startServer({})).app.br.map;
  const s = await world({ br: { fixedRoute: overhead(map, 'clock') } });
  try {
    const ps = await players(s, 4); await startToCombatReady(s, ps);
    adv(s, 45_000);
    const [a, b] = ps; await a.post('/api/br/jump'); await b.post('/api/br/jump');
    const m = matchOf(s), poi = map.pois.find(p => p.id === 'clock'), g = map.heightAt(poi.x, poi.z);
    for (const [p, h] of [[a, 6], [b, 40]]) { const pl = m.players.get(p.me.publicId); pl.x = poi.x; pl.z = poi.z; pl.y = g + h; pl.state = 'parachute'; pl.chuteAt = 0; pl.moveAt = s.clock.t; pl.lastSeen = s.clock.t; }
    let ra, rb;
    for (let i = 0; i < 4; i++) {
      adv(s, 500);
      ra = await a.post('/api/br/move', { x: poi.x, y: g + 6, z: poi.z });
      rb = await b.post('/api/br/move', { x: poi.x, y: g + 40, z: poi.z });
    }
    assert.equal(ra.body.state, 'landed', '6 m above the server ground and not sinking for >1 s counts as grounded');
    assert.equal(rb.body.state, 'parachute', '40 m up is still in the air');
  } finally { await s.close(); }
});

test('br: shops and match-currency pickups are server-authoritative, phase-gated and idempotent', async () => {
  const s = await world();
  try {
    const ps = await players(s, 4); await startToCombatReady(s, ps);
    const a = ps[0];
    const m = matchOf(s);
    const me = m.players.get(a.me.publicId);

    let st = await stateOf(a);
    assert.ok(Array.isArray(st.shops) && st.shops.length > 0);
    assert.ok(Array.isArray(st.currency) && st.currency.length > 0);
    assert.equal((await a.post('/api/br/shop/open', { shopId: st.shops[0].id })).status, 409, 'shop closed before combat');

    adv(s, 80_000); // cabin+flight -> combat
    st = await stateOf(a);
    const shop = st.shops[0], cash = st.currency[0];
    me.state = 'landed'; me.x = cash.x; me.y = cash.y; me.z = cash.z; me.lastSeen = s.clock.t; me.moveAt = s.clock.t;
    const take = await a.post('/api/br/currency/pickup', { pickupId: cash.id });
    assert.equal(take.status, 200);
    assert.ok(take.body.amount > 0);
    assert.equal((await a.post('/api/br/currency/pickup', { pickupId: cash.id })).status, 409, 'cannot take one pickup twice');

    me.x = shop.x; me.y = shop.y; me.z = shop.z; me.lastSeen = s.clock.t; me.moveAt = s.clock.t;
    me.matchCoins = 500;
    const open = await a.post('/api/br/shop/open', { shopId: shop.id });
    assert.equal(open.status, 200);
    assert.ok(open.body.offers.some(o => o.weaponId === 'rifle_ar'));

    const buy = await a.post('/api/br/shop/buy', { shopId: shop.id, weaponId: 'rifle_ar', requestId: 'req-1' });
    assert.equal(buy.status, 200);
    assert.equal(buy.body.item.weaponId, 'rifle_ar');
    const after = me.matchCoins;
    const dup = await a.post('/api/br/shop/buy', { shopId: shop.id, weaponId: 'rifle_ar', requestId: 'req-1' });
    assert.equal(dup.status, 200);
    assert.equal(dup.body.duplicate, true);
    assert.equal(me.matchCoins, after, 'duplicate request does not spend again');

    assert.equal((await a.post('/api/br/shop/buy', { shopId: shop.id, weaponId: 'rifle_ar', requestId: 'req-2' })).status, 409, 'already owned in match');
    me.matchCoins = 0;
    assert.equal((await a.post('/api/br/shop/buy', { shopId: shop.id, weaponId: 'pistol_p9', requestId: 'req-3' })).status, 402, 'insufficient funds');
  } finally { await s.close(); }
});