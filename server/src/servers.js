'use strict';
// Dedicated-server registry.
//
// Trust model:
//  * Only a holder of the game-server key (x-noxy-server-key) can register, heartbeat or deregister. Clients can only READ.
//  * register() returns a per-instance token. Heartbeats must present it, so a server id cannot be hijacked by another
//    process that merely knows the id.
//  * A record existing does NOT mean a server is online. "online" requires: the server reported `online`, its last
//    heartbeat is fresher than ttlMs, and (when a probe is configured) a recent successful health probe. Everything else
//    is `starting`, `draining` or `offline`, and only `online` servers are listed to players.
//  * Stale records stop being listed immediately and are deleted after purgeAfterMs.
const crypto = require('node:crypto');
const net = require('node:net');

const ID_RE = /^[a-z0-9][a-z0-9-]{2,39}$/;
const REGION_RE = /^[a-z0-9-]{2,20}$/;
const HOST_RE = /^(?:[A-Za-z0-9](?:[A-Za-z0-9-]{0,61}[A-Za-z0-9])?)(?:\.[A-Za-z0-9](?:[A-Za-z0-9-]{0,61}[A-Za-z0-9])?)*$|^\[[0-9A-Fa-f:.]+\]$/;
const sha256 = s => crypto.createHash('sha256').update(s).digest('hex');

const SCHEMA = `
CREATE TABLE IF NOT EXISTS game_servers (
  id             TEXT PRIMARY KEY,
  name           TEXT NOT NULL,
  region         TEXT NOT NULL,
  mode           TEXT NOT NULL,
  host           TEXT NOT NULL,
  port           INTEGER NOT NULL CHECK (port BETWEEN 1 AND 65535),
  capacity       INTEGER NOT NULL CHECK (capacity BETWEEN 1 AND 200),
  players        INTEGER NOT NULL DEFAULT 0 CHECK (players >= 0),
  version        TEXT NOT NULL DEFAULT '',
  status         TEXT NOT NULL DEFAULT 'starting' CHECK (status IN ('starting','online','draining')),
  token_hash     TEXT NOT NULL,
  registered_at  INTEGER NOT NULL,
  last_heartbeat INTEGER NOT NULL,
  probe_ok_at    INTEGER NOT NULL DEFAULT 0,
  CHECK (players <= capacity)
);
CREATE INDEX IF NOT EXISTS idx_game_servers_heartbeat ON game_servers(last_heartbeat);
`;

/** TCP connect probe. Only useful when the game server also accepts TCP on its public port; heartbeats remain the primary signal. */
function tcpProbe(timeoutMs = 2000) {
  return (host, port) => new Promise(resolve => {
    const s = net.connect({ host: host.replace(/^\[|\]$/g, ''), port });
    const done = ok => { s.destroy(); resolve(ok); };
    s.setTimeout(timeoutMs, () => done(false));
    s.once('connect', () => done(true));
    s.once('error', () => done(false));
  });
}

