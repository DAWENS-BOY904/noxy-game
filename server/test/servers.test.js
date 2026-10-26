'use strict';
const test = require('node:test');
const assert = require('node:assert/strict');
const { startServer } = require('./helpers');

const KEY = 'test-server-key-123';
const boot = (servers) => startServer({ config: { economy: { matchServerKey: KEY }, ...(servers ? { servers } : {}) } });
const internal = (s, method, path, body, key = KEY) => fetch(s.base + path, {
  method, headers: { 'Content-Type': 'application/json', ...(key ? { 'x-noxy-server-key': key } : {}) },
  body: body === undefined ? undefined : JSON.stringify(body),
}).then(async r => ({ status: r.status, body: await r.json() }));

const spec = (extra = {}) => ({ serverId: 'eu-west-01', name: 'EU West 1', region: 'eu-west', mode: 'battle_royale', host: 'gs1.example.test', port: 7777, capacity: 60, version: '0.1.0', ...extra });
const listed = async (s, token, q = '') => (await s.call('GET', '/api/servers' + q, { token })).body.servers;

test('servers: registering needs the server key; a player token or wrong key cannot register or heartbeat', async () => {
  const s = await boot();
  try {
    const p = await s.register('srv_player');
    assert.equal((await internal(s, 'POST', '/api/internal/servers/register', spec(), null)).status, 401);
    assert.equal((await internal(s, 'POST', '/api/internal/servers/register', spec(), 'wrong')).status, 401);
    assert.equal((await s.call('POST', '/api/internal/servers/register', { token: p.token, body: spec() })).status, 401);
    assert.equal((await s.call('GET', '/api/servers')).status, 401, 'listing needs a signed-in player');
    assert.equal((await listed(s, p.token)).length, 0);
  } finally { await s.close(); }
});

test('servers: not configured means 503, never open registration', async () => {
  const s = await startServer({});
  try {
    const r = await internal(s, 'POST', '/api/internal/servers/register', spec(), 'anything');
    assert.equal(r.status, 503);
  } finally { await s.close(); }
});

test('servers: a record is not "online" until a heartbeat says so; stale heartbeats take it offline', async () => {
  const s = await boot();
  try {
    const p = await s.register('srv_player2');
    const reg = await internal(s, 'POST', '/api/internal/servers/register', spec());
    assert.equal(reg.status, 201);
    const { instanceToken } = reg.body;

    assert.equal((await listed(s, p.token)).length, 0, 'registered but unproven: not listed');
    const op = await internal(s, 'GET', '/api/internal/servers');
    assert.equal(op.body.servers[0].status, 'starting');

    const hb = await internal(s, 'POST', '/api/internal/servers/heartbeat', { serverId: 'eu-west-01', instanceToken, players: 12, status: 'online' });
    assert.equal(hb.status, 200); assert.equal(hb.body.status, 'online');
    const list = await listed(s, p.token);
    assert.equal(list.length, 1);
    assert.deepEqual({ id: list[0].id, region: list[0].region, mode: list[0].mode, players: list[0].players, capacity: list[0].capacity, host: list[0].host, port: list[0].port },
      { id: 'eu-west-01', region: 'eu-west', mode: 'battle_royale', players: 12, capacity: 60, host: 'gs1.example.test', port: 7777 });
    assert.equal(list[0].token_hash, undefined); assert.equal(list[0].instanceToken, undefined);

    s.clock.t += 31_000; // heartbeats stopped (server killed)
    assert.equal((await listed(s, p.token)).length, 0, 'stale server disappears from the browser');
    assert.equal((await internal(s, 'GET', '/api/internal/servers')).body.servers[0].status, 'offline');

    await internal(s, 'POST', '/api/internal/servers/heartbeat', { serverId: 'eu-west-01', instanceToken, players: 3 });
    assert.equal((await listed(s, p.token)).length, 1, 'a returning heartbeat revives it');

    s.clock.t += 25 * 3600 * 1000; // abandoned for over a day: record is purged
    await internal(s, 'POST', '/api/internal/servers/register', spec({ serverId: 'other-server-1' }));
    const gone = await internal(s, 'POST', '/api/internal/servers/heartbeat', { serverId: 'eu-west-01', instanceToken });
    assert.equal(gone.status, 404); assert.equal(gone.body.error.code, 'unknown_server');
  } finally { await s.close(); }
});

test('servers: heartbeats need the instance token; re-registering replaces it and resets to starting', async () => {
  const s = await boot();
  try {
    const p = await s.register('srv_player3');
    const a = (await internal(s, 'POST', '/api/internal/servers/register', spec())).body;
    await internal(s, 'POST', '/api/internal/servers/heartbeat', { serverId: 'eu-west-01', instanceToken: a.instanceToken, players: 1 });
    assert.equal((await listed(s, p.token)).length, 1);

    const bad = await internal(s, 'POST', '/api/internal/servers/heartbeat', { serverId: 'eu-west-01', instanceToken: 'x'.repeat(43), players: 1 });
    assert.equal(bad.status, 401);
    assert.equal((await internal(s, 'POST', '/api/internal/servers/heartbeat', { serverId: 'eu-west-01', players: 1 })).status, 401);

    const b = (await internal(s, 'POST', '/api/internal/servers/register', spec({ host: 'gs2.example.test' }))).body;
    assert.notEqual(b.instanceToken, a.instanceToken);
    assert.equal((await listed(s, p.token)).length, 0, 'a restarted server must prove health again');
    assert.equal((await internal(s, 'POST', '/api/internal/servers/heartbeat', { serverId: 'eu-west-01', instanceToken: a.instanceToken })).status, 401, 'old instance is locked out');
    assert.equal((await internal(s, 'POST', '/api/internal/servers/heartbeat', { serverId: 'eu-west-01', instanceToken: b.instanceToken })).status, 200);
    assert.equal((await listed(s, p.token))[0].host, 'gs2.example.test');
  } finally { await s.close(); }
});

