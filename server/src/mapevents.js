'use strict';
// Configurable interactive map events (supply drops, contested objectives, loot zones, environmental events).
// The dedicated server owns the match: it asks this service to PLAN the events for a match, then reports
// objective progress and loot claims. Everything is validated here (roster, team, phase, position, capacity, caps) and
// persisted with absolute timestamps so a restart or reconnect can resume from GET without any in-memory state.
// Rewards go through the economy ledger with per-(event,player) keys, so a replay cannot pay twice.
const crypto = require('node:crypto');

const TYPES = ['supply_drop', 'objective', 'loot_zone', 'environmental'];
const MAX_COINS_PER_MATCH = 300;
const MAX_COINS_PER_DAY = 600;
const DAY = 86400000;

const SCHEMA = `
CREATE TABLE IF NOT EXISTS map_definitions (
  map    TEXT PRIMARY KEY,
  min_x  REAL NOT NULL, max_x REAL NOT NULL, min_z REAL NOT NULL, max_z REAL NOT NULL,
  blocked_json TEXT NOT NULL DEFAULT '[]',
  CHECK (min_x < max_x AND min_z < max_z)
);
CREATE TABLE IF NOT EXISTS map_event_configs (
  id TEXT PRIMARY KEY,
  map TEXT NOT NULL REFERENCES map_definitions(map) ON DELETE CASCADE,
  type TEXT NOT NULL CHECK (type IN ('supply_drop','objective','loot_zone','environmental')),
  title TEXT NOT NULL,
  description TEXT NOT NULL DEFAULT '',
  count INTEGER NOT NULL CHECK (count BETWEEN 1 AND 10),
  earliest_s INTEGER NOT NULL, latest_s INTEGER NOT NULL,
  duration_s INTEGER NOT NULL CHECK (duration_s BETWEEN 10 AND 900),
  announce_lead_s INTEGER NOT NULL CHECK (announce_lead_s BETWEEN 0 AND 300),
  reward_coins INTEGER NOT NULL DEFAULT 0 CHECK (reward_coins BETWEEN 0 AND 500),
  capacity INTEGER NOT NULL DEFAULT 0,
  objective_target INTEGER NOT NULL DEFAULT 0,
  loot_json TEXT NOT NULL DEFAULT '[]',
  locations_json TEXT NOT NULL,
  effect TEXT NOT NULL DEFAULT '',
  enabled INTEGER NOT NULL DEFAULT 1,
  CHECK (earliest_s <= latest_s)
);
CREATE TABLE IF NOT EXISTS map_events (
  id TEXT PRIMARY KEY,
  match_id TEXT NOT NULL,
  slot TEXT NOT NULL,
  config_id TEXT NOT NULL,
  type TEXT NOT NULL,
  title TEXT NOT NULL,
  description TEXT NOT NULL,
  location_id TEXT NOT NULL,
  x REAL NOT NULL, y REAL NOT NULL, z REAL NOT NULL, radius REAL NOT NULL,
  announce_at INTEGER NOT NULL, start_at INTEGER NOT NULL, ends_at INTEGER NOT NULL,
  reward_coins INTEGER NOT NULL, capacity INTEGER NOT NULL, objective_target INTEGER NOT NULL,
  loot_json TEXT NOT NULL, effect TEXT NOT NULL DEFAULT '',
  status TEXT NOT NULL DEFAULT 'planned' CHECK (status IN ('planned','completed','cancelled')),
  winner_team INTEGER,
  completed_at INTEGER,
  UNIQUE (match_id, slot)
);
CREATE INDEX IF NOT EXISTS idx_map_events_match ON map_events(match_id, start_at);
CREATE TABLE IF NOT EXISTS map_event_roster (
  match_id TEXT NOT NULL, player_id INTEGER NOT NULL REFERENCES players(id) ON DELETE CASCADE, team INTEGER NOT NULL,
  PRIMARY KEY (match_id, player_id)
);
CREATE TABLE IF NOT EXISTS map_event_progress (
  event_id TEXT NOT NULL REFERENCES map_events(id) ON DELETE CASCADE, team INTEGER NOT NULL, progress INTEGER NOT NULL DEFAULT 0,
  PRIMARY KEY (event_id, team)
);
CREATE TABLE IF NOT EXISTS map_event_reports (
  report_key TEXT PRIMARY KEY,
  event_id TEXT NOT NULL REFERENCES map_events(id) ON DELETE CASCADE,
  player_id INTEGER NOT NULL, team INTEGER NOT NULL, amount INTEGER NOT NULL, created_at INTEGER NOT NULL
);
CREATE TABLE IF NOT EXISTS map_event_claims (
  event_id TEXT NOT NULL REFERENCES map_events(id) ON DELETE CASCADE,
  player_id INTEGER NOT NULL REFERENCES players(id) ON DELETE CASCADE,
  claim_no INTEGER NOT NULL,
  coins INTEGER NOT NULL DEFAULT 0, reward_status TEXT NOT NULL DEFAULT 'paid',
  loot_json TEXT NOT NULL DEFAULT '[]',
  created_at INTEGER NOT NULL,
  PRIMARY KEY (event_id, player_id, claim_no)
);
`;

