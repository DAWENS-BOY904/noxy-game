'use strict';
const crypto = require('node:crypto');
const fs = require('node:fs');
const path = require('node:path');
const { DatabaseSync } = require('node:sqlite');

const catalog = JSON.parse(fs.readFileSync(path.join(__dirname, '..', 'data', 'catalog.json'), 'utf8'));
const ITEMS = new Map(catalog.items.map(i => [i.id, i]));
const SLOTS = new Set(catalog.items.map(i => i.slot));
const REQUIRED_SLOTS = new Set(['outfit', 'shoes']);

const HUD_ELEMENTS = new Set(['move_stick', 'look', 'fire', 'aim', 'jump', 'sprint', 'crouch', 'reload', 'interact',
  'slot1', 'slot2', 'slot3', 'grenade', 'map', 'inventory', 'minimap']);

const DEFAULT_CONFIG = {
  sessionTtlMs: 30 * 24 * 3600 * 1000,
  inviteTtlMs: 60 * 1000,
  inviteCooldownMs: 15 * 1000,
  maxPendingSentInvites: 5,
  presenceTimeoutMs: 30 * 1000,
  usernameChangeCooldownMs: 24 * 3600 * 1000,
  limits: { auth: [20, 60000], search: [20, 60000], invite: [30, 60000], friendRequest: [20, 60000], report: [5, 60000] },
  idGenerator: () => String(crypto.randomInt(1_000_000_000, 10_000_000_000)),
};

const CHEAT_CATEGORIES = ['aimbot', 'wallhack', 'speed_hack', 'abnormal_headshots', 'shoots_through_walls', 'walks_through_walls', 'abnormal_movement'];
const REPORT_CATEGORIES = [...CHEAT_CATEGORIES, 'abusive_chat', 'offensive_name', 'afk', 'other'];

class ApiError extends Error {
  constructor(status, code, message, extra) {
    super(message);
    this.status = status;
    this.code = code;
    this.extra = extra || {};
  }
}
const fail = (status, code, message, extra) => { throw new ApiError(status, code, message, extra); };