test('servers: validation rejects bad input; player counts cannot exceed capacity', async () => {
  const s = await boot();
  try {
    for (const bad of [{ port: 0 }, { port: 70000 }, { port: 'x' }, { capacity: 0 }, { capacity: 201 }, { mode: 'nope' }, { host: 'a b' }, { host: 'http://evil' }, { region: 'EU West' }, { name: '' }, { serverId: 'A' }]) {
      const r = await internal(s, 'POST', '/api/internal/servers/register', spec(bad));
      assert.equal(r.status, 400, JSON.stringify(bad));
    }
    const { instanceToken } = (await internal(s, 'POST', '/api/internal/servers/register', spec())).body;
    assert.equal((await internal(s, 'POST', '/api/internal/servers/heartbeat', { serverId: 'eu-west-01', instanceToken, players: 61 })).status, 400);
    assert.equal((await internal(s, 'POST', '/api/internal/servers/heartbeat', { serverId: 'eu-west-01', instanceToken, players: -1 })).status, 400);
    assert.equal((await internal(s, 'POST', '/api/internal/servers/heartbeat', { serverId: 'eu-west-01', instanceToken, status: 'online-ish' })).status, 400);
  } finally { await s.close(); }
});

test('servers: draining servers are hidden, filters work, deregister removes the record', async () => {
  const s = await boot();
  try {
    const p = await s.register('srv_player4');
    const a = (await internal(s, 'POST', '/api/internal/servers/register', spec())).body;
    const b = (await internal(s, 'POST', '/api/internal/servers/register', spec({ serverId: 'us-east-01', region: 'us-east', mode: 'lone_wolf', name: 'US East' }))).body;
    await internal(s, 'POST', '/api/internal/servers/heartbeat', { serverId: 'eu-west-01', instanceToken: a.instanceToken });
    await internal(s, 'POST', '/api/internal/servers/heartbeat', { serverId: 'us-east-01', instanceToken: b.instanceToken });
    assert.equal((await listed(s, p.token)).length, 2);
    assert.deepEqual((await listed(s, p.token, '?region=us-east')).map(x => x.id), ['us-east-01']);
    assert.deepEqual((await listed(s, p.token, '?mode=battle_royale')).map(x => x.id), ['eu-west-01']);

    await internal(s, 'POST', '/api/internal/servers/heartbeat', { serverId: 'eu-west-01', instanceToken: a.instanceToken, status: 'draining' });
    assert.deepEqual((await listed(s, p.token)).map(x => x.id), ['us-east-01'], 'draining servers take no new players');

    assert.equal((await internal(s, 'POST', '/api/internal/servers/deregister', { serverId: 'us-east-01', instanceToken: 'x'.repeat(43) })).status, 401);
    assert.equal((await internal(s, 'POST', '/api/internal/servers/deregister', { serverId: 'us-east-01', instanceToken: b.instanceToken })).status, 200);
    assert.equal((await listed(s, p.token)).length, 0);
  } finally { await s.close(); }
});

test('servers: with a health probe configured, a server that claims online but is unreachable is not listed', async () => {
  const state = { reachable: false };
  const s = await boot({ probe: async () => state.reachable });
  try {
    const p = await s.register('srv_player5');
    const { instanceToken } = (await internal(s, 'POST', '/api/internal/servers/register', spec())).body;
    const hb = () => internal(s, 'POST', '/api/internal/servers/heartbeat', { serverId: 'eu-west-01', instanceToken, players: 2 });

    assert.equal((await hb()).body.status, 'starting');
    assert.equal((await listed(s, p.token)).length, 0, 'claims online, probe fails: not listed');

    state.reachable = true;
    assert.equal((await hb()).body.status, 'online');
    assert.equal((await listed(s, p.token)).length, 1);

    state.reachable = false; // port stops answering while heartbeats continue
    s.clock.t += 20_000; await hb();
    s.clock.t += 20_000; await hb();
    assert.equal((await listed(s, p.token)).length, 1, 'short probe failures are tolerated (grace window)');
    s.clock.t += 20_000; await hb();
    s.clock.t += 20_000; await hb();
    assert.equal((await listed(s, p.token)).length, 0, 'probe stopped passing, so it is no longer listed');
  } finally { await s.close(); }
});

test('servers: registry size is capped', async () => {
  const s = await boot({ maxServers: 2 });
  try {
    assert.equal((await internal(s, 'POST', '/api/internal/servers/register', spec({ serverId: 'aaa-1' }))).status, 201);
    assert.equal((await internal(s, 'POST', '/api/internal/servers/register', spec({ serverId: 'aaa-2' }))).status, 201);
    assert.equal((await internal(s, 'POST', '/api/internal/servers/register', spec({ serverId: 'aaa-3' }))).status, 429);
    assert.equal((await internal(s, 'POST', '/api/internal/servers/register', spec({ serverId: 'aaa-2', name: 'Renamed' }))).status, 201, 'existing ids may re-register');
  } finally { await s.close(); }
});
