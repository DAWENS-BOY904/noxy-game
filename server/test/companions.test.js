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
const dbId = (s, u) => s.app.db.prepare('SELECT id FROM players WHERE public_id = ?').get(u.me.publicId).id;
const credit = (s, u, cur, n, key) => s.app.economy.apply(dbId(s, u), cur, n, { kind: 'test', refType: 'test', refId: key, key });
const wallet = async (s, u) => (await s.call('GET', '/api/me/wallet', { token: u.token })).body;
const list = async (s, u) => (await s.call('GET', '/api/companions', { token: u.token })).body;

test('companions: catalog with default owned, equip/unequip, ownership enforced', async () => {
  const s = await server();
  const a = await s.register('petlover1');
  const v = await list(s, a);
  assert.ok(v.companions.length >= 6);
  assert.equal(v.companions.find(c => c.id === 'bolt').owned, true);
  assert.equal(v.companions.find(c => c.id === 'nova').owned, false);
  assert.equal((await s.call('PUT', '/api/me/companion', { token: a.token, body: { companionId: 'nova' } })).status, 403);
  assert.equal((await s.call('PUT', '/api/me/companion', { token: a.token, body: { companionId: 'bolt' } })).status, 200);
  assert.deepEqual((await list(s, a)).equipped, { companionId: 'bolt', variantId: 'default' });
  assert.equal((await s.call('PUT', '/api/me/companion', { token: a.token, body: { companionId: null } })).status, 200);
  assert.equal((await list(s, a)).equipped, null);
  assert.equal((await s.call('GET', '/api/companions')).status, 401);
  await s.close();
});

test('companions: server-priced unlocks, insufficient funds, level gates, no double charge, variants need the base', async () => {
  const s = await server();
  const a = await s.register('petlover2');
  const broke = await s.call('POST', '/api/companions/pip/unlock', { token: a.token, body: {} });
  assert.equal(broke.status, 402);
  assert.equal((await s.call('POST', '/api/companions/pip/unlock', { token: a.token, body: { price: 0 } })).status, 402, 'client price is ignored');
  credit(s, a, 'coins', 2000, 'seed-coins');
  assert.equal((await s.call('POST', '/api/companions/pip/unlock', { token: a.token, body: {} })).status, 201);
  assert.equal((await wallet(s, a)).coins, 1200);
  assert.equal((await s.call('POST', '/api/companions/pip/unlock', { token: a.token, body: {} })).status, 409);
  assert.equal((await wallet(s, a)).coins, 1200);
  // level gate
  const lvl = await s.call('POST', '/api/companions/orbit/unlock', { token: a.token, body: {} });
  assert.equal(lvl.status, 403); assert.equal(lvl.body.error.code, 'level_too_low');
  // variants require the base companion
  assert.equal((await s.call('POST', '/api/companions/cinder/unlock', { token: a.token, body: { variantId: 'frost' } })).status, 409);
  assert.equal((await s.call('POST', '/api/companions/pip/unlock', { token: a.token, body: { variantId: 'neon' } })).status, 402);
  credit(s, a, 'coins', 400, 'seed-2');
  assert.equal((await s.call('POST', '/api/companions/pip/unlock', { token: a.token, body: { variantId: 'neon' } })).status, 201);
  assert.equal((await wallet(s, a)).coins, 100);
  assert.equal((await s.call('POST', '/api/companions/ghost/unlock', { token: a.token, body: {} })).status, 404);
  await s.close();
});

test('companions: premium unlock debits diamonds once under concurrent requests', async () => {
  const s = await server();
  const a = await s.register('petlover3');
  credit(s, a, 'diamonds', 1000, 'seed-dia');
  const rs = await Promise.all(Array.from({ length: 5 }, () => s.call('POST', '/api/companions/cinder/unlock', { token: a.token, body: {} })));
  assert.equal(rs.filter(r => r.status === 201).length, 1);
  assert.equal((await wallet(s, a)).diamonds, 700);
  assert.equal((await s.call('PUT', '/api/me/companion', { token: a.token, body: { companionId: 'cinder' } })).status, 200);
  await s.close();
});

test('companions: ownership and equipped choice persist across restart; game server reads ids only', async () => {
  const path = require('node:path'), os = require('node:os'), fs = require('node:fs');
  const dir = fs.mkdtempSync(path.join(os.tmpdir(), 'noxy-pet-'));
  const file = path.join(dir, 'db.sqlite');
  let s = await server(file);
  const a = await s.register('petlover4');
  credit(s, a, 'coins', 900, 'seed');
  await s.call('POST', '/api/companions/pip/unlock', { token: a.token, body: {} });
  await s.call('PUT', '/api/me/companion', { token: a.token, body: { companionId: 'pip' } });
  const token = a.token, pid = a.me.publicId;
  await s.close();
  s = await server(file);
  assert.deepEqual((await s.call('GET', '/api/companions', { token })).body.equipped, { companionId: 'pip', variantId: 'default' });
  const mp = await internal(s, 'GET', `/api/internal/players/${pid}/match-profile`);
  assert.equal(mp.status, 200); assert.equal(mp.body.companion.companionId, 'pip');
  assert.equal((await internal(s, 'GET', `/api/internal/players/${pid}/match-profile`, undefined, null)).status, 401);
  // progression: idempotent per match
  assert.equal((await internal(s, 'POST', '/api/internal/companions/xp', { publicId: pid, matchId: 'match-0001', amount: 50 })).body.applied, true);
  assert.equal((await internal(s, 'POST', '/api/internal/companions/xp', { publicId: pid, matchId: 'match-0001', amount: 50 })).body.duplicate, true);
  assert.equal((await s.call('GET', '/api/companions', { token })).body.companions.find(c => c.id === 'pip').xp, 50);
  await s.close();
  fs.rmSync(dir, { recursive: true, force: true });
});