const SEED_MAP = { map: 'Foundry Yard', minX: -150, maxX: 150, minZ: -150, maxZ: 150, blocked: [{ x1: -25, z1: -25, x2: 25, z2: 25 }] };
const SEED_CONFIGS = [
  { id: 'fy_supply', map: 'Foundry Yard', type: 'supply_drop', title: 'Supply Drop', description: 'A crate is inbound. First come, first served.', count: 2, earliestS: 120, latestS: 600, durationS: 90, announceLeadS: 30, rewardCoins: 25, capacity: 6,
    loot: [{ itemId: 'medkit', qty: 2, weight: 5 }, { itemId: 'armor_plate', qty: 1, weight: 4 }, { itemId: 'ammo_pack', qty: 2, weight: 6 }, { itemId: 'grenade', qty: 2, weight: 3 }],
    locations: [{ id: 'n', x: 0, y: 0, z: 90, radius: 6 }, { id: 's', x: 0, y: 0, z: -90, radius: 6 }, { id: 'e', x: 90, y: 0, z: 0, radius: 6 }, { id: 'w', x: -90, y: 0, z: 0, radius: 6 }] },
  { id: 'fy_beacon', map: 'Foundry Yard', type: 'objective', title: 'Control Beacon', description: 'Hold the beacon to charge it. The first team to 100 wins the reward.', count: 1, earliestS: 240, latestS: 600, durationS: 150, announceLeadS: 45, rewardCoins: 60, objectiveTarget: 100,
    locations: [{ id: 'yard', x: 60, y: 0, z: 60, radius: 10 }, { id: 'dock', x: -60, y: 0, z: 60, radius: 10 }] },
  { id: 'fy_cache', map: 'Foundry Yard', type: 'loot_zone', title: 'Cache Zone', description: 'A limited stash of gear. Capacity is limited.', count: 1, earliestS: 180, latestS: 500, durationS: 180, announceLeadS: 30, rewardCoins: 15, capacity: 12,
    loot: [{ itemId: 'ammo_pack', qty: 3, weight: 5 }, { itemId: 'medkit', qty: 1, weight: 3 }],
    locations: [{ id: 'depot', x: -90, y: 0, z: -60, radius: 15 }, { id: 'tower', x: 90, y: 0, z: -60, radius: 15 }] },
  { id: 'fy_storm', map: 'Foundry Yard', type: 'environmental', title: 'Dust Storm', description: 'Visibility drops across the yard for a short time.', count: 1, earliestS: 300, latestS: 700, durationS: 60, announceLeadS: 30, effect: 'reduced_visibility',
    locations: [{ id: 'yard', x: 0, y: 0, z: 0, radius: 100 }] },
];

