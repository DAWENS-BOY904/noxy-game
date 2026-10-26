'use strict';
// Battle Royale match authority: roster policy, phases on SERVER time, jump eligibility, freefall/parachute/landing
// validation, combat gating and server-computed damage. Clients send intents (move, jump, fire, pickup); they can never
// send a damage value, a kill, a phase change or a result.
//
// Transport note: this module is transport-independent. The dev backend exposes it over HTTP polling, which is fine for
// lobby/cabin/flight/jump but is NOT a real-time combat transport. A dedicated game server would run this same logic and
// stream movement over UDP/WebSocket. Matches live in memory: a backend restart ends running matches.
const fs = require('node:fs');
const path = require('node:path');
const crypto = require('node:crypto');

const DEFAULTS = {
  modes: { battle_royale: { minPlayers: 10, maxPlayers: 20, fillTimeoutMs: 60_000, maxWaitMs: 180_000, belowMin: 'cancel', requireServer: true } },
  cabinSeconds: 15,            // synchronized pre-match phase
  quietSeconds: 5,             // no jump button yet after the plane starts moving
  flightSeconds: 60,           // total flight; the jump window closes (and everyone is ejected) at the end
  planeAltitude: 450,
  cabin: { halfLength: 13, halfWidth: 3, spacing: 1.8, maxSpeed: 5 },
  moveTolerance: 1.35,         // allowed excess over each state's maximum speed (jitter, polling gaps)
  maxMoveGapSeconds: 2,        // longer gaps do not buy a longer teleport
  ground: { maxSpeed: 9, maxUp: 12, maxDown: 30, eyeHeight: 1.6 },
  freefall: { terminalSpeed: 55, maxHorizontal: 35, autoDeployAltitude: 90 },
  // after opening the chute the fall speed decays from the freefall speed to maxDescent over transitionSeconds
  parachute: { maxDescent: 12, maxHorizontal: 16, minDeployAltitude: 20, transitionSeconds: 2 },
  // The server's terrain is a coarse copy of what the player sees (about 1.4 m apart for 99% of land, more at cliff edges),
  // so touchdown is accepted within a tolerance, or when a descending player has stopped sinking just above the ground.
  landing: { safeSpeed: 14, damagePerExcess: 6, tolerance: 1.5, stallSeconds: 1, stallRange: 12 },
  combat: { headshotMultiplier: 2, armorAbsorb: 0.5, falloff: 0.3, relevanceRadius: 300, startHealth: 100, medkitHeal: 50, medkitSeconds: 3, medkitLimit: 4, switchSeconds: 0.3, pickupRange: 3 },
  disconnectMs: 45_000, abandonMs: 150_000, maxMatchSeconds: 1800, endedKeepMs: 10 * 60_000,
  allowedAnims: ['idle', 'walk', 'run', 'punch', 'wave', 'flex', 'shuffle', 'salute', 'freefall', 'glide', 'crouch', 'jump'],
};

const WEAPONS = {
  melee_knife: { slot: 'melee', mag: 0, interval: 0.5, reload: 0, range: 2.2, damage: 40, auto: false, ammo: null },
  pistol_p9: { slot: 'secondary', mag: 12, interval: 0.25, reload: 1.3, range: 70, damage: 24, auto: false, ammo: 'pistol' },
  rifle_ar: { slot: 'primary', mag: 30, interval: 0.1, reload: 2.0, range: 120, damage: 20, auto: true, ammo: 'rifle' },
};

const num = v => typeof v === 'number' && Number.isFinite(v);
const clamp = (v, a, b) => Math.min(b, Math.max(a, v));

function loadMap(file) {
  if (!fs.existsSync(file)) return null;
  const raw = fs.readFileSync(file);
  const m = JSON.parse(raw.toString('utf8'));
  const n = m.grid, half = m.worldSize / 2, cell = m.worldSize / (n - 1);
  const h = m.heightsDecimeters;
  const heightAt = (x, z) => {
    const gx = (x + half) / cell, gz = (z + half) / cell;
    if (gx < 0 || gz < 0 || gx > n - 1 || gz > n - 1) return m.waterLevel - 20;
    const x0 = Math.min(Math.floor(gx), n - 2), z0 = Math.min(Math.floor(gz), n - 2), tx = gx - x0, tz = gz - z0;
    const a = h[z0 * n + x0] / 10, b = h[z0 * n + x0 + 1] / 10, c = h[(z0 + 1) * n + x0] / 10, d = h[(z0 + 1) * n + x0 + 1] / 10;
    return a + (b - a) * tx + (c - a) * tz + (a - b - c + d) * tx * tz;
  };
  const slopeAt = (x, z) => { const e = 3; return Math.hypot((heightAt(x + e, z) - heightAt(x - e, z)) / (2 * e), (heightAt(x, z + e) - heightAt(x, z - e)) / (2 * e)); };
  const inBounds = (x, z) => Math.abs(x) <= half && Math.abs(z) <= half;
  const isValidLanding = (x, z) => inBounds(x, z) && heightAt(x, z) > m.waterLevel + 1.0 && slopeAt(x, z) < 0.65;
  const nearestValidLanding = (x, z) => {
    if (isValidLanding(x, z)) return { x, z };
    for (let r = 10; r <= half; r += 10) for (let i = 0; i < 24; i++) {
      const a = i * Math.PI * 2 / 24, px = x + Math.cos(a) * r, pz = z + Math.sin(a) * r;
      if (isValidLanding(px, pz)) return { x: px, z: pz };
    }
    return { x: 0, z: 0 };
  };
  const loot = new Map(m.loot.map(l => [l.id, l]));
  return { ...m, half, heightAt, slopeAt, inBounds, isValidLanding, nearestValidLanding, loot, hash: crypto.createHash('sha256').update(raw).digest('hex').slice(0, 16) };
}