function create(ctx) {
  const { db, q, tx, fail, now, config, catalog, rateLimit, requireServerKey } = ctx;
  const cfg = { ttlMs: 30_000, purgeAfterMs: 24 * 3600 * 1000, maxServers: 500, probe: null, ...(config.servers || {}) };
  db.exec(SCHEMA);

  const fresh = row => now() - row.last_heartbeat <= cfg.ttlMs;
  function effectiveStatus(row) {
    if (!fresh(row)) return 'offline';
    if (row.status === 'draining') return 'draining';
    if (row.status !== 'online') return 'starting';
    if (cfg.probe && now() - row.probe_ok_at > cfg.ttlMs * 2) return 'starting'; // asserted online but not (yet) reachable
    return 'online';
  }

  const view = row => ({
    id: row.id, name: row.name, region: row.region, mode: row.mode, host: row.host, port: row.port,
    capacity: row.capacity, players: row.players, version: row.version, status: effectiveStatus(row),
    lastSeenSeconds: Math.max(0, Math.round((now() - row.last_heartbeat) / 1000)),
  });

  function purge() {
    q.run('DELETE FROM game_servers WHERE last_heartbeat < ?', now() - cfg.purgeAfterMs);
  }

  function readStr(v, name, re, hint) {
    if (typeof v !== 'string' || !re.test(v)) fail(400, 'invalid_' + name, `${name} must ${hint}.`);
    return v;
  }
  const readInt = (v, name, min, max) => {
    if (!Number.isInteger(v) || v < min || v > max) fail(400, 'invalid_' + name, `${name} must be an integer from ${min} to ${max}.`);
    return v;
  };

  function authenticateInstance(body) {
    const id = readStr(body.serverId, 'serverId', ID_RE, 'be 3-40 lowercase letters, digits or dashes');
    const row = q.one('SELECT * FROM game_servers WHERE id = ?', id);
    if (!row) fail(404, 'unknown_server', 'This server is not registered (it may have expired). Register again.');
    const given = Buffer.from(sha256(typeof body.instanceToken === 'string' ? body.instanceToken : ''), 'hex');
    if (!crypto.timingSafeEqual(given, Buffer.from(row.token_hash, 'hex'))) fail(401, 'invalid_instance_token', 'Invalid instance token.');
    return row;
  }

  function install(route) {
    route('POST', '/api/internal/servers/register', ({ req, ip, body }) => {
      requireServerKey(req, ip);
      const id = body.serverId === undefined ? 'srv-' + crypto.randomBytes(4).toString('hex') : readStr(body.serverId, 'serverId', ID_RE, 'be 3-40 lowercase letters, digits or dashes');
      const name = typeof body.name === 'string' && body.name.length >= 1 && body.name.length <= 40 ? body.name : fail(400, 'invalid_name', 'name must be 1-40 characters.');
      const region = readStr(body.region, 'region', REGION_RE, 'be 2-20 lowercase letters, digits or dashes');
      if (typeof body.mode !== 'string' || !Object.prototype.hasOwnProperty.call(catalog.modes, body.mode)) fail(400, 'invalid_mode', 'mode must be one of: ' + Object.keys(catalog.modes).join(', '));
      const host = readStr(body.host, 'host', HOST_RE, 'be a DNS name or IP address');
      const port = readInt(body.port, 'port', 1, 65535);
      const capacity = readInt(body.capacity, 'capacity', 1, 200);
      const version = typeof body.version === 'string' ? body.version.slice(0, 32) : '';

      const token = crypto.randomBytes(32).toString('base64url');
      tx(() => {
        purge();
        const existing = q.one('SELECT id FROM game_servers WHERE id = ?', id);
        if (!existing && q.one('SELECT COUNT(*) c FROM game_servers').c >= cfg.maxServers) fail(429, 'registry_full', 'Too many registered servers.');
        // Re-registering (e.g. after a restart) replaces the instance token and resets to "starting": it must prove health again.
        q.run(`INSERT INTO game_servers(id, name, region, mode, host, port, capacity, players, version, status, token_hash, registered_at, last_heartbeat, probe_ok_at)
               VALUES (?,?,?,?,?,?,?,0,?, 'starting', ?, ?, ?, 0)
               ON CONFLICT(id) DO UPDATE SET name=excluded.name, region=excluded.region, mode=excluded.mode, host=excluded.host, port=excluded.port,
                 capacity=excluded.capacity, players=0, version=excluded.version, status='starting', token_hash=excluded.token_hash,
                 registered_at=excluded.registered_at, last_heartbeat=excluded.last_heartbeat, probe_ok_at=0`,
          id, name, region, body.mode, host, port, capacity, version, sha256(token), now(), now());
      });
      return { status: 201, body: { serverId: id, instanceToken: token, heartbeatSeconds: Math.max(1, Math.floor(cfg.ttlMs / 3000)), ttlSeconds: Math.floor(cfg.ttlMs / 1000) } };
    }, { auth: false });

    route('POST', '/api/internal/servers/heartbeat', async ({ req, ip, body }) => {
      requireServerKey(req, ip);
      const row = authenticateInstance(body);
      const status = body.status === undefined ? 'online' : body.status;
      if (status !== 'online' && status !== 'draining') fail(400, 'invalid_status', "status must be 'online' or 'draining'.");
      const players = readInt(body.players === undefined ? 0 : body.players, 'players', 0, row.capacity);
      let probeOk = row.probe_ok_at;
      if (cfg.probe) {
        let ok = false;
        try { ok = !!(await cfg.probe(row.host, row.port)); } catch { ok = false; }
        if (ok) probeOk = now();
      }
      q.run('UPDATE game_servers SET status = ?, players = ?, last_heartbeat = ?, probe_ok_at = ? WHERE id = ?', status, players, now(), probeOk, row.id);
      purge();
      return { body: { ok: true, status: effectiveStatus(q.one('SELECT * FROM game_servers WHERE id = ?', row.id)), ttlSeconds: Math.floor(cfg.ttlMs / 1000) } };
    }, { auth: false });

    route('POST', '/api/internal/servers/deregister', ({ req, ip, body }) => {
      requireServerKey(req, ip);
      const row = authenticateInstance(body);
      q.run('DELETE FROM game_servers WHERE id = ?', row.id);
      return { body: { ok: true } };
    }, { auth: false });

    // Operator view: every record with its computed status (never trust the stored status alone).
    route('GET', '/api/internal/servers', ({ req, ip }) => {
      requireServerKey(req, ip);
      return { body: { servers: q.all('SELECT * FROM game_servers ORDER BY id').map(view), ttlSeconds: Math.floor(cfg.ttlMs / 1000) } };
    }, { auth: false });

    // Player view: only servers that are verifiably online right now.
    route('GET', '/api/servers', ({ me, query }) => {
      rateLimit('search', me.id);
      const mode = query.get('mode');
      const region = query.get('region');
      const servers = q.all('SELECT * FROM game_servers ORDER BY region, name').map(view)
        .filter(s => s.status === 'online' && (!mode || s.mode === mode) && (!region || s.region === region));
      return { body: { servers, serverNow: now() } };
    });
  }

  return { install, effectiveStatus, view };
}

module.exports = { create, tcpProbe };