function create(ctx) {
  const { db, q, tx, fail, now, requireString, rateLimit, playerByPublicId, economy } = ctx;
  db.exec(SCHEMA);
  const inTx = fn => (db.isTransaction ? fn() : tx(fn));
  const isFlagged = id => !!q.one('SELECT 1 x FROM cheat_strikes WHERE player_id = ? AND flagged = 1', id);
  const dayStart = () => Math.floor(now() / DAY) * DAY;

  function mapDef(map) {
    const d = q.one('SELECT * FROM map_definitions WHERE map = ?', map);
    return d ? { map: d.map, minX: d.min_x, maxX: d.max_x, minZ: d.min_z, maxZ: d.max_z, blocked: JSON.parse(d.blocked_json) } : null;
  }
  const inBlocked = (d, x, z, r) => d.blocked.some(b => x + r > Math.min(b.x1, b.x2) && x - r < Math.max(b.x1, b.x2) && z + r > Math.min(b.z1, b.z2) && z - r < Math.max(b.z1, b.z2));
  const inBounds = (d, x, z, r) => x - r >= d.minX && x + r <= d.maxX && z - r >= d.minZ && z + r <= d.maxZ;
  // Environmental events cover large areas by design; blocked zones only matter for placed objects.
  const locationOk = (d, l, type) => Number.isFinite(l.x) && Number.isFinite(l.z) && Number.isFinite(l.y) && Number.isFinite(l.radius) && l.radius > 0 && l.radius <= 150
    && inBounds(d, l.x, l.z, l.radius) && (type === 'environmental' || !inBlocked(d, l.x, l.z, l.radius));

  function saveMap(body) {
    const m = requireString(body.map, 'map', 2, 40);
    for (const k of ['minX', 'maxX', 'minZ', 'maxZ']) if (!Number.isFinite(body[k])) fail(400, 'invalid_map', `${k} must be a number.`);
    if (body.minX >= body.maxX || body.minZ >= body.maxZ) fail(400, 'invalid_map', 'Bounds are inverted.');
    const blocked = Array.isArray(body.blocked) ? body.blocked : [];
    if (blocked.length > 50 || blocked.some(b => !['x1', 'z1', 'x2', 'z2'].every(k => Number.isFinite(b[k])))) fail(400, 'invalid_map', 'blocked must be up to 50 rectangles with x1,z1,x2,z2.');
    q.run('INSERT INTO map_definitions(map, min_x, max_x, min_z, max_z, blocked_json) VALUES (?,?,?,?,?,?) ON CONFLICT(map) DO UPDATE SET min_x=excluded.min_x, max_x=excluded.max_x, min_z=excluded.min_z, max_z=excluded.max_z, blocked_json=excluded.blocked_json',
      m, body.minX, body.maxX, body.minZ, body.maxZ, JSON.stringify(blocked));
  }

  function saveConfig(body) {
    const id = requireString(body.id, 'id', 3, 40);
    if (!/^[A-Za-z0-9_-]+$/.test(id)) fail(400, 'invalid_id', 'id may use letters, numbers, dashes and underscores.');
    const d = mapDef(String(body.map || ''));
    if (!d) fail(400, 'unknown_map', 'Define the map bounds first.');
    if (!TYPES.includes(body.type)) fail(400, 'invalid_type', 'type must be one of ' + TYPES.join(', '));
    const int = (v, lo, hi, name) => { if (!Number.isInteger(v) || v < lo || v > hi) fail(400, 'invalid_' + name, `${name} must be ${lo}-${hi}.`); return v; };
    const count = int(body.count, 1, 10, 'count');
    const earliest = int(body.earliestS, 0, 7200, 'earliestS'), latest = int(body.latestS, 0, 7200, 'latestS');
    if (earliest > latest) fail(400, 'invalid_window', 'earliestS must be <= latestS.');
    const duration = int(body.durationS, 10, 900, 'durationS'), lead = int(body.announceLeadS ?? 0, 0, 300, 'announceLeadS');
    const rewardCoins = int(body.rewardCoins ?? 0, 0, 500, 'rewardCoins');
    const capacity = int(body.capacity ?? 0, 0, 100, 'capacity'), target = int(body.objectiveTarget ?? 0, 0, 10000, 'objectiveTarget');
    if (body.type === 'objective' && target < 1) fail(400, 'invalid_objectiveTarget', 'Objectives need objectiveTarget >= 1.');
    if ((body.type === 'supply_drop' || body.type === 'loot_zone') && capacity < 1) fail(400, 'invalid_capacity', 'Loot events need capacity >= 1.');
    const loot = Array.isArray(body.loot) ? body.loot : [];
    if (loot.length > 30 || loot.some(l => typeof l.itemId !== 'string' || !/^[a-z0-9_]{2,40}$/.test(l.itemId) || !Number.isInteger(l.qty) || l.qty < 1 || l.qty > 99 || !Number.isInteger(l.weight) || l.weight < 1)) fail(400, 'invalid_loot', 'loot entries need itemId, qty 1-99 and weight >= 1.');
    const locs = Array.isArray(body.locations) ? body.locations : [];
    if (!locs.length || locs.length > 40) fail(400, 'invalid_locations', 'Provide 1-40 locations.');
    for (const l of locs) {
      if (typeof l.id !== 'string' || !/^[A-Za-z0-9_-]{1,20}$/.test(l.id)) fail(400, 'invalid_locations', 'Each location needs a short id.');
      if (!locationOk(d, l, body.type)) fail(422, 'location_out_of_bounds', `Location '${l.id}' is outside the map bounds or inside a blocked area.`);
    }
    if (new Set(locs.map(l => l.id)).size !== locs.length) fail(400, 'invalid_locations', 'Location ids must be unique.');
    q.run(`INSERT INTO map_event_configs(id, map, type, title, description, count, earliest_s, latest_s, duration_s, announce_lead_s, reward_coins, capacity, objective_target, loot_json, locations_json, effect, enabled)
      VALUES (?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?)
      ON CONFLICT(id) DO UPDATE SET map=excluded.map, type=excluded.type, title=excluded.title, description=excluded.description, count=excluded.count, earliest_s=excluded.earliest_s, latest_s=excluded.latest_s,
        duration_s=excluded.duration_s, announce_lead_s=excluded.announce_lead_s, reward_coins=excluded.reward_coins, capacity=excluded.capacity, objective_target=excluded.objective_target,
        loot_json=excluded.loot_json, locations_json=excluded.locations_json, effect=excluded.effect, enabled=excluded.enabled`,
      id, d.map, body.type, requireString(body.title, 'title', 3, 60), String(body.description || '').slice(0, 200), count, earliest, latest, duration, lead, rewardCoins, capacity, target,
      JSON.stringify(loot), JSON.stringify(locs), String(body.effect || '').slice(0, 40), body.enabled === false ? 0 : 1);
    return id;
  }

  // Seed defaults once (INSERT OR IGNORE semantics so operator edits survive restarts).
  if (!q.one('SELECT 1 x FROM map_definitions WHERE map = ?', SEED_MAP.map)) {
    saveMap(SEED_MAP);
    for (const c of SEED_CONFIGS) saveConfig(c);
  }

  // Deterministic PRNG: the same match seed always produces the same plan.
  function rng(seed) {
    let n = 0;
    return () => crypto.createHash('sha256').update(seed + ':' + (n++)).digest().readUInt32BE(0) / 0x100000000;
  }

  function planMatch({ matchId, map, durationSeconds, startedAt, players }) {
    return inTx(() => {
      const d = mapDef(map);
      if (!d) fail(404, 'unknown_map', 'No map definition for that map.');
      const existing = q.all('SELECT * FROM map_events WHERE match_id = ? ORDER BY start_at', matchId);
      if (existing.length) return { duplicate: true, events: existing.map(e => eventView(e)) };
      for (const p of players) q.run('INSERT OR IGNORE INTO map_event_roster(match_id, player_id, team) VALUES (?,?,?)', matchId, p.id, p.team);
      const seed = crypto.createHash('sha256').update(matchId).digest('hex');
      const rand = rng(seed);
      const used = [];
      for (const c of q.all('SELECT * FROM map_event_configs WHERE map = ? AND enabled = 1 ORDER BY id', map)) {
        const locs = JSON.parse(c.locations_json).filter(l => locationOk(d, l, c.type));
        if (!locs.length) continue;
        const lootTable = JSON.parse(c.loot_json);
        for (let i = 0; i < c.count; i++) {
          const minOff = Math.max(c.earliest_s, c.announce_lead_s);
          const maxOff = Math.min(c.latest_s, durationSeconds - c.duration_s);
          if (maxOff < minOff) continue;
          const startOff = Math.floor(minOff + rand() * (maxOff - minOff + 1));
          // Prefer a location not already used by an overlapping event of the same match.
          const free = locs.filter(l => !used.some(u => u.loc === l.id && u.cfg === c.id));
          const pool = free.length ? free : locs;
          const loc = pool[Math.floor(rand() * pool.length)];
          used.push({ cfg: c.id, loc: loc.id });
          const loot = [];
          if (lootTable.length) {
            const total = lootTable.reduce((n, l) => n + l.weight, 0);
            for (let k = 0; k < Math.max(1, c.capacity); k++) {
              let r = rand() * total, pick = lootTable[0];
              for (const l of lootTable) { r -= l.weight; if (r <= 0) { pick = l; break; } }
              loot.push({ itemId: pick.itemId, qty: pick.qty });
            }
          }
          const start = startedAt + startOff * 1000;
          q.run(`INSERT INTO map_events(id, match_id, slot, config_id, type, title, description, location_id, x, y, z, radius, announce_at, start_at, ends_at, reward_coins, capacity, objective_target, loot_json, effect)
            VALUES (?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?)`,
            'me_' + crypto.randomBytes(6).toString('hex'), matchId, `${c.id}#${i}`, c.id, c.type, c.title, c.description, loc.id, loc.x, loc.y, loc.z, loc.radius,
            start - c.announce_lead_s * 1000, start, start + c.duration_s * 1000, c.reward_coins, c.capacity, c.objective_target, JSON.stringify(loot), c.effect);
        }
      }
      return { duplicate: false, events: q.all('SELECT * FROM map_events WHERE match_id = ? ORDER BY start_at', matchId).map(e => eventView(e)) };
    });
  }

  function phaseOf(e) {
    if (e.status === 'completed') return 'completed';
    if (e.status === 'cancelled') return 'cancelled';
    const t = now();
    if (t < e.announce_at) return 'scheduled';
    if (t < e.start_at) return 'announced';
    if (t < e.ends_at) return 'active';
    return 'expired';
  }
  function eventView(e, withLoot = false) {
    const progress = q.all('SELECT team, progress FROM map_event_progress WHERE event_id = ? ORDER BY team', e.id).map(p => ({ team: p.team, progress: p.progress }));
    const claims = q.one('SELECT COUNT(*) c FROM map_event_claims WHERE event_id = ?', e.id).c;
    const phase = phaseOf(e);
    return {
      id: e.id, matchId: e.match_id, type: e.type, title: e.title, description: e.description, phase,
      announceAt: e.announce_at, startAt: e.start_at, endsAt: e.ends_at, serverNow: now(),
      countdownMs: phase === 'scheduled' ? e.announce_at - now() : phase === 'announced' ? e.start_at - now() : phase === 'active' ? e.ends_at - now() : 0,
      marker: { locationId: e.location_id, x: e.x, y: e.y, z: e.z, radius: e.radius },
      reward: { coins: e.reward_coins }, capacity: e.capacity, claimed: claims, remaining: Math.max(0, e.capacity - claims),
      objectiveTarget: e.objective_target, progress, winnerTeam: e.winner_team, effect: e.effect,
      ...(withLoot ? { loot: JSON.parse(e.loot_json) } : {}),
    };
  }

  function rosterEntry(matchId, publicId) {
    const p = playerByPublicId(publicId);
    const r = p && q.one('SELECT team FROM map_event_roster WHERE match_id = ? AND player_id = ?', matchId, p.id);
    if (!r) fail(422, 'not_in_match', 'That player is not on this match roster.');
    return { p, team: r.team };
  }
  function activeEvent(eventId) {
    const e = q.one('SELECT * FROM map_events WHERE id = ?', eventId);
    if (!e) fail(404, 'event_not_found', 'Unknown map event.');
    const ph = phaseOf(e);
    if (ph !== 'active') fail(409, 'event_not_active', `The event is ${ph}.`);
    return e;
  }
  const withinRange = (e, x, z) => Number.isFinite(x) && Number.isFinite(z) && Math.hypot(x - e.x, z - e.z) <= e.radius * 1.25 + 1;

  function coinsToday(playerId) {
    return q.one('SELECT COALESCE(SUM(coins),0) s FROM map_event_claims WHERE player_id = ? AND created_at >= ?', playerId, dayStart()).s;
  }
  function coinsThisMatch(playerId, matchId) {
    return q.one('SELECT COALESCE(SUM(c.coins),0) s FROM map_event_claims c JOIN map_events e ON e.id = c.event_id WHERE c.player_id = ? AND e.match_id = ?', playerId, matchId).s;
  }
  // Single reward path for every event type. Capped per match and per day, and held for flagged accounts.
  function grant(e, p, claimNo, coins, loot) {
    let paid = 0, status = 'paid';
    if (coins > 0) {
      if (isFlagged(p.id)) status = 'held';
      else {
        paid = Math.max(0, Math.min(coins, MAX_COINS_PER_MATCH - coinsThisMatch(p.id, e.match_id), MAX_COINS_PER_DAY - coinsToday(p.id)));
        if (paid > 0) economy.apply(p.id, 'coins', paid, { kind: 'map_event', refType: 'map_event', refId: e.id, key: `mapevent:${e.id}:${p.id}:${claimNo}` });
        else status = 'capped';
      }
    }
    q.run('INSERT INTO map_event_claims(event_id, player_id, claim_no, coins, reward_status, loot_json, created_at) VALUES (?,?,?,?,?,?,?)', e.id, p.id, claimNo, paid, status, JSON.stringify(loot), now());
    return { coins: paid, rewardStatus: status, loot };
  }

  function install(route) {
    // Players: read the schedule of a match they are on. Countdowns use absolute server timestamps.
    route('GET', '/api/matches/:matchId/events', ({ me, params }) => {
      if (!q.one('SELECT 1 x FROM map_event_roster WHERE match_id = ? AND player_id = ?', params.matchId, me.id)) fail(404, 'match_not_found', 'No events for that match.');
      const events = q.all('SELECT * FROM map_events WHERE match_id = ? ORDER BY start_at', params.matchId).map(e => eventView(e)).filter(v => v.phase !== 'scheduled');
      return { body: { serverNow: now(), events } };
    });

    // ---- trusted game server ----
    route('POST', '/api/internal/matches/:matchId/events/plan', ({ req, ip, params, body }) => {
      ctx.requireServerKey(req, ip);
      if (!/^[A-Za-z0-9_-]{4,64}$/.test(params.matchId)) fail(400, 'invalid_match', 'matchId is not valid.');
      const map = requireString(body.map, 'map', 2, 40);
      if (!Number.isInteger(body.durationSeconds) || body.durationSeconds < 120 || body.durationSeconds > 7200) fail(400, 'invalid_duration', 'durationSeconds must be 120-7200.');
      if (!Number.isInteger(body.startedAt) || Math.abs(body.startedAt - now()) > 3600000) fail(400, 'invalid_start', 'startedAt must be a recent epoch time in milliseconds.');
      if (!Array.isArray(body.players) || !body.players.length || body.players.length > 200) fail(400, 'invalid_players', 'players must be a list of 1-200 entries.');
      const players = body.players.map(e => {
        const p = playerByPublicId(e && e.publicId);
        if (!p || !Number.isInteger(e.team) || e.team < 0 || e.team > 63) fail(422, 'invalid_players', 'Every player needs a known publicId and a team 0-63.');
        return { id: p.id, team: e.team };
      });
      return { body: planMatch({ matchId: params.matchId, map, durationSeconds: body.durationSeconds, startedAt: body.startedAt, players }) };
    }, { auth: false });

    route('GET', '/api/internal/matches/:matchId/events', ({ req, ip, params }) => {
      ctx.requireServerKey(req, ip);
      return { body: { serverNow: now(), events: q.all('SELECT * FROM map_events WHERE match_id = ? ORDER BY start_at', params.matchId).map(e => eventView(e, true)) } };
    }, { auth: false });

    route('POST', '/api/internal/map-events/:id/progress', ({ req, ip, params, body }) => {
      ctx.requireServerKey(req, ip);
      return tx(() => {
        const e = activeEvent(params.id);
        if (e.type !== 'objective') fail(409, 'not_an_objective', 'This event has no objective progress.');
        const reportKey = requireString(body.reportId, 'reportId', 6, 80);
        const { p, team } = rosterEntry(e.match_id, body.publicId);
        if (!Number.isInteger(body.amount) || body.amount < 1 || body.amount > Math.max(1, Math.ceil(e.objective_target / 5))) fail(422, 'invalid_amount', 'amount is outside the allowed range for one report.');
        if (!withinRange(e, body.x, body.z)) fail(422, 'out_of_range', 'The player is not at the objective.');
        if (q.one('SELECT 1 x FROM map_event_reports WHERE report_key = ?', `${e.id}:${reportKey}`)) return { body: { applied: false, duplicate: true } };
        q.run('INSERT INTO map_event_reports(report_key, event_id, player_id, team, amount, created_at) VALUES (?,?,?,?,?,?)', `${e.id}:${reportKey}`, e.id, p.id, team, body.amount, now());
        q.run('INSERT OR IGNORE INTO map_event_progress(event_id, team, progress) VALUES (?,?,0)', e.id, team);
        q.run('UPDATE map_event_progress SET progress = min(?, progress + ?) WHERE event_id = ? AND team = ?', e.objective_target, body.amount, e.id, team);
        const prog = q.one('SELECT progress FROM map_event_progress WHERE event_id = ? AND team = ?', e.id, team).progress;
        let completed = false, rewarded = [];
        if (prog >= e.objective_target) {
          // The status guard makes completion atomic: only the first team to finish wins.
          const r = q.run("UPDATE map_events SET status = 'completed', winner_team = ?, completed_at = ? WHERE id = ? AND status = 'planned'", team, now(), e.id);
          if (r.changes) {
            completed = true;
            const contributors = q.all('SELECT DISTINCT player_id FROM map_event_reports WHERE event_id = ? AND team = ?', e.id, team);
            for (const c of contributors) {
              const pl = q.one('SELECT * FROM players WHERE id = ?', c.player_id);
              rewarded.push({ publicId: pl.public_id, ...grant(e, pl, 0, e.reward_coins, []) });
            }
          }
        }
        return { body: { applied: true, team, progress: prog, completed, winnerTeam: completed ? team : undefined, rewarded } };
      });
    }, { auth: false });

    route('POST', '/api/internal/map-events/:id/claim', ({ req, ip, params, body }) => {
      ctx.requireServerKey(req, ip);
      return tx(() => {
        const e = activeEvent(params.id);
        if (e.type !== 'supply_drop' && e.type !== 'loot_zone') fail(409, 'not_lootable', 'This event has no loot.');
        const { p } = rosterEntry(e.match_id, body.publicId);
        if (!withinRange(e, body.x, body.z)) fail(422, 'out_of_range', 'The player is not at the loot.');
        if (q.one('SELECT 1 x FROM map_event_claims WHERE event_id = ? AND player_id = ?', e.id, p.id)) fail(409, 'already_claimed', 'This player already collected from this event.');
        const n = q.one('SELECT COUNT(*) c FROM map_event_claims WHERE event_id = ?', e.id).c;
        if (n >= e.capacity) fail(409, 'event_depleted', 'Nothing is left to collect.');
        const loot = JSON.parse(e.loot_json);
        const mine = loot.length ? [loot[n % loot.length]] : [];
        return { status: 201, body: grant(e, p, 1, e.reward_coins, mine) };
      });
    }, { auth: false });

    route('POST', '/api/internal/map-events/:id/cancel', ({ req, ip, params }) => {
      ctx.requireServerKey(req, ip);
      const r = q.run("UPDATE map_events SET status = 'cancelled' WHERE id = ? AND status = 'planned'", params.id);
      if (!r.changes) fail(409, 'invalid_state', 'Only planned events can be cancelled.');
      return { body: { ok: true } };
    }, { auth: false });

    // ---- operator configuration ----
    route('PUT', '/api/internal/maps/:map', ({ req, ip, params, body }) => {
      ctx.requireServerKey(req, ip);
      saveMap({ ...body, map: params.map });
      return { body: { ok: true } };
    }, { auth: false });
    route('PUT', '/api/internal/map-events/configs/:id', ({ req, ip, params, body }) => {
      ctx.requireServerKey(req, ip);
      return { body: { id: saveConfig({ ...body, id: params.id }) } };
    }, { auth: false });
    route('GET', '/api/internal/map-events/configs', ({ req, ip, query }) => {
      ctx.requireServerKey(req, ip);
      const map = query.get('map');
      return { body: { configs: q.all(`SELECT * FROM map_event_configs ${map ? 'WHERE map = ?' : ''} ORDER BY id`, ...(map ? [map] : [])).map(c => ({ ...c, loot: JSON.parse(c.loot_json), locations: JSON.parse(c.locations_json) })) } };
    }, { auth: false });
  }

  return { install, planMatch, eventView, saveConfig, saveMap };
}

module.exports = { create, TYPES };