function createApp(options = {}) {
  const config = { ...DEFAULT_CONFIG, ...(options.config || {}), limits: { ...DEFAULT_CONFIG.limits, ...((options.config || {}).limits || {}) } };
  const now = options.now || (() => Date.now());
  const db = new DatabaseSync(options.dbPath || ':memory:');
  db.exec('PRAGMA foreign_keys = ON; PRAGMA journal_mode = WAL;');
  db.exec(fs.readFileSync(path.join(__dirname, 'schema.sql'), 'utf8'));
  // Dev databases created before report categories existed.
  if (!db.prepare('PRAGMA table_info(reports)').all().some((c) => c.name === 'category')) {
    db.exec("ALTER TABLE reports ADD COLUMN category TEXT NOT NULL DEFAULT 'other'");
  }
  db.exec(`CREATE TABLE IF NOT EXISTS cheat_strikes (
    player_id INTEGER PRIMARY KEY REFERENCES players(id) ON DELETE CASCADE,
    strikes   INTEGER NOT NULL DEFAULT 0,
    flagged   INTEGER NOT NULL DEFAULT 0,
    updated_at INTEGER NOT NULL
  )`);

  const q = {
    one: (sql, ...p) => db.prepare(sql).get(...p),
    all: (sql, ...p) => db.prepare(sql).all(...p),
    run: (sql, ...p) => db.prepare(sql).run(...p),
  };
  const tx = fn => {
    db.exec('BEGIN IMMEDIATE');
    try { const r = fn(); db.exec('COMMIT'); return r; } catch (e) { db.exec('ROLLBACK'); throw e; }
  };

  // ---------- rate limiting (in-memory sliding window) ----------
  const hits = new Map();
  function rateLimit(bucket, key) {
    const [max, windowMs] = config.limits[bucket];
    const k = bucket + ':' + key;
    const t = now();
    if (hits.size > 5000) { // keep memory bounded: drop buckets whose window has fully passed
      for (const [hk, arr] of hits) if (!arr.length || t - arr[arr.length - 1] > 120000) hits.delete(hk);
    }
    const arr = (hits.get(k) || []).filter(x => t - x < windowMs);
    if (arr.length >= max) {
      hits.set(k, arr);
      fail(429, 'rate_limited', 'Too many requests. Please slow down.', { retryAfter: Math.ceil((windowMs - (t - arr[0])) / 1000) });
    }
    arr.push(t);
    hits.set(k, arr);
  }

  // ---------- helpers ----------
  const sha256 = s => crypto.createHash('sha256').update(s).digest('hex');
  const hashPassword = (password, salt) => crypto.scryptSync(password, salt, 64).toString('hex');

  function requireString(v, name, min, max) {
    if (typeof v !== 'string' || v.length < min || v.length > max) fail(400, 'invalid_' + name, `${name} must be ${min}-${max} characters.`);
    return v;
  }
  const USERNAME_RE = /^[A-Za-z0-9_]{3,16}$/;
  const validUsername = u => typeof u === 'string' && USERNAME_RE.test(u);
  const validPublicId = id => typeof id === 'string' && /^[1-9][0-9]{9}$/.test(id);

  function playerByPublicId(publicId) {
    if (!validPublicId(publicId)) return null;
    return q.one('SELECT * FROM players WHERE public_id = ?', publicId) || null;
  }
  const areFriends = (a, b) => !!q.one('SELECT 1 x FROM friendships WHERE low_id = ? AND high_id = ?', Math.min(a, b), Math.max(a, b));
  const isBlockedEitherWay = (a, b) => !!q.one('SELECT 1 x FROM player_blocks WHERE (blocker_id=? AND blocked_id=?) OR (blocker_id=? AND blocked_id=?)', a, b, b, a);

  function notify(playerId, kind, title, body) {
    q.run('INSERT INTO notifications(player_id, kind, title, body, created_at) VALUES (?,?,?,?,?)', playerId, kind, title, body, now());
    // Keep only the latest 200 per player so the table cannot grow without bound.
    q.run('DELETE FROM notifications WHERE player_id = ? AND id <= (SELECT id FROM notifications WHERE player_id = ? ORDER BY id DESC LIMIT 1 OFFSET 200)', playerId, playerId);
  }

  function settingsRow(playerId) {
    return q.one('SELECT * FROM player_settings WHERE player_id = ?', playerId);
  }

  function statusFor(targetId, viewerId) {
    const s = settingsRow(targetId);
    const visibility = s ? s.online_status : 'friends';
    if (targetId !== viewerId) {
      if (visibility === 'nobody') return 'hidden';
      if (visibility === 'friends' && !areFriends(targetId, viewerId)) return 'hidden';
    }
    const p = q.one('SELECT * FROM presence WHERE player_id = ?', targetId);
    if (!p || now() - p.last_seen > config.presenceTimeoutMs) return 'offline';
    return p.state;
  }

  function loadoutOf(playerId) {
    const out = {};
    for (const r of q.all('SELECT slot, item_id FROM player_equipped_items WHERE player_id = ?', playerId)) out[r.slot] = r.item_id;
    return out;
  }
  const xpToNext = level => 500 + 150 * Math.max(0, level - 1);

  function statsOf(playerId) {
    const s = q.one('SELECT * FROM player_statistics WHERE player_id = ?', playerId) || {};
    return {
      matches: s.matches || 0, wins: s.wins || 0, top3: s.top3 || 0, kills: s.kills || 0,
      deaths: s.deaths || 0, headshots: s.headshots || 0, hoursPlayed: Math.round(((s.seconds_played || 0) / 3600) * 10) / 10,
    };
  }

  function relationship(viewerId, target) {
    if (viewerId === target.id) return 'self';
    if (areFriends(viewerId, target.id)) return 'friend';
    if (q.one("SELECT 1 x FROM friend_requests WHERE from_id=? AND to_id=? AND status='pending'", viewerId, target.id)) return 'request_sent';
    if (q.one("SELECT 1 x FROM friend_requests WHERE from_id=? AND to_id=? AND status='pending'", target.id, viewerId)) return 'request_received';
    return 'none';
  }

  function brief(viewerId, p) {
    return { publicId: p.public_id, username: p.username, level: p.level, rank: p.rank_name, status: statusFor(p.id, viewerId), relationship: relationship(viewerId, p) };
  }
  function publicProfile(viewerId, p) {
    return { ...brief(viewerId, p), stats: statsOf(p.id), loadout: loadoutOf(p.id) };
  }
  function meObject(p) {
    const s = settingsRow(p.id);
    return {
      publicId: p.public_id, username: p.username, level: p.level, xp: p.xp, xpToNext: xpToNext(p.level),
      credits: p.credits, cores: p.cores, rank: p.rank_name, createdAt: p.created_at,
      stats: statsOf(p.id), loadout: loadoutOf(p.id),
      privacy: { friendRequests: s.friend_requests, partyInvites: s.party_invites, onlineStatus: s.online_status },
    };
  }

  // ---------- auth ----------
  function createPlayer(username, password) {
    const salt = crypto.randomBytes(16).toString('hex');
    const hash = hashPassword(password, salt);
    for (let attempt = 0; attempt < 12; attempt++) {
      const publicId = config.idGenerator();
      try {
        return tx(() => {
          const r = q.run('INSERT INTO players(public_id, username, password_salt, password_hash, created_at, level, xp, credits, cores) VALUES (?,?,?,?,?,?,?,?,?)',
            publicId, username, salt, hash, now(), 1, 0, 0, 0);
          const id = Number(r.lastInsertRowid);
          q.run('INSERT INTO player_statistics(player_id) VALUES (?)', id);
          q.run('INSERT INTO player_settings(player_id, updated_at) VALUES (?,?)', id, now());
          for (const item of catalog.startingOwned) q.run('INSERT INTO player_inventory(player_id,item_id,acquired_at) VALUES (?,?,?)', id, item, now());
          for (const [slot, item] of Object.entries(catalog.startingLoadout)) q.run('INSERT INTO player_equipped_items(player_id,slot,item_id) VALUES (?,?,?)', id, slot, item);
          return id;
        });
      } catch (e) {
        const msg = String(e.message);
        if (msg.includes('players.public_id')) continue; // ID collision: generate another
        if (msg.includes('players.username')) fail(409, 'username_taken', 'That username is already taken.');
        throw e;
      }
    }
    fail(503, 'id_allocation_failed', 'Could not allocate a player ID. Please retry.');
  }

  function issueToken(playerId) {
    q.run('DELETE FROM sessions WHERE expires_at < ?', now()); // purge expired sessions opportunistically
    const token = crypto.randomBytes(32).toString('base64url');
    q.run('INSERT INTO sessions(token_hash, player_id, created_at, expires_at) VALUES (?,?,?,?)', sha256(token), playerId, now(), now() + config.sessionTtlMs);
    return token;
  }

  function authenticate(req) {
    const header = req.headers['authorization'] || '';
    const m = /^Bearer ([A-Za-z0-9_-]{20,100})$/.exec(header);
    if (!m) fail(401, 'unauthorized', 'Sign in required.');
    const s = q.one('SELECT * FROM sessions WHERE token_hash = ?', sha256(m[1]));
    if (!s || s.expires_at < now()) fail(401, 'unauthorized', 'Session expired. Please sign in again.');
    const p = q.one('SELECT * FROM players WHERE id = ?', s.player_id);
    if (!p) fail(401, 'unauthorized', 'Account no longer exists.');
    return { player: p, tokenHash: s.token_hash };
  }

  // ---------- invites / party ----------
  function expireInvites() {
    const t = now();
    const stale = q.all("SELECT * FROM party_invitations WHERE status='pending' AND expires_at <= ?", t);
    for (const inv of stale) {
      q.run("UPDATE party_invitations SET status='expired' WHERE id = ?", inv.id);
      const target = q.one('SELECT username FROM players WHERE id = ?', inv.to_id);
      notify(inv.from_id, 'invite_expired', 'Invitation expired', `${target ? target.username : 'Player'} did not respond in time.`);
    }
  }

  const modeMax = modeId => (catalog.modes[modeId] || { maxPartySize: 1 }).maxPartySize;
  const partyOf = playerId => q.one('SELECT p.* FROM parties p JOIN party_members m ON m.party_id = p.id WHERE m.player_id = ?', playerId);
  const memberCount = partyId => q.one('SELECT COUNT(*) c FROM party_members WHERE party_id = ?', partyId).c;

  function partyView(viewerId, party) {
    if (!party) return null;
    const members = q.all('SELECT m.ready, m.joined_at, p.* FROM party_members m JOIN players p ON p.id = m.player_id WHERE m.party_id = ? ORDER BY m.joined_at, p.id', party.id);
    return {
      id: party.id, modeId: party.mode_id, maxSize: modeMax(party.mode_id),
      leaderPublicId: members.find(m => m.id === party.leader_id)?.public_id || null,
      members: members.map(m => ({
        publicId: m.public_id, username: m.username, level: m.level, ready: !!m.ready, isLeader: m.id === party.leader_id,
        status: statusFor(m.id, viewerId),
      })),
    };
  }

  function removeFromParty(playerId) {
    const party = partyOf(playerId);
    if (!party) return;
    q.run('DELETE FROM party_members WHERE player_id = ?', playerId);
    const left = q.all('SELECT player_id FROM party_members WHERE party_id = ? ORDER BY joined_at', party.id);
    if (left.length === 0) { q.run('DELETE FROM parties WHERE id = ?', party.id); return; }
    if (party.leader_id === playerId) q.run('UPDATE parties SET leader_id = ? WHERE id = ?', left[0].player_id, party.id);
  }

  function incomingInvites(playerId) {
    expireInvites();
    return q.all(`SELECT i.id, i.mode_id, i.expires_at, i.party_id, p.public_id, p.username
      FROM party_invitations i JOIN players p ON p.id = i.from_id
      WHERE i.to_id = ? AND i.status = 'pending' ORDER BY i.id`, playerId)
      .map(i => ({ id: i.id, fromPublicId: i.public_id, fromUsername: i.username, modeId: i.mode_id, expiresAt: i.expires_at, partyId: i.party_id }));
  }

  // ---------- validation of bodies ----------
  function validateLoadout(playerId, body) {
    if (!body || typeof body.loadout !== 'object' || body.loadout === null || Array.isArray(body.loadout)) fail(400, 'invalid_loadout', 'loadout must be an object of slot -> itemId.');
    const result = {};
    for (const [slot, itemId] of Object.entries(body.loadout)) {
      if (!SLOTS.has(slot)) fail(400, 'invalid_slot', `Unknown slot '${slot}'.`);
      if (typeof itemId !== 'string' || itemId.length > 64) fail(400, 'invalid_item', 'Item id must be a string.');
      if (itemId === '') {
        if (REQUIRED_SLOTS.has(slot)) fail(400, 'slot_required', `${slot} cannot be empty.`);
        result[slot] = '';
        continue;
      }
      const item = ITEMS.get(itemId);
      if (!item || item.slot !== slot) fail(400, 'invalid_item', `Item '${itemId}' does not fit slot '${slot}'.`);
      if (!q.one('SELECT 1 x FROM player_inventory WHERE player_id = ? AND item_id = ?', playerId, itemId)) fail(403, 'not_owned', `You do not own ${item.name}.`);
      result[slot] = itemId;
    }
    return result;
  }

  const ENUMS = {
    friendRequests: ['everyone', 'nobody'],
    partyInvites: ['friends', 'nobody'],
    onlineStatus: ['everyone', 'friends', 'nobody'],
  };
  function validateSettingsData(data) {
    if (data === undefined) return undefined;
    if (typeof data !== 'object' || data === null || Array.isArray(data)) fail(400, 'invalid_settings', 'data must be an object.');
    const keys = Object.keys(data);
    if (keys.length > 100) fail(400, 'invalid_settings', 'Too many settings.');
    for (const k of keys) {
      const v = data[k];
      if (!/^[A-Za-z0-9_.]{1,64}$/.test(k)) fail(400, 'invalid_settings', `Bad setting key '${k}'.`);
      const t = typeof v;
      if (t === 'number') { if (!Number.isFinite(v) || Math.abs(v) > 1e6) fail(400, 'invalid_settings', `Bad value for '${k}'.`); }
      else if (t === 'string') { if (v.length > 64) fail(400, 'invalid_settings', `Value for '${k}' too long.`); }
      else if (t !== 'boolean') fail(400, 'invalid_settings', `Unsupported value type for '${k}'.`);
    }
    return data;
  }

  function validateHud(body) {
    if (!body || typeof body.elements !== 'object' || body.elements === null || Array.isArray(body.elements)) fail(400, 'invalid_hud', 'elements must be an object.');
    const out = {};
    for (const [id, e] of Object.entries(body.elements)) {
      if (!HUD_ELEMENTS.has(id)) fail(400, 'invalid_hud', `Unknown HUD element '${id}'.`);
      const num = (v, lo, hi, name) => { if (typeof v !== 'number' || !Number.isFinite(v) || v < lo || v > hi) fail(400, 'invalid_hud', `${id}.${name} must be ${lo}-${hi}.`); return v; };
      out[id] = { x: num(e.x, 0, 1, 'x'), y: num(e.y, 0, 1, 'y'), scale: num(e.scale, 0.5, 2, 'scale'), opacity: num(e.opacity, 0.2, 1, 'opacity') };
    }
    return { elements: out };
  }

  // ---------- routes ----------
  const routes = [];
  const route = (method, pattern, handler, opts = {}) => {
    const keys = [];
    const re = new RegExp('^' + pattern.replace(/:([A-Za-z]+)/g, (_, k) => { keys.push(k); return '([^/]+)'; }) + '$');
    routes.push({ method, re, keys, handler, auth: opts.auth !== false });
  };

  route('POST', '/api/auth/register', ({ body, ip }) => {
    rateLimit('auth', ip);
    if (!validUsername(body.username)) fail(400, 'invalid_username', 'Username must be 3-16 letters, numbers or underscores.');
    requireString(body.password, 'password', 8, 128);
    const id = createPlayer(body.username, body.password);
    const player = q.one('SELECT * FROM players WHERE id = ?', id);
    return { status: 201, body: { token: issueToken(id), player: meObject(player) } };
  }, { auth: false });

  route('POST', '/api/auth/login', ({ body, ip }) => {
    rateLimit('auth', ip);
    const generic = () => fail(401, 'invalid_credentials', 'Incorrect username or password.');
    if (typeof body.username !== 'string' || typeof body.password !== 'string' || body.password.length > 128) generic();
    const p = q.one('SELECT * FROM players WHERE username = ?', body.username);
    const salt = p ? p.password_salt : '00'.repeat(16); // equalise timing for unknown users
    const attempt = Buffer.from(hashPassword(body.password, salt), 'hex');
    if (!p || !crypto.timingSafeEqual(attempt, Buffer.from(p.password_hash, 'hex'))) generic();
    return { body: { token: issueToken(p.id), player: meObject(p) } };
  }, { auth: false });

  route('POST', '/api/auth/logout', ({ auth }) => {
    q.run('DELETE FROM sessions WHERE token_hash = ?', auth.tokenHash);
    return { body: { ok: true } };
  });

  route('GET', '/api/me', ({ me }) => ({ body: meObject(me) }));

  route('PATCH', '/api/me', ({ me, body }) => {
    if (!validUsername(body.username)) fail(400, 'invalid_username', 'Username must be 3-16 letters, numbers or underscores.');
    if (body.username.toLowerCase() === me.username.toLowerCase() && body.username === me.username) return { body: meObject(me) };
    if (me.username_changed_at && now() - me.username_changed_at < config.usernameChangeCooldownMs)
      fail(429, 'username_cooldown', 'You can change your name once per day.');
    try { q.run('UPDATE players SET username = ?, username_changed_at = ? WHERE id = ?', body.username, now(), me.id); }
    catch (e) { if (String(e.message).includes('players.username')) fail(409, 'username_taken', 'That username is already taken.'); throw e; }
    return { body: meObject(q.one('SELECT * FROM players WHERE id = ?', me.id)) };
  });

  route('DELETE', '/api/me', ({ me, body, ip }) => {
    rateLimit('auth', ip);
    requireString(body.password, 'password', 1, 128);
    const attempt = Buffer.from(hashPassword(body.password, me.password_salt), 'hex');
    if (!crypto.timingSafeEqual(attempt, Buffer.from(me.password_hash, 'hex'))) fail(403, 'invalid_credentials', 'Incorrect password.');
    tx(() => { removeFromParty(me.id); q.run('DELETE FROM players WHERE id = ?', me.id); });
    return { body: { ok: true } };
  });

  route('GET', '/api/me/inventory', ({ me }) => ({
    body: { owned: q.all('SELECT item_id FROM player_inventory WHERE player_id = ? ORDER BY item_id', me.id).map(r => r.item_id), loadout: loadoutOf(me.id) },
  }));

  route('PUT', '/api/me/loadout', ({ me, body }) => {
    const changes = validateLoadout(me.id, body);
    tx(() => {
      for (const [slot, itemId] of Object.entries(changes)) {
        if (itemId === '') q.run('DELETE FROM player_equipped_items WHERE player_id = ? AND slot = ?', me.id, slot);
        else q.run('INSERT INTO player_equipped_items(player_id, slot, item_id) VALUES (?,?,?) ON CONFLICT(player_id, slot) DO UPDATE SET item_id = excluded.item_id', me.id, slot, itemId);
      }
    });
    return { body: { loadout: loadoutOf(me.id) } };
  });

  route('POST', '/api/shop/purchase', () => fail(501, 'not_implemented', 'Purchases are not available yet.'));

  route('GET', '/api/me/settings', ({ me }) => {
    const s = settingsRow(me.id);
    return { body: { friendRequests: s.friend_requests, partyInvites: s.party_invites, onlineStatus: s.online_status, data: JSON.parse(s.data_json), updatedAt: s.updated_at } };
  });

  route('PUT', '/api/me/settings', ({ me, body }) => {
    const cur = settingsRow(me.id);
    const next = { friendRequests: cur.friend_requests, partyInvites: cur.party_invites, onlineStatus: cur.online_status };
    for (const k of Object.keys(ENUMS)) {
      if (body[k] === undefined) continue;
      if (!ENUMS[k].includes(body[k])) fail(400, 'invalid_settings', `${k} must be one of ${ENUMS[k].join(', ')}.`);
      next[k] = body[k];
    }
    const data = validateSettingsData(body.data);
    q.run('UPDATE player_settings SET friend_requests=?, party_invites=?, online_status=?, data_json=?, updated_at=? WHERE player_id=?',
      next.friendRequests, next.partyInvites, next.onlineStatus, data === undefined ? cur.data_json : JSON.stringify(data), now(), me.id);
    return { body: { ok: true } };
  });

  route('GET', '/api/me/hud/:key', ({ me, params }) => {
    if (!/^[a-z0-9_]{1,24}$/.test(params.key)) fail(400, 'invalid_key', 'Invalid layout key.');
    const r = q.one('SELECT data_json, updated_at FROM player_hud_layouts WHERE player_id = ? AND layout_key = ?', me.id, params.key);
    if (!r) fail(404, 'not_found', 'No saved layout.');
    return { body: { ...JSON.parse(r.data_json), updatedAt: r.updated_at } };
  });

  route('PUT', '/api/me/hud/:key', ({ me, params, body }) => {
    if (!/^[a-z0-9_]{1,24}$/.test(params.key)) fail(400, 'invalid_key', 'Invalid layout key.');
    const layout = validateHud(body);
    q.run('INSERT INTO player_hud_layouts(player_id, layout_key, data_json, updated_at) VALUES (?,?,?,?) ON CONFLICT(player_id, layout_key) DO UPDATE SET data_json=excluded.data_json, updated_at=excluded.updated_at',
      me.id, params.key, JSON.stringify(layout), now());
    return { body: { ok: true } };
  });

  route('GET', '/api/players/search', ({ me, query }) => {
    rateLimit('search', me.id);
    const text = (query.get('q') || '').trim();
    let rows = [];
    if (/^\d{10}$/.test(text)) {
      if (validPublicId(text)) rows = q.all('SELECT * FROM players WHERE public_id = ?', text);
    } else if (/^[A-Za-z0-9_]{3,16}$/.test(text)) {
      rows = q.all("SELECT * FROM players WHERE username = ? OR username LIKE ? ESCAPE '\\' ORDER BY (username = ?) DESC, username LIMIT 10",
        text, text.replace(/[\\%_]/g, c => '\\' + c) + '%', text);
    } else {
      fail(400, 'invalid_query', 'Enter a 10-digit player ID or a username (3-16 characters).');
    }
    const results = rows.filter(p => !isBlockedEitherWay(me.id, p.id)).map(p => brief(me.id, p));
    return { body: { results } };
  });

  route('GET', '/api/players/:publicId', ({ me, params }) => {
    const p = playerByPublicId(params.publicId);
    if (!p || isBlockedEitherWay(me.id, p.id)) fail(404, 'player_not_found', 'Player not found.');
    rateLimit('search', me.id);
    return { body: publicProfile(me.id, p) };
  });

  route('POST', '/api/players/:publicId/report', ({ me, params, body }) => {
    rateLimit('report', me.id);
    const p = playerByPublicId(params.publicId);
    if (!p || p.id === me.id) fail(404, 'player_not_found', 'Player not found.');
    const reason = requireString(body.reason, 'reason', 3, 300);
    const category = body.category === undefined ? 'other' : String(body.category);
    if (!REPORT_CATEGORIES.includes(category)) fail(400, 'invalid_category', 'Unknown report category.');
    const dayAgo = now() - 86400000;
    const dup = q.one('SELECT 1 AS x FROM reports WHERE reporter_id=? AND target_id=? AND category=? AND created_at>?', me.id, p.id, category, dayAgo);
    if (dup) return { status: 200, body: { ok: true, duplicate: true } };
    q.run('INSERT INTO reports(reporter_id, target_id, reason, category, created_at) VALUES (?,?,?,?,?)', me.id, p.id, reason, category, now());
    // Reports only queue a review. Distinct reporters on a cheat category raise a strike; nobody is auto-banned here.
    if (CHEAT_CATEGORIES.includes(category)) {
      const distinct = q.one('SELECT COUNT(DISTINCT reporter_id) AS n FROM reports WHERE target_id=? AND category IN (' + CHEAT_CATEGORIES.map(() => '?').join(',') + ') AND created_at>?',
        p.id, ...CHEAT_CATEGORIES, dayAgo).n;
      if (distinct >= 3) {
        q.run('INSERT INTO cheat_strikes(player_id, strikes, flagged, updated_at) VALUES (?,1,1,?) ON CONFLICT(player_id) DO UPDATE SET flagged=1, updated_at=excluded.updated_at', p.id, now());
      }
    }
    return { status: 201, body: { ok: true } };
  });

  route('GET', '/api/friends', ({ me }) => {
    expireInvites();
    const friends = q.all(`SELECT p.* FROM friendships f JOIN players p ON p.id = CASE WHEN f.low_id = ? THEN f.high_id ELSE f.low_id END
      WHERE f.low_id = ? OR f.high_id = ? ORDER BY p.username`, me.id, me.id, me.id).map(p => brief(me.id, p));
    const incoming = q.all("SELECT r.id rid, p.* FROM friend_requests r JOIN players p ON p.id = r.from_id WHERE r.to_id = ? AND r.status='pending' ORDER BY r.id", me.id)
      .map(r => ({ requestId: r.rid, player: brief(me.id, r) }));
    const outgoing = q.all("SELECT r.id rid, p.* FROM friend_requests r JOIN players p ON p.id = r.to_id WHERE r.from_id = ? AND r.status='pending' ORDER BY r.id", me.id)
      .map(r => ({ requestId: r.rid, player: brief(me.id, r) }));
    return { body: { friends, incoming, outgoing } };
  });

  route('POST', '/api/friends/requests', ({ me, body }) => {
    rateLimit('friendRequest', me.id);
    const target = playerByPublicId(body.publicId);
    if (!target || isBlockedEitherWay(me.id, target.id) && target.id !== me.id) fail(404, 'player_not_found', 'Player not found.');
    if (target.id === me.id) fail(400, 'self_request', 'You cannot add yourself.');
    if (areFriends(me.id, target.id)) fail(409, 'already_friends', 'You are already friends.');
    if (q.one("SELECT 1 x FROM friend_requests WHERE from_id=? AND to_id=? AND status='pending'", me.id, target.id)) fail(409, 'duplicate_request', 'Friend request already sent.');
    if (q.one("SELECT 1 x FROM friend_requests WHERE from_id=? AND to_id=? AND status='pending'", target.id, me.id)) fail(409, 'incoming_request_exists', 'This player already sent you a request. Accept it instead.');
    if (settingsRow(target.id).friend_requests === 'nobody') fail(403, 'requests_disabled', 'This player is not accepting friend requests.');
    const r = q.run('INSERT INTO friend_requests(from_id, to_id, created_at) VALUES (?,?,?)', me.id, target.id, now());
    notify(target.id, 'friend_request', 'Friend request', `${me.username} sent you a friend request.`);
    return { status: 201, body: { requestId: Number(r.lastInsertRowid) } };
  });

  function loadRequest(id, status = 'pending') {
    const n = Number(id);
    const r = Number.isInteger(n) ? q.one('SELECT * FROM friend_requests WHERE id = ?', n) : null;
    if (!r || r.status !== status) fail(404, 'request_not_found', 'Friend request not found.');
    return r;
  }

  route('POST', '/api/friends/requests/:id/accept', ({ me, params }) => {
    const r = loadRequest(params.id);
    if (r.to_id !== me.id) fail(404, 'request_not_found', 'Friend request not found.');
    if (isBlockedEitherWay(me.id, r.from_id)) fail(403, 'blocked', 'You cannot accept this request.');
    tx(() => {
      q.run("UPDATE friend_requests SET status='accepted' WHERE id = ?", r.id);
      q.run('INSERT OR IGNORE INTO friendships(low_id, high_id, created_at) VALUES (?,?,?)', Math.min(r.from_id, r.to_id), Math.max(r.from_id, r.to_id), now());
    });
    notify(r.from_id, 'friend_accepted', 'Friend request accepted', `${me.username} accepted your friend request.`);
    return { body: { ok: true } };
  });

  route('POST', '/api/friends/requests/:id/decline', ({ me, params }) => {
    const r = loadRequest(params.id);
    if (r.to_id !== me.id) fail(404, 'request_not_found', 'Friend request not found.');
    q.run("UPDATE friend_requests SET status='declined' WHERE id = ?", r.id);
    return { body: { ok: true } };
  });

  route('POST', '/api/friends/requests/:id/cancel', ({ me, params }) => {
    const r = loadRequest(params.id);
    if (r.from_id !== me.id) fail(404, 'request_not_found', 'Friend request not found.');
    q.run("UPDATE friend_requests SET status='cancelled' WHERE id = ?", r.id);
    return { body: { ok: true } };
  });

  route('DELETE', '/api/friends/:publicId', ({ me, params }) => {
    const p = playerByPublicId(params.publicId);
    if (!p || !areFriends(me.id, p.id)) fail(404, 'not_friends', 'Not in your friends list.');
    q.run('DELETE FROM friendships WHERE low_id = ? AND high_id = ?', Math.min(me.id, p.id), Math.max(me.id, p.id));
    return { body: { ok: true } };
  });

  route('GET', '/api/blocks', ({ me }) => ({
    body: { blocked: q.all('SELECT p.* FROM player_blocks b JOIN players p ON p.id = b.blocked_id WHERE b.blocker_id = ? ORDER BY p.username', me.id)
      .map(p => ({ publicId: p.public_id, username: p.username })) },
  }));

  route('POST', '/api/blocks', ({ me, body }) => {
    const p = playerByPublicId(body.publicId);
    if (!p) fail(404, 'player_not_found', 'Player not found.');
    if (p.id === me.id) fail(400, 'self_block', 'You cannot block yourself.');
    tx(() => {
      q.run('INSERT OR IGNORE INTO player_blocks(blocker_id, blocked_id, created_at) VALUES (?,?,?)', me.id, p.id, now());
      q.run('DELETE FROM friendships WHERE low_id = ? AND high_id = ?', Math.min(me.id, p.id), Math.max(me.id, p.id));
      q.run("UPDATE friend_requests SET status='cancelled' WHERE status='pending' AND ((from_id=? AND to_id=?) OR (from_id=? AND to_id=?))", me.id, p.id, p.id, me.id);
      q.run("UPDATE party_invitations SET status='cancelled' WHERE status='pending' AND ((from_id=? AND to_id=?) OR (from_id=? AND to_id=?))", me.id, p.id, p.id, me.id);
    });
    return { body: { ok: true } };
  });

  route('DELETE', '/api/blocks/:publicId', ({ me, params }) => {
    const p = playerByPublicId(params.publicId);
    if (!p) fail(404, 'player_not_found', 'Player not found.');
    q.run('DELETE FROM player_blocks WHERE blocker_id = ? AND blocked_id = ?', me.id, p.id);
    return { body: { ok: true } };
  });

  // ----- party -----
  route('GET', '/api/party', ({ me }) => ({ body: { party: partyView(me.id, partyOf(me.id)), invitations: incomingInvites(me.id) } }));

  route('POST', '/api/party/invite', ({ me, body }) => {
    rateLimit('invite', me.id);
    expireInvites();
    const target = playerByPublicId(body.publicId);
    if (!target || (target.id !== me.id && isBlockedEitherWay(me.id, target.id))) fail(404, 'player_not_found', 'Player not found.');
    if (target.id === me.id) fail(400, 'self_invite', 'You cannot invite yourself.');
    if (!areFriends(me.id, target.id)) fail(403, 'not_friends', 'You can only invite friends.');
    if (settingsRow(target.id).party_invites === 'nobody') fail(403, 'invites_disabled', 'This player is not accepting invitations.');

    const modeId = body.modeId === undefined ? (partyOf(me.id)?.mode_id || 'battle_royale') : body.modeId;
    if (!catalog.modes[modeId]) fail(400, 'invalid_mode', 'Unknown game mode.');
    if (modeMax(modeId) <= 1) fail(409, 'mode_not_supported', 'This game mode does not support parties.');

    let party = partyOf(me.id);
    if (party && party.leader_id !== me.id) fail(403, 'not_leader', 'Only the party leader can invite players.');
    if (party && party.mode_id !== modeId) fail(409, 'mode_mismatch', 'The party is queued for a different game mode.');
    if (party && memberCount(party.id) >= modeMax(modeId)) fail(409, 'party_full', 'Your party is full.');
    if (party && q.one('SELECT 1 x FROM party_members WHERE party_id = ? AND player_id = ?', party.id, target.id)) fail(409, 'already_in_party', 'That player is already in your party.');

    const t = now();
    const targetPresence = q.one('SELECT * FROM presence WHERE player_id = ?', target.id);
    if (targetPresence && targetPresence.state === 'in_match' && t - targetPresence.last_seen <= config.presenceTimeoutMs) fail(409, 'target_in_match', 'That player is in a match.');

    const last = q.one('SELECT created_at FROM party_invitations WHERE from_id = ? AND to_id = ? ORDER BY created_at DESC LIMIT 1', me.id, target.id);
    if (last && t - last.created_at < config.inviteCooldownMs)
      fail(429, 'invite_cooldown', 'Please wait before inviting this player again.', { retryAfter: Math.ceil((config.inviteCooldownMs - (t - last.created_at)) / 1000) });
    const pending = q.one("SELECT COUNT(*) c FROM party_invitations WHERE from_id = ? AND status='pending'", me.id).c;
    if (pending >= config.maxPendingSentInvites) fail(429, 'too_many_invites', 'You have too many pending invitations.');

    const inviteId = tx(() => {
      if (!party) {
        const r = q.run('INSERT INTO parties(leader_id, mode_id, created_at) VALUES (?,?,?)', me.id, modeId, t);
        q.run('INSERT INTO party_members(party_id, player_id, ready, joined_at) VALUES (?,?,0,?)', Number(r.lastInsertRowid), me.id, t);
        party = q.one('SELECT * FROM parties WHERE id = ?', Number(r.lastInsertRowid));
      }
      const r = q.run('INSERT INTO party_invitations(party_id, from_id, to_id, mode_id, created_at, expires_at) VALUES (?,?,?,?,?,?)', party.id, me.id, target.id, modeId, t, t + config.inviteTtlMs);
      return Number(r.lastInsertRowid);
    });
    notify(target.id, 'party_invite', 'Party invitation', `${me.username} invited you to play ${modeId.replace('_', ' ')}.`);
    return { status: 201, body: { invitationId: inviteId, expiresAt: t + config.inviteTtlMs } };
  });

  function loadInvite(id, me) {
    expireInvites();
    const n = Number(id);
    const inv = Number.isInteger(n) ? q.one('SELECT * FROM party_invitations WHERE id = ?', n) : null;
    if (!inv || inv.to_id !== me.id) fail(404, 'invitation_not_found', 'Invitation not found.');
    if (inv.status === 'expired') fail(410, 'invitation_expired', 'This invitation has expired.');
    if (inv.status !== 'pending') fail(409, 'invitation_closed', 'This invitation is no longer available.');
    return inv;
  }

  route('POST', '/api/party/invitations/:id/accept', ({ me, params }) => {
    const inv = loadInvite(params.id, me);
    const party = q.one('SELECT * FROM parties WHERE id = ?', inv.party_id);
    if (!party) { q.run("UPDATE party_invitations SET status='cancelled' WHERE id = ?", inv.id); fail(410, 'party_gone', 'That party no longer exists.'); }
    if (isBlockedEitherWay(me.id, inv.from_id)) fail(403, 'blocked', 'You cannot join this party.');
    if (memberCount(party.id) >= modeMax(party.mode_id)) fail(409, 'party_full', 'That party is full.');
    tx(() => {
      const current = partyOf(me.id);
      if (!current || current.id !== party.id) {
        removeFromParty(me.id);
        q.run('INSERT INTO party_members(party_id, player_id, ready, joined_at) VALUES (?,?,0,?)', party.id, me.id, now());
      }
      q.run("UPDATE party_invitations SET status='accepted' WHERE id = ?", inv.id);
    });
    notify(inv.from_id, 'invite_accepted', 'Invitation accepted', `${me.username} joined your party.`);
    return { body: { party: partyView(me.id, partyOf(me.id)) } };
  });

  route('POST', '/api/party/invitations/:id/decline', ({ me, params }) => {
    const inv = loadInvite(params.id, me);
    q.run("UPDATE party_invitations SET status='declined' WHERE id = ?", inv.id);
    notify(inv.from_id, 'invite_declined', 'Invitation declined', `${me.username} declined your invitation.`);
    return { body: { ok: true } };
  });

  route('POST', '/api/party/leave', ({ me }) => {
    tx(() => removeFromParty(me.id));
    return { body: { ok: true } };
  });

  route('POST', '/api/party/ready', ({ me, body }) => {
    if (typeof body.ready !== 'boolean') fail(400, 'invalid_ready', 'ready must be true or false.');
    if (!partyOf(me.id)) fail(404, 'no_party', 'You are not in a party.');
    q.run('UPDATE party_members SET ready = ? WHERE player_id = ?', body.ready ? 1 : 0, me.id);
    return { body: { party: partyView(me.id, partyOf(me.id)) } };
  });

  route('POST', '/api/party/mode', ({ me, body }) => {
    const party = partyOf(me.id);
    if (!party) fail(404, 'no_party', 'You are not in a party.');
    if (party.leader_id !== me.id) fail(403, 'not_leader', 'Only the party leader can change the mode.');
    if (!catalog.modes[body.modeId]) fail(400, 'invalid_mode', 'Unknown game mode.');
    if (memberCount(party.id) > modeMax(body.modeId)) fail(409, 'party_too_large', 'Your party is too large for that mode.');
    tx(() => {
      q.run('UPDATE parties SET mode_id = ? WHERE id = ?', body.modeId, party.id);
      q.run('UPDATE party_members SET ready = 0 WHERE party_id = ?', party.id);
      q.run("UPDATE party_invitations SET status='cancelled' WHERE party_id = ? AND status='pending' AND mode_id <> ?", party.id, body.modeId);
    });
    return { body: { party: partyView(me.id, partyOf(me.id)) } };
  });

  route('POST', '/api/presence', ({ me, body }) => {
    if (!['lobby', 'in_match', 'offline'].includes(body.state)) fail(400, 'invalid_state', 'Invalid presence state.');
    q.run('INSERT INTO presence(player_id, state, last_seen) VALUES (?,?,?) ON CONFLICT(player_id) DO UPDATE SET state=excluded.state, last_seen=excluded.last_seen', me.id, body.state, now());
    return { body: { ok: true } };
  });

  route('GET', '/api/notifications', ({ me, query }) => {
    expireInvites();
    const after = Number.parseInt(query.get('after') || '0', 10) || 0;
    const rows = q.all('SELECT * FROM notifications WHERE player_id = ? AND id > ? ORDER BY id LIMIT 50', me.id, after);
    return { body: { notifications: rows.map(n => ({ id: n.id, kind: n.kind, title: n.title, body: n.body, createdAt: n.created_at, read: !!n.is_read })) } };
  });

  route('POST', '/api/notifications/read', ({ me }) => {
    q.run('UPDATE notifications SET is_read = 1 WHERE player_id = ?', me.id);
    return { body: { ok: true } };
  });

  // ---------- HTTP ----------
  const MAX_BODY = 64 * 1024;
  function readBody(req) {
    return new Promise((resolve, reject) => {
      const chunks = []; let size = 0;
      req.on('data', c => { size += c.length; if (size > MAX_BODY) { reject(new ApiError(413, 'payload_too_large', 'Request body too large.')); req.destroy(); } else chunks.push(c); });
      req.on('end', () => {
        if (size === 0) return resolve({});
        try {
          const v = JSON.parse(Buffer.concat(chunks).toString('utf8'));
          if (typeof v !== 'object' || v === null || Array.isArray(v)) return reject(new ApiError(400, 'invalid_json', 'Body must be a JSON object.'));
          resolve(v);
        } catch { reject(new ApiError(400, 'invalid_json', 'Body is not valid JSON.')); }
      });
      req.on('error', reject);
    });
  }

  async function handle(req, res) {
    const send = (status, payload, extraHeaders = {}) => {
      const data = JSON.stringify(payload);
      res.writeHead(status, { 'Content-Type': 'application/json; charset=utf-8', 'Content-Length': Buffer.byteLength(data), 'Cache-Control': 'no-store', 'X-Content-Type-Options': 'nosniff', ...extraHeaders });
      res.end(data);
    };
    try {
      const url = new URL(req.url, 'http://localhost');
      const ip = req.socket.remoteAddress || 'unknown';
      let matched = null; let params = {};
      let pathMatched = false;
      for (const r of routes) {
        const m = r.re.exec(url.pathname);
        if (!m) continue;
        pathMatched = true;
        if (r.method !== req.method) continue;
        matched = r;
        r.keys.forEach((k, i) => { params[k] = decodeURIComponent(m[i + 1]); });
        break;
      }
      if (!matched) fail(pathMatched ? 405 : 404, pathMatched ? 'method_not_allowed' : 'not_found', 'No such endpoint.');
      const body = ['POST', 'PUT', 'PATCH', 'DELETE'].includes(req.method) ? await readBody(req) : {};
      const ctx = { req, ip, body, params, query: url.searchParams };
      if (matched.auth) { ctx.auth = authenticate(req); ctx.me = ctx.auth.player; }
      const out = matched.handler(ctx);
      send(out.status || 200, out.body);
    } catch (e) {
      if (e instanceof ApiError) {
        const headers = e.extra.retryAfter ? { 'Retry-After': String(e.extra.retryAfter) } : {};
        send(e.status, { error: { code: e.code, message: e.message, ...e.extra } }, headers);
      } else {
        console.error('Unhandled error:', e);
        send(500, { error: { code: 'internal_error', message: 'Something went wrong.' } });
      }
    }
  }

  return { handle, db, config, close: () => db.close() };
}

module.exports = { createApp, catalog };