function create(ctx) {
  const { q, fail, now, config, rateLimit, playerByPublicId, economy, servers } = ctx;
  const cfg = { ...DEFAULTS, ...(config.br || {}) };
  for (const k of ['cabin', 'ground', 'freefall', 'parachute', 'landing', 'combat']) cfg[k] = { ...DEFAULTS[k], ...((config.br || {})[k] || {}) };
  cfg.modes = { ...DEFAULTS.modes, ...((config.br || {}).modes || {}) };
  const map = loadMap(cfg.mapFile || path.join(__dirname, '..', 'data', 'maps', 'bermuda.json'));

  const matches = new Map();      // matchId -> match
  const inMatch = new Map();      // players.id -> matchId
  let seq = 0;

  // ---------------------------------------------------------------- helpers
  const mode = id => cfg.modes[id];
  const rnd = (seed, salt) => { const h = crypto.createHash('sha256').update(seed + ':' + salt).digest(); return h.readUInt32BE(0) / 4294967296; };

  function cabinSpawn(slot) {
    const c = cfg.cabin, perRow = Math.max(1, Math.floor((2 * c.halfLength - 2) / c.spacing) + 1);
    return { x: -c.halfLength + 1 + (slot % perRow) * c.spacing, z: -c.halfWidth + 1 + Math.floor(slot / perRow) * c.spacing };
  }

  function planeRoute(seed) {
    // operator/test override: a fixed straight route instead of a seeded random one
    if (cfg.fixedRoute) { const f = cfg.fixedRoute; return { ...f, altitude: cfg.planeAltitude, headingDeg: +((Math.atan2(f.bx - f.ax, f.bz - f.az) * 180 / Math.PI + 360) % 360).toFixed(1) }; }
    // enters and leaves over open sea, passing within ~35% of the map half-size of the centre
    const half = map.half, ang = rnd(seed, 'angle') * Math.PI * 2, off = (rnd(seed, 'offset') - 0.5) * 0.7 * half;
    const dx = Math.cos(ang), dz = Math.sin(ang), px = -dz, pz = dx, R = half * 1.15;
    return { ax: +(-dx * R + px * off).toFixed(1), az: +(-dz * R + pz * off).toFixed(1), bx: +(dx * R + px * off).toFixed(1), bz: +(dz * R + pz * off).toFixed(1), altitude: cfg.planeAltitude, headingDeg: +((Math.atan2(dx, dz) * 180 / Math.PI + 360) % 360).toFixed(1) };
  }
  function planeAt(m, t) {
    const r = m.route, f = clamp((t - m.t.flightStart) / (m.t.flightEnd - m.t.flightStart), 0, 1);
    return { x: r.ax + (r.bx - r.ax) * f, y: r.altitude, z: r.az + (r.bz - r.az) * f };
  }

  function emit(m, type, data) { m.events.push({ seq: ++seq, at: now(), type, ...data }); if (m.events.length > 400) m.events.shift(); }
  const alivePlayers = m => [...m.players.values()].filter(p => p.state !== 'eliminated');

  function newPlayer(m, me) {
    const slot = m.players.size, sp = cabinSpawn(slot);
    return {
      publicId: me.public_id, playerId: me.id, username: me.username, slot, state: 'aircraft', space: 'cabin',
      x: sp.x, y: 0, z: sp.z, yaw: 0, anim: 'idle', moveAt: now(), lastSeen: now(), connected: true,
      health: cfg.combat.startHealth, armor: 0, kills: 0, placement: 0, forced: false, relocated: false, vy: 0, jumpedAt: 0, landedAt: 0,
      weapons: { primary: null, secondary: null, melee: { id: 'melee_knife', mag: 0 } }, selected: 'melee', ammo: { pistol: 0, rifle: 0 }, medkits: 0,
      lastShotAt: 0, reloading: null, switchingUntil: 0, healing: null, damageTaken: [],
    };
  }

  // ---------------------------------------------------------------- lifecycle
  function startCabin(m) {
    const t = now();
    m.t = { cabinStart: t, cabinEnd: t + cfg.cabinSeconds * 1000 };
    m.t.flightStart = m.t.cabinEnd; m.t.jumpOpens = m.t.flightStart + cfg.quietSeconds * 1000;
    m.t.flightEnd = m.t.flightStart + cfg.flightSeconds * 1000; m.t.combatStart = m.t.flightEnd;
    m.route = planeRoute(m.seed);
    m.status = 'cabin';
    emit(m, 'phase', { phase: 'cabin' });
  }

  function finish(m, reason) {
    if (m.status === 'ended' || m.status === 'cancelled') return;
    // whoever is still standing is ranked by kills, then health (a time-limit finish can leave several)
    const alive = alivePlayers(m).sort((a, b) => b.kills - a.kills || b.health - a.health);
    alive.forEach((p, i) => { p.placement = i + 1; });
    m.status = 'ended'; m.endedAt = now(); m.endReason = reason;
    m.results = [...m.players.values()].sort((a, b) => a.placement - b.placement).map(p => ({ publicId: p.publicId, username: p.username, placement: p.placement, kills: p.kills }));
    emit(m, 'phase', { phase: 'ended', reason });
    for (const p of m.players.values()) {
      if (p.abandoned) continue;
      try { if (ctx.fairplay && ctx.fairplay.recordCleanMatch) ctx.fairplay.recordCleanMatch(p.playerId, m.id); } catch { /* best effort */ }
    }
    // trusted result -> the existing idempotent reward pipeline (Booyah, coins, statistics)
    try {
      const startedAt = m.t ? m.t.flightStart : m.createdAt;
      m.rewards = economy.processMatch({
        matchId: m.id, mode: m.mode, playerCount: m.players.size,
        durationSeconds: clamp(Math.round((m.endedAt - startedAt) / 1000), 0, 7200),
        players: m.results.map(r => ({ publicId: r.publicId, placement: r.placement, kills: Math.min(r.kills, 100) })),
      });
    } catch (e) { m.rewards = { error: e.code || 'reward_failed' }; }
  }

  function eliminate(m, p, why, killer) {
    if (p.state === 'eliminated') return;
    const aliveBefore = alivePlayers(m).length;
    p.state = 'eliminated'; p.placement = aliveBefore; p.health = 0; p.eliminatedAt = now(); p.reloading = null; p.healing = null;
    if (killer) killer.kills++;
    emit(m, 'elimination', { victim: p.publicId, victimName: p.username, killer: killer ? killer.publicId : null, killerName: killer ? killer.username : null, reason: why, placement: p.placement });
  }

  function advance(m) {
    const t = now();
    if (m.status === 'ended' || m.status === 'cancelled') return;
    if (m.status === 'waiting') {
      const pol = mode(m.mode), n = m.players.size;
      if (n >= pol.minPlayers && (n >= pol.maxPlayers || t >= m.fillDeadline)) startCabin(m);
      else if (t >= m.cancelAt && pol.belowMin === 'cancel') {
        m.status = 'cancelled'; m.endedAt = t; m.endReason = 'not_enough_players'; m.results = [];
        emit(m, 'phase', { phase: 'cancelled', reason: 'not_enough_players' });
      }
      return;
    }
    if (m.status === 'cabin' && t >= m.t.cabinEnd) { m.status = 'flight'; emit(m, 'phase', { phase: 'flight' }); }
    if (m.status === 'flight' && t >= m.t.flightEnd) {
      // the server-enforced drop deadline: whoever is still aboard leaves at the plane's final position
      for (const p of m.players.values()) if (p.state === 'aircraft') { p.forced = true; exitPlane(m, p, m.t.flightEnd); }
      m.status = 'combat'; emit(m, 'phase', { phase: 'combat' });
    }
    // disconnects
    for (const p of m.players.values()) {
      if (p.state === 'eliminated') continue;
      p.connected = t - p.lastSeen <= cfg.disconnectMs;
      if (m.status !== 'cabin' && t - p.lastSeen > cfg.abandonMs) { p.abandoned = true; eliminate(m, p, 'abandoned', null); recordAbandon(m, p); }
    }
    // delayed actions
    for (const p of m.players.values()) {
      if (p.reloading && t >= p.reloading.endsAt) completeReload(p);
      if (p.healing && t >= p.healing.endsAt) { p.health = Math.min(cfg.combat.startHealth, p.health + cfg.combat.medkitHeal); p.healing = null; }
    }
    if (m.status === 'combat') {
      const alive = alivePlayers(m);
      if (alive.length <= 1) finish(m, 'last_standing');
      else if (t - m.t.combatStart > cfg.maxMatchSeconds * 1000) finish(m, 'time_limit');
    }
  }

  function recordAbandon(m, p) {
    try { if (ctx.fairplay && ctx.fairplay.recordAbandon) ctx.fairplay.recordAbandon(p.playerId, m.id); } catch { /* reputation is best effort */ }
  }

  function exitPlane(m, p, at) {
    const plane = planeAt(m, at);
    // the player leaves from where they stood in the cabin. Cabin axes follow Unity: local +x is the plane's forward direction
    // and local +z points to its LEFT (rotating +x by the heading, then the left-handed +z)
    const h = m.route.headingDeg * Math.PI / 180, lx = p.x, lz = p.z;
    p.x = plane.x + lx * Math.sin(h) - lz * Math.cos(h); p.z = plane.z + lx * Math.cos(h) + lz * Math.sin(h); p.y = plane.y - 2;
    p.space = 'world'; p.state = 'freefall'; p.jumpedAt = at; p.moveAt = at; p.anim = 'freefall'; p.vy = 0;
    emit(m, 'jump', { player: p.publicId, forced: p.forced });
  }

  function sweep() {
    const t = now();
    for (const [id, m] of matches) {
      advance(m);
      if ((m.status === 'ended' || m.status === 'cancelled') && t - m.endedAt > cfg.endedKeepMs) matches.delete(id);
    }
  }

  // ---------------------------------------------------------------- weapons and loot
  const weaponOf = p => (p.weapons[p.selected] ? WEAPONS[p.weapons[p.selected].id] : null);

  function completeReload(p) {
    const r = p.reloading; p.reloading = null;
    const w = p.weapons[r.slot]; if (!w) return;
    const def = WEAPONS[w.id], need = def.mag - w.mag, take = Math.min(need, p.ammo[def.ammo]);
    w.mag += take; p.ammo[def.ammo] -= take;
  }

  function lootContents(m, spawn) {
    // decided by the server from the match seed and the spawn id; a client cannot choose what it gets
    const r = rnd(m.seed, spawn.id);
    switch (spawn.kind) {
      case 'weapon': return spawn.tier >= 2 || r > 0.6 ? { type: 'weapon', id: 'rifle_ar' } : { type: 'weapon', id: 'pistol_p9' };
      case 'ammo': return r > 0.5 ? { type: 'ammo', ammo: 'rifle', amount: 30 } : { type: 'ammo', ammo: 'pistol', amount: 24 };
      case 'medical': return { type: 'medkit', amount: 1 };
      default: return { type: 'armor', amount: spawn.tier >= 2 ? 60 : 30 };
    }
  }

  function applyLoot(p, item) {
    switch (item.type) {
      case 'weapon': {
        const def = WEAPONS[item.id];
        const have = p.weapons[def.slot];
        if (have && have.id === item.id) { p.ammo[def.ammo] += def.mag; return 'ammo'; }
        p.weapons[def.slot] = { id: item.id, mag: def.mag };
        if (p.selected === 'melee') p.selected = def.slot;
        return 'weapon';
      }
      case 'ammo': p.ammo[item.ammo] = Math.min(240, p.ammo[item.ammo] + item.amount); return 'ammo';
      case 'medkit': if (p.medkits >= cfg.combat.medkitLimit) return null; p.medkits += item.amount; return 'medkit';
      default: if (p.armor >= 100) return null; p.armor = Math.min(100, Math.max(p.armor, 0) + item.amount); return 'armor';
    }
  }

  // ray vs vertical capsule-ish target; terrain blocks shots. Building walls are NOT enforced server-side yet.
  function trace(m, shooter, dir, range) {
    const o = { x: shooter.x, y: shooter.y + cfg.ground.eyeHeight, z: shooter.z };
    let best = null;
    for (const t of m.players.values()) {
      if (t === shooter || t.state !== 'landed') continue;
      const wx = t.x - o.x, wz = t.z - o.z, d2 = dir.x * dir.x + dir.z * dir.z;
      if (d2 < 1e-6) continue;
      const tt = (wx * dir.x + wz * dir.z) / d2;
      if (tt <= 0 || tt > range) continue;
      const hx = o.x + dir.x * tt - t.x, hz = o.z + dir.z * tt - t.z;
      const y = o.y + dir.y * tt;
      if (Math.hypot(hx, hz) > 0.45 || y < t.y || y > t.y + 1.85) continue;
      if (!best || tt < best.t) best = { target: t, t: tt, head: y >= t.y + 1.45 };
    }
    if (best) for (let s = 2; s < best.t; s += 2) { if (map.heightAt(o.x + dir.x * s, o.z + dir.z * s) > o.y + dir.y * s) return null; }
    return best;
  }

  // ---------------------------------------------------------------- views
  function activeMatchOf(me, allowFinished = false) {
    const id = inMatch.get(me.id); const m = id && matches.get(id);
    if (!m) fail(404, 'not_in_match', 'You are not in a Battle Royale match.');
    advance(m);
    if (!allowFinished && (m.status === 'ended' || m.status === 'cancelled')) fail(409, 'match_over', m.status === 'cancelled' ? 'The match was cancelled.' : 'The match is over.');
    const p = m.players.get(me.public_id);
    p.lastSeen = now(); p.connected = true;
    return { m, p };
  }

  const timesView = m => (m.t ? { cabinStartsAt: m.t.cabinStart, cabinEndsAt: m.t.cabinEnd, flightStartsAt: m.t.flightStart, jumpOpensAt: m.t.jumpOpens, flightEndsAt: m.t.flightEnd, combatStartsAt: m.t.combatStart } : null);

  function playerView(m, viewer, p) {
    const base = { publicId: p.publicId, username: p.username, slot: p.slot, state: p.state, alive: p.state !== 'eliminated', kills: p.kills, connected: p.connected, anim: p.anim, yaw: p.yaw, space: p.space };
    if (p.state === 'eliminated') return { ...base, visible: false };
    if (p === viewer || p.state === 'aircraft') return { ...base, visible: true, x: p.x, y: p.y, z: p.z };
    // interest management: positions of far-away players are not sent (no map-hack from the network)
    const d = Math.hypot(p.x - viewer.x, p.z - viewer.z);
    return d <= cfg.combat.relevanceRadius ? { ...base, visible: true, x: p.x, y: p.y, z: p.z } : { ...base, visible: false };
  }

  function stateView(m, me, since) {
    const p = m.players.get(me.public_id), t = now();
    const v = {
      matchId: m.id, mode: m.mode, status: m.status, serverNow: t, times: timesView(m), eventSeq: seq,
      server: m.server, players: [...m.players.values()].sort((a, b) => a.slot - b.slot).map(x => playerView(m, p, x)),
      you: {
        publicId: p.publicId, state: p.state, space: p.space, x: p.x, y: p.y, z: p.z, health: p.health, armor: p.armor, medkits: p.medkits, selected: p.selected, kills: p.kills,
        weapons: p.weapons, ammo: p.ammo, reloadingUntil: p.reloading ? p.reloading.endsAt : 0, healingUntil: p.healing ? p.healing.endsAt : 0, relocated: p.relocated, forced: p.forced, placement: p.placement,
      },
      events: m.events.filter(e => e.seq > since),
    };
    if (m.status === 'waiting') { const pol = mode(m.mode); v.waiting = { players: m.players.size, minPlayers: pol.minPlayers, maxPlayers: pol.maxPlayers, fillDeadlineAt: m.fillDeadline, cancelAt: m.cancelAt }; }
    if (m.route) {
      v.route = m.route;
      v.plane = planeAt(m, t);
      v.jump = { open: m.status === 'flight' && t >= m.t.jumpOpens, opensAt: m.t.jumpOpens, deadlineAt: m.t.flightEnd };
      v.combat = { enabled: m.status === 'combat', startsAt: m.t.combatStart };
    }
    if (m.status === 'ended' || m.status === 'cancelled') { v.results = m.results || []; v.endReason = m.endReason; v.rewards = m.rewards ? { processed: !m.rewards.error, error: m.rewards.error || null } : null; }
    return v;
  }

  // ---------------------------------------------------------------- movement validation
  function validateMove(m, p, body) {
    const t = now(), dt = clamp((t - p.moveAt) / 1000, 0.02, cfg.maxMoveGapSeconds), tol = cfg.moveTolerance;
    const x = body.x, y = body.y, z = body.z;
    if (![x, y, z].every(num) || Math.abs(x) > 5000 || Math.abs(z) > 5000 || Math.abs(y) > 5000) return { ok: false, code: 'invalid_position' };
    const anim = typeof body.anim === 'string' && cfg.allowedAnims.includes(body.anim) ? body.anim : p.anim;
    const yaw = num(body.yaw) ? body.yaw % 360 : p.yaw;
    if (p.state === 'aircraft') {
      const c = cfg.cabin;
      if (Math.abs(x) > c.halfLength || Math.abs(z) > c.halfWidth) return { ok: false, code: 'outside_cabin' };
      if (Math.hypot(x - p.x, z - p.z) > c.maxSpeed * dt * tol + 0.3) return { ok: false, code: 'too_fast' };
      // harmless in the cabin: a punch is only an animation, it never reaches the combat code
      return { ok: true, apply: () => { p.x = x; p.z = z; p.yaw = yaw; p.anim = anim; p.moveAt = t; } };
    }
    if (p.state === 'freefall' || p.state === 'parachute') {
      const f = p.state === 'freefall' ? cfg.freefall : cfg.parachute;
      const maxH = f.maxHorizontal * dt * tol + 0.5;
      let dropLimit = p.state === 'freefall' ? f.terminalSpeed : f.maxDescent;
      if (p.state === 'parachute') {
        // right after opening, the player is still falling fast and needs a moment to slow down
        const left = Math.max(0, 1 - (t - (p.chuteAt || 0)) / (cfg.parachute.transitionSeconds * 1000));
        dropLimit = cfg.parachute.maxDescent + (cfg.freefall.terminalSpeed - cfg.parachute.maxDescent) * left;
      }
      const maxDrop = dropLimit * dt * tol + 0.5;
      if (Math.hypot(x - p.x, z - p.z) > maxH) return { ok: false, code: 'too_fast' };
      if (y - p.y > 0.5) return { ok: false, code: 'cannot_climb' };
      if (p.y - y > maxDrop) return { ok: false, code: 'too_fast' };
      if (y > cfg.planeAltitude + 5) return { ok: false, code: 'invalid_position' };
      return { ok: true, apply: () => landOrFall(m, p, x, y, z, yaw, dt) };
    }
    if (p.state === 'landed') {
      const g = cfg.ground;
      if (Math.hypot(x - p.x, z - p.z) > g.maxSpeed * dt * tol + 0.4) return { ok: false, code: 'too_fast' };
      const ground = map.heightAt(x, z);
      if (y < ground - 0.8 || y > ground + 14) return { ok: false, code: 'invalid_height' };
      if (y - p.y > g.maxUp * dt * tol + 0.5 || p.y - y > g.maxDown * dt * tol + 0.5) return { ok: false, code: 'too_fast' };
      if (!map.inBounds(x, z)) return { ok: false, code: 'out_of_bounds' };
      const moved = Math.hypot(x - p.x, z - p.z) > 0.5;
      return { ok: true, apply: () => { if (moved) p.healing = null; p.x = x; p.y = y; p.z = z; p.yaw = yaw; p.anim = anim; p.moveAt = t; } };
    }
    return { ok: false, code: 'cannot_move' };
  }

  function landOrFall(m, p, x, y, z, yaw, dt) {
    const rate = (p.y - y) / dt;      // descent speed derived from positions, never trusted from the client
    p.x = x; p.z = z; p.yaw = yaw; p.vy = rate; p.moveAt = now();
    const ground = map.heightAt(x, z);
    if (p.state === 'freefall' && y - ground <= cfg.freefall.autoDeployAltitude) { p.state = 'parachute'; p.chuteAt = now(); p.anim = 'glide'; emit(m, 'parachute', { player: p.publicId, auto: true }); }
    const stalled = rate < 0.3 && y - ground <= cfg.landing.stallRange;
    if (!stalled) p.stallSince = 0; else if (!p.stallSince) p.stallSince = now();
    if (y <= ground + cfg.landing.tolerance || (stalled && now() - p.stallSince >= cfg.landing.stallSeconds * 1000)) {
      let lx = x, lz = z;
      if (!map.isValidLanding(x, z)) { const n = map.nearestValidLanding(x, z); lx = n.x; lz = n.z; p.relocated = true; }
      p.x = lx; p.z = lz; p.y = map.heightAt(lx, lz); p.state = 'landed'; p.space = 'world'; p.anim = 'idle'; p.landedAt = now();
      const excess = rate - cfg.landing.safeSpeed;
      if (excess > 0) {
        p.health -= Math.round(excess * cfg.landing.damagePerExcess);
        if (p.health <= 0) eliminate(m, p, 'fall', null);
      }
      emit(m, 'landed', { player: p.publicId, relocated: p.relocated });
    } else p.y = y;
  }

  // ---------------------------------------------------------------- routes
  function install(route) {
    const guard = () => { if (!map) fail(503, 'map_missing', 'Battle Royale map data (server/data/maps/bermuda.json) is missing.'); };

    route('GET', '/api/br/config', ({ me }) => {
      guard(); rateLimit('brState', me.id);
      return { body: {
        cabinSeconds: cfg.cabinSeconds, quietSeconds: cfg.quietSeconds, flightSeconds: cfg.flightSeconds, planeAltitude: cfg.planeAltitude,
        cabin: cfg.cabin, ground: cfg.ground, freefall: cfg.freefall, parachute: cfg.parachute, landing: cfg.landing, headshotMultiplier: cfg.combat.headshotMultiplier,
        modes: Object.fromEntries(Object.entries(cfg.modes).map(([k, v]) => [k, { minPlayers: v.minPlayers, maxPlayers: v.maxPlayers, fillTimeoutSeconds: v.fillTimeoutMs / 1000 }])),
        weapons: WEAPONS, allowedAnims: cfg.allowedAnims,
      } };
    });

    // lets the client verify it renders the same island the server validates against
    route('GET', '/api/br/map-info', ({ me }) => { guard(); rateLimit('brState', me.id); return { body: { mapId: map.mapId, seed: map.seed, worldSize: map.worldSize, grid: map.grid, hash: map.hash } }; });

    route('POST', '/api/br/queue', ({ me, body }) => {
      guard(); rateLimit('brAction', me.id);
      const modeId = typeof body.mode === 'string' ? body.mode : 'battle_royale';
      const pol = mode(modeId);
      if (!pol) fail(400, 'invalid_mode', 'That mode has no matchmaking.');
      const existing = inMatch.get(me.id);
      if (existing && matches.get(existing)) {
        const old = matches.get(existing); advance(old);
        if (old.status !== 'ended' && old.status !== 'cancelled') return { body: stateView(old, me, 0) };
      }
      if (ctx.fairplay && ctx.fairplay.eligibility && !ctx.fairplay.eligibility(me.id).competitiveAllowed) fail(403, 'competitive_restricted', 'Competitive play is restricted on your account.');
      let srv = null;
      if (pol.requireServer) {
        srv = servers.pick(modeId);
        if (!srv) fail(503, 'no_server_available', 'No game server is online for this mode right now. Nothing was queued.');
      }
      let m = [...matches.values()].find(x => x.mode === modeId && x.status === 'waiting' && x.players.size < pol.maxPlayers && (!srv || x.server && x.server.id === srv.id));
      if (m) advance(m);
      if (!m || m.status !== 'waiting') {
        const t = now(), id = 'br-' + crypto.randomBytes(6).toString('hex');
        m = { id, mode: modeId, status: 'waiting', createdAt: t, fillDeadline: t + pol.fillTimeoutMs, cancelAt: t + pol.maxWaitMs, seed: crypto.randomBytes(8).toString('hex'), players: new Map(), events: [], server: srv ? { id: srv.id, name: srv.name, region: srv.region, host: srv.host, port: srv.port } : null };
        matches.set(id, m);
      }
      const p = newPlayer(m, me); m.players.set(me.public_id, p); inMatch.set(me.id, m.id);
      emit(m, 'joined', { player: p.publicId, count: m.players.size });
      advance(m);
      return { status: 201, body: stateView(m, me, 0) };
    });

    route('DELETE', '/api/br/queue', ({ me }) => {
      guard(); rateLimit('brAction', me.id);
      const { m, p } = activeMatchOf(me, true);
      if (m.status === 'ended' || m.status === 'cancelled') { inMatch.delete(me.id); return { body: { ok: true } }; }
      if (m.status !== 'waiting') fail(409, 'match_started', 'The match already started.');
      m.players.delete(p.publicId); inMatch.delete(me.id);
      // slots are assigned in join order; compact them so cabin spawn points stay unique
      let i = 0; for (const x of m.players.values()) { x.slot = i++; const sp = cabinSpawn(x.slot); x.x = sp.x; x.z = sp.z; }
      if (m.players.size === 0) matches.delete(m.id);
      return { body: { ok: true } };
    });

    route('GET', '/api/br/state', ({ me, query }) => {
      guard(); rateLimit('brState', me.id);
      const { m } = activeMatchOf(me, true);
      return { body: stateView(m, me, Number.parseInt(query.get('since') || '0', 10) || 0) };
    });

    route('POST', '/api/br/move', ({ me, body }) => {
      guard(); rateLimit('brMove', me.id);
      const { m, p } = activeMatchOf(me);
      if (m.status === 'waiting' || m.status === 'ended') fail(409, 'not_moving_phase', 'Movement is not accepted in this phase.');
      const r = validateMove(m, p, body || {});
      if (!r.ok) return { status: 409, body: { error: { code: r.code, message: 'Move rejected by the server.' }, correction: { x: p.x, y: p.y, z: p.z, state: p.state } } };
      r.apply();
      return { body: { ok: true, state: p.state, x: p.x, y: p.y, z: p.z, relocated: p.relocated, health: p.health } };
    });

    route('POST', '/api/br/jump', ({ me }) => {
      guard(); rateLimit('brAction', me.id);
      const { m, p } = activeMatchOf(me);
      const t = now();
      if (p.state !== 'aircraft') fail(409, p.state === 'eliminated' ? 'eliminated' : 'already_jumped', 'You are not in the aircraft.');
      if (m.status !== 'flight' || t < m.t.jumpOpens) fail(409, 'jump_not_open', 'The jump window is not open yet.', { opensInMs: m.t ? Math.max(0, m.t.jumpOpens - t) : null });
      exitPlane(m, p, t);   // only THIS player leaves; everyone else keeps their own timing and choice
      return { body: { ok: true, state: p.state, x: p.x, y: p.y, z: p.z } };
    });

    route('POST', '/api/br/parachute', ({ me }) => {
      guard(); rateLimit('brAction', me.id);
      const { m, p } = activeMatchOf(me);
      if (p.state !== 'freefall') fail(409, 'not_in_freefall', 'You can only open the parachute during freefall.');
      if (p.y - map.heightAt(p.x, p.z) < cfg.parachute.minDeployAltitude) fail(409, 'too_low', 'Too low to open the parachute.');
      p.state = 'parachute'; p.chuteAt = now(); p.anim = 'glide'; emit(m, 'parachute', { player: p.publicId, auto: false });
      return { body: { ok: true, state: p.state } };
    });

    route('POST', '/api/br/select', ({ me, body }) => {
      guard(); rateLimit('brAction', me.id);
      const { p } = activeMatchOf(me);
      if (!['primary', 'secondary', 'melee'].includes(body.slot) || !p.weapons[body.slot]) fail(400, 'invalid_slot', 'No weapon in that slot.');
      if (p.state === 'eliminated') fail(409, 'eliminated', 'You are eliminated.');
      p.selected = body.slot; p.reloading = null; p.switchingUntil = now() + cfg.combat.switchSeconds * 1000;
      return { body: { ok: true, selected: p.selected } };
    });

    route('POST', '/api/br/reload', ({ me }) => {
      guard(); rateLimit('brAction', me.id);
      const { p } = activeMatchOf(me);
      const w = p.weapons[p.selected], def = w && WEAPONS[w.id];
      if (!def || !def.ammo) fail(409, 'not_reloadable', 'This weapon cannot be reloaded.');
      if (p.state !== 'landed') fail(409, 'not_landed', 'You can only reload on the ground.');
      if (p.reloading) fail(409, 'reloading', 'Already reloading.');
      if (w.mag >= def.mag) fail(409, 'magazine_full', 'The magazine is full.');
      if (p.ammo[def.ammo] <= 0) fail(409, 'no_reserve', 'No reserve ammunition.');
      p.reloading = { slot: p.selected, endsAt: now() + def.reload * 1000 };
      return { body: { ok: true, endsAt: p.reloading.endsAt } };
    });

    route('POST', '/api/br/heal', ({ me }) => {
      guard(); rateLimit('brAction', me.id);
      const { p } = activeMatchOf(me);
      if (p.state !== 'landed') fail(409, 'not_landed', 'You can only heal on the ground.');
      if (p.medkits <= 0) fail(409, 'no_medkit', 'You have no medkit.');
      if (p.health >= cfg.combat.startHealth) fail(409, 'full_health', 'You are at full health.');
      if (p.healing) fail(409, 'already_healing', 'Already healing.');
      p.medkits--; p.healing = { endsAt: now() + cfg.combat.medkitSeconds * 1000 };
      return { body: { ok: true, endsAt: p.healing.endsAt } };
    });

    route('POST', '/api/br/loot/pickup', ({ me, body }) => {
      guard(); rateLimit('brAction', me.id);
      const { m, p } = activeMatchOf(me);
      if (p.state !== 'landed') fail(409, 'not_landed', 'You can only pick up loot on the ground.');
      const spawn = typeof body.lootId === 'string' ? map.loot.get(body.lootId) : null;
      if (!spawn) fail(404, 'unknown_loot', 'No such loot.');
      m.lootTaken = m.lootTaken || new Set();
      if (m.lootTaken.has(spawn.id)) fail(409, 'already_taken', 'Someone already took that.');
      if (Math.hypot(spawn.x - p.x, spawn.z - p.z) > cfg.combat.pickupRange || Math.abs(spawn.y - p.y) > 3) fail(409, 'too_far', 'Too far away to pick that up.');
      const item = lootContents(m, spawn);
      const got = applyLoot(p, item);
      if (!got) fail(409, 'cannot_carry', 'You cannot carry more of that.');
      m.lootTaken.add(spawn.id);
      return { body: { ok: true, item, you: { weapons: p.weapons, ammo: p.ammo, medkits: p.medkits, armor: p.armor, selected: p.selected } } };
    });

    route('POST', '/api/br/fire', ({ me, body }) => {
      guard(); rateLimit('brAction', me.id);
      const { m, p } = activeMatchOf(me);
      const t = now();
      // body.damage, body.target, body.kill ... are never read: damage is computed here
      if (m.status !== 'combat') fail(409, 'combat_disabled', 'Combat is not enabled yet.', { startsInMs: m.t ? Math.max(0, m.t.combatStart - t) : null });
      if (p.state === 'eliminated') fail(409, 'eliminated', 'You are eliminated.');
      if (p.state !== 'landed') fail(409, 'not_landed', 'You cannot fire while airborne.');
      const w = p.weapons[p.selected], def = w && WEAPONS[w.id];
      if (!def) fail(409, 'no_weapon', 'No weapon equipped.');
      if (t < p.switchingUntil) fail(409, 'switching', 'Switching weapons.');
      if (p.reloading) fail(409, 'reloading', 'Reloading.');
      if (t - p.lastShotAt < def.interval * 1000 * 0.85) fail(429, 'fire_rate', 'Firing too fast.');
      if (def.ammo && w.mag <= 0) fail(409, 'empty', 'Magazine empty.');
      const d = Array.isArray(body.dir) && body.dir.length === 3 && body.dir.every(num) ? { x: body.dir[0], y: body.dir[1], z: body.dir[2] } : null;
      const len = d ? Math.hypot(d.x, d.y, d.z) : 0;
      if (!d || !(len > 1e-3) || len > 1e6) fail(400, 'invalid_direction', 'Invalid aim direction.');   // any non-zero vector is normalised here
      d.x /= len; d.y /= len; d.z /= len;
      p.lastShotAt = t; p.healing = null;
      if (def.ammo) w.mag--;
      const hit = trace(m, p, d, def.range);
      let result = null;
      if (hit) {
        const target = hit.target;
        let dmg = def.damage * (hit.head ? cfg.combat.headshotMultiplier : 1) * (1 - cfg.combat.falloff * hit.t / def.range);
        dmg = Math.max(1, Math.round(dmg));
        const soak = Math.min(target.armor, Math.round(dmg * cfg.combat.armorAbsorb));
        target.armor -= soak; target.health -= dmg - soak; target.healing = null;
        target.damageTaken.push({ at: t, from: p.publicId, amount: dmg });
        if (target.damageTaken.length > 20) target.damageTaken.shift();
        result = { target: target.publicId, damage: dmg, head: hit.head, distance: +hit.t.toFixed(1) };
        if (target.health <= 0) eliminate(m, target, hit.head ? 'headshot' : 'shot', p);
        advance(m);
      }
      return { body: { ok: true, hit: result, ammo: { mag: w.mag, reserve: def.ammo ? p.ammo[def.ammo] : 0 } } };
    });

    // ---- operator/game-server view
    route('GET', '/api/internal/br/matches', ({ req, ip }) => {
      ctx.requireServerKey(req, ip); sweep();
      return { body: { matches: [...matches.values()].map(m => ({ id: m.id, mode: m.mode, status: m.status, players: m.players.size, alive: alivePlayers(m).length, server: m.server && m.server.id, createdAt: m.createdAt })) } };
    }, { auth: false });
  }

  return { install, sweep, map, cfg, WEAPONS, _matches: matches, _inMatch: inMatch };
}

module.exports = { create, WEAPONS, DEFAULTS };
