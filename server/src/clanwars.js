'use strict';
// Clan wars. Clients can register rosters, queue and read history; they can NEVER submit scores.
// Results arrive only from the dedicated server (x-noxy-server-key) and are validated against the stored roster,
// the match format and basic score consistency before any XP / rating / coin reward is applied.
// Rewards are idempotent (unique result per match, per-player ledger keys) and capped per clan and per opponent pair.
const crypto = require('node:crypto');

const DAY = 86400000;

const DEFAULTS = {
  formats: {
    '2v2': { teamSize: 2, roundLimit: 3, durationSeconds: 600 },
    '4v4': { teamSize: 4, roundLimit: 5, durationSeconds: 900 },
  },
  maps: ['Foundry Yard', 'Skyline Rooftops', 'Dockside'],
  minMemberAgeMs: DAY, // blocks "join an alt right before the match" farming
  winXp: 150, lossXp: 50, drawXp: 75,
  winCoins: 100, participationCoins: 30,
  dailyClanXpCap: 1500,
  pairCapPer24h: 2, // rewarded matches between the same two clans per 24h; extra ones are unrated
  disputeWindowMs: DAY,
  pendingTimeoutMs: 10 * 60000,
  liveGraceMs: 5 * 60000,
  queueTimeoutMs: 15 * 60000,
  ratingK: 24, queueBaseRange: 150, queueRangePer30s: 100,
};

const SCHEMA = `
CREATE TABLE IF NOT EXISTS clan_war_events (
  id                  TEXT PRIMARY KEY,
  title               TEXT NOT NULL,
  team_size           INTEGER NOT NULL CHECK (team_size BETWEEN 1 AND 8),
  map                 TEXT NOT NULL,
  round_limit         INTEGER NOT NULL CHECK (round_limit BETWEEN 1 AND 15),
  duration_seconds    INTEGER NOT NULL CHECK (duration_seconds BETWEEN 60 AND 3600),
  starts_at           INTEGER NOT NULL,
  registration_closes_at INTEGER NOT NULL,
  status              TEXT NOT NULL DEFAULT 'scheduled' CHECK (status IN ('scheduled','matching','finished','cancelled')),
  created_at          INTEGER NOT NULL
);
CREATE TABLE IF NOT EXISTS clan_war_registrations (
  event_id   TEXT NOT NULL REFERENCES clan_war_events(id) ON DELETE CASCADE,
  clan_id    INTEGER NOT NULL REFERENCES clans(id) ON DELETE CASCADE,
  registered_by INTEGER,
  rating     INTEGER NOT NULL,
  match_id   TEXT,
  created_at INTEGER NOT NULL,
  PRIMARY KEY (event_id, clan_id)
);
CREATE TABLE IF NOT EXISTS clan_war_roster (
  event_id  TEXT NOT NULL,
  clan_id   INTEGER NOT NULL,
  player_id INTEGER NOT NULL REFERENCES players(id) ON DELETE CASCADE,
  PRIMARY KEY (event_id, clan_id, player_id),
  FOREIGN KEY (event_id, clan_id) REFERENCES clan_war_registrations(event_id, clan_id) ON DELETE CASCADE
);
CREATE TABLE IF NOT EXISTS clan_war_queue (
  clan_id   INTEGER PRIMARY KEY REFERENCES clans(id) ON DELETE CASCADE,
  format_id TEXT NOT NULL,
  rating    INTEGER NOT NULL,
  queued_by INTEGER,
  queued_at INTEGER NOT NULL
);
CREATE TABLE IF NOT EXISTS clan_war_queue_players (
  clan_id   INTEGER NOT NULL REFERENCES clan_war_queue(clan_id) ON DELETE CASCADE,
  player_id INTEGER NOT NULL REFERENCES players(id) ON DELETE CASCADE,
  PRIMARY KEY (clan_id, player_id)
);
CREATE TABLE IF NOT EXISTS clan_matches (
  id          TEXT PRIMARY KEY,
  event_id    TEXT,
  clan_a      INTEGER NOT NULL,
  clan_b      INTEGER NOT NULL,
  clan_a_name TEXT NOT NULL,
  clan_b_name TEXT NOT NULL,
  team_size   INTEGER NOT NULL,
  map         TEXT NOT NULL,
  round_limit INTEGER NOT NULL,
  duration_seconds INTEGER NOT NULL,
  status      TEXT NOT NULL DEFAULT 'pending' CHECK (status IN ('pending','live','completed','void','disputed')),
  server_id   TEXT,
  created_at  INTEGER NOT NULL,
  started_at  INTEGER,
  finished_at INTEGER,
  CHECK (clan_a <> clan_b)
);
CREATE INDEX IF NOT EXISTS idx_clan_matches_a ON clan_matches(clan_a, status);
CREATE INDEX IF NOT EXISTS idx_clan_matches_b ON clan_matches(clan_b, status);
CREATE TABLE IF NOT EXISTS clan_match_players (
  match_id   TEXT NOT NULL REFERENCES clan_matches(id) ON DELETE CASCADE,
  clan_id    INTEGER NOT NULL,
  player_id  INTEGER NOT NULL REFERENCES players(id) ON DELETE CASCADE,
  kills      INTEGER NOT NULL DEFAULT 0,
  deaths     INTEGER NOT NULL DEFAULT 0,
  score      INTEGER NOT NULL DEFAULT 0,
  abandoned  INTEGER NOT NULL DEFAULT 0,
  played     INTEGER NOT NULL DEFAULT 0,
  points     INTEGER NOT NULL DEFAULT 0,
  coins      INTEGER NOT NULL DEFAULT 0,
  reward_status TEXT NOT NULL DEFAULT 'none',
  PRIMARY KEY (match_id, player_id)
);
CREATE INDEX IF NOT EXISTS idx_cmp_player ON clan_match_players(player_id);
CREATE TABLE IF NOT EXISTS clan_match_results (
  match_id     TEXT PRIMARY KEY REFERENCES clan_matches(id) ON DELETE CASCADE,
  payload_hash TEXT NOT NULL,
  winner_clan  INTEGER,
  rounds_a     INTEGER NOT NULL,
  rounds_b     INTEGER NOT NULL,
  forfeit      INTEGER NOT NULL DEFAULT 0,
  rated        INTEGER NOT NULL DEFAULT 0,
  xp_a         INTEGER NOT NULL DEFAULT 0,
  xp_b         INTEGER NOT NULL DEFAULT 0,
  rating_delta_a INTEGER NOT NULL DEFAULT 0,
  rating_delta_b INTEGER NOT NULL DEFAULT 0,
  reverted     INTEGER NOT NULL DEFAULT 0,
  reported_at  INTEGER NOT NULL
);
CREATE TABLE IF NOT EXISTS clan_match_disputes (
  match_id   TEXT PRIMARY KEY REFERENCES clan_matches(id) ON DELETE CASCADE,
  raised_by  INTEGER NOT NULL,
  clan_id    INTEGER NOT NULL,
  reason     TEXT NOT NULL,
  status     TEXT NOT NULL DEFAULT 'open' CHECK (status IN ('open','upheld','voided')),
  resolved_by INTEGER,
  note       TEXT,
  created_at INTEGER NOT NULL,
  resolved_at INTEGER
);
`;

function create(ctx) {
  const { db, q, tx, fail, now, config, requireString, rateLimit, playerByPublicId, notify, fairplay, economy, clans } = ctx;
  const cfg = { ...DEFAULTS, ...((config.clans || {}).wars || {}) };
  db.exec(SCHEMA);
  const inTx = fn => (db.isTransaction ? fn() : tx(fn));
  const newId = prefix => prefix + crypto.randomBytes(8).toString('hex');
  const dayStart = () => Math.floor(now() / DAY) * DAY;
  const isFlagged = id => !!q.one('SELECT 1 x FROM cheat_strikes WHERE player_id = ? AND flagged = 1', id);
  const ACTIVE = "('pending','live')";

  // ---------- guards used by clans.js ----------
  clans.ctxSet = true;
  ctx.clanwarsGuard = clanId => {
    if (q.one(`SELECT 1 x FROM clan_matches WHERE (clan_a = ? OR clan_b = ?) AND status IN ${ACTIVE}`, clanId, clanId)) fail(409, 'clan_in_war', 'The clan has a clan war match in progress.');
    q.run('DELETE FROM clan_war_queue WHERE clan_id = ?', clanId);
  };
  ctx.clanwarsGuardPlayer = (clanId, playerId) => {
    if (q.one(`SELECT 1 x FROM clan_match_players p JOIN clan_matches m ON m.id = p.match_id WHERE p.player_id = ? AND m.status IN ${ACTIVE}`, playerId)) fail(409, 'in_active_match', 'You are on the roster of a clan war match in progress.');
    const queued = q.one('SELECT 1 x FROM clan_war_queue_players WHERE clan_id = ? AND player_id = ?', clanId, playerId);
    if (queued) q.run('DELETE FROM clan_war_queue WHERE clan_id = ?', clanId);
    q.run('DELETE FROM clan_war_registrations WHERE clan_id = ? AND match_id IS NULL AND EXISTS (SELECT 1 FROM clan_war_roster r WHERE r.event_id = clan_war_registrations.event_id AND r.clan_id = ? AND r.player_id = ?)', clanId, clanId, playerId);
  };

  const formatOf = id => cfg.formats[id] || null;

  function validateRoster(clanId, teamSize, publicIds) {
    if (!Array.isArray(publicIds) || publicIds.length !== teamSize) fail(400, 'invalid_roster', `Pick exactly ${teamSize} players.`);
    if (new Set(publicIds).size !== publicIds.length) fail(400, 'invalid_roster', 'The roster contains duplicates.');
    return publicIds.map(pid => {
      const p = playerByPublicId(pid);
      const m = p && clans.memberOf(p.id);
      if (!m || m.clan_id !== clanId) fail(400, 'invalid_roster', 'Every roster player must be a member of your clan.');
      if (now() - m.joined_at < cfg.minMemberAgeMs) fail(409, 'member_too_new', `${p.username} must be in the clan for at least ${Math.ceil(cfg.minMemberAgeMs / 3600000)} hours before playing clan wars.`);
      const e = fairplay.eligibility(p.id);
      if (!e.competitiveAllowed) fail(403, 'player_restricted', `${p.username} cannot play competitive matches right now.`);
      if (q.one(`SELECT 1 x FROM clan_match_players mp JOIN clan_matches m ON m.id = mp.match_id WHERE mp.player_id = ? AND m.status IN ${ACTIVE}`, p.id)) fail(409, 'player_busy', `${p.username} is already in a clan war match.`);
      return p;
    });
  }
  const requireWarRole = (me) => {
    const m = clans.memberOf(me.id);
    if (!m) fail(403, 'not_in_clan', 'Join a clan first.');
    if (clans.rank(m.role) < clans.rank('co_leader')) fail(403, 'insufficient_role', 'Only the clan leader or co-leaders can manage clan wars.');
    return m;
  };

  // ---------- matches ----------
  function createMatch({ eventId, a, b, rosterA, rosterB, teamSize, map, roundLimit, durationSeconds }) {
    const id = newId('cm_');
    const ca = clans.clanById(a), cb = clans.clanById(b);
    q.run('INSERT INTO clan_matches(id, event_id, clan_a, clan_b, clan_a_name, clan_b_name, team_size, map, round_limit, duration_seconds, created_at) VALUES (?,?,?,?,?,?,?,?,?,?,?)',
      id, eventId, a, b, ca.name, cb.name, teamSize, map, roundLimit, durationSeconds, now());
    for (const pid of rosterA) q.run('INSERT INTO clan_match_players(match_id, clan_id, player_id) VALUES (?,?,?)', id, a, pid);
    for (const pid of rosterB) q.run('INSERT INTO clan_match_players(match_id, clan_id, player_id) VALUES (?,?,?)', id, b, pid);
    for (const pid of [...rosterA, ...rosterB]) notify(pid, 'clanwar', 'Clan war match found', `${ca.name} vs ${cb.name} is ready. Join from the Clan Wars screen.`);
    return id;
  }

  function matchView(m, viewerClanId) {
    const players = q.all(`SELECT mp.*, p.public_id, p.username FROM clan_match_players mp JOIN players p ON p.id = mp.player_id WHERE mp.match_id = ? ORDER BY mp.clan_id, mp.points DESC`, m.id);
    const res = q.one('SELECT * FROM clan_match_results WHERE match_id = ?', m.id);
    const side = viewerClanId === m.clan_b ? 'b' : 'a';
    return {
      id: m.id, status: m.status, map: m.map, teamSize: m.team_size, roundLimit: m.round_limit, durationSeconds: m.duration_seconds,
      clanA: { id: m.clan_a, name: m.clan_a_name }, clanB: { id: m.clan_b, name: m.clan_b_name }, createdAt: m.created_at, finishedAt: m.finished_at,
      mySide: side,
      result: res ? { winnerClanId: res.winner_clan, roundsA: res.rounds_a, roundsB: res.rounds_b, forfeit: !!res.forfeit, rated: !!res.rated, xpA: res.xp_a, xpB: res.xp_b, ratingDeltaA: res.rating_delta_a, ratingDeltaB: res.rating_delta_b, reverted: !!res.reverted } : null,
      players: players.map(p => ({ publicId: p.public_id, username: p.username, clanId: p.clan_id, kills: p.kills, deaths: p.deaths, score: p.score, points: p.points, coins: p.coins, played: !!p.played, abandoned: !!p.abandoned, rewardStatus: p.reward_status })),
    };
  }

  // ---------- matchmaking queue ----------
  const pairKey = (x, y) => [Math.min(x, y), Math.max(x, y)];
  function recentPairMatches(a, b) {
    return q.one(`SELECT COUNT(*) c FROM clan_matches m JOIN clan_match_results r ON r.match_id = m.id
      WHERE ((m.clan_a = ? AND m.clan_b = ?) OR (m.clan_a = ? AND m.clan_b = ?)) AND r.rated = 1 AND r.reverted = 0 AND m.finished_at > ?`, a, b, b, a, now() - DAY).c;
  }
  function pairQueue() {
    return inTx(() => {
      const made = [];
      let progress = true;
      while (progress) {
        progress = false;
        const entries = q.all('SELECT * FROM clan_war_queue ORDER BY queued_at, clan_id');
        for (const A of entries) {
          let best = null;
          for (const B of entries) {
            if (B.clan_id === A.clan_id || B.format_id !== A.format_id) continue;
            const waited = Math.max(now() - A.queued_at, now() - B.queued_at);
            const allowance = cfg.queueBaseRange + Math.floor(waited / 30000) * cfg.queueRangePer30s;
            const diff = Math.abs(A.rating - B.rating);
            if (diff > allowance) continue;
            if (recentPairMatches(A.clan_id, B.clan_id) >= cfg.pairCapPer24h) continue; // would be unrated, don't waste a slot
            if (!best || diff < best.diff) best = { B, diff };
          }
          if (!best) continue;
          const f = formatOf(A.format_id);
          const rosterA = q.all('SELECT player_id FROM clan_war_queue_players WHERE clan_id = ?', A.clan_id).map(r => r.player_id);
          const rosterB = q.all('SELECT player_id FROM clan_war_queue_players WHERE clan_id = ?', best.B.clan_id).map(r => r.player_id);
          const map = cfg.maps[crypto.createHash('sha256').update(A.clan_id + ':' + best.B.clan_id + ':' + now()).digest()[0] % cfg.maps.length];
          made.push(createMatch({ eventId: null, a: A.clan_id, b: best.B.clan_id, rosterA, rosterB, teamSize: f.teamSize, map, roundLimit: f.roundLimit, durationSeconds: f.durationSeconds }));
          q.run('DELETE FROM clan_war_queue WHERE clan_id IN (?,?)', A.clan_id, best.B.clan_id);
          progress = true;
          break;
        }
      }
      return made;
    });
  }

  function sweep() {
    return inTx(() => {
      let voided = 0;
      const stalePending = q.all("SELECT id FROM clan_matches WHERE status = 'pending' AND created_at < ?", now() - cfg.pendingTimeoutMs);
      const staleLive = q.all("SELECT id FROM clan_matches WHERE status = 'live' AND started_at + duration_seconds * 1000 * 2 + ? < ?", cfg.liveGraceMs, now());
      for (const { id } of [...stalePending, ...staleLive]) {
        q.run("UPDATE clan_matches SET status = 'void', finished_at = ? WHERE id = ?", now(), id);
        for (const p of q.all('SELECT player_id FROM clan_match_players WHERE match_id = ?', id)) notify(p.player_id, 'clanwar', 'Clan war match cancelled', 'The match could not be completed (server issue). Nobody was penalised or rewarded.');
        fairplay.audit(null, 'system', 'clanwar.void_stale', id, '');
        voided++;
      }
      const expiredQueue = q.run('DELETE FROM clan_war_queue WHERE queued_at < ?', now() - cfg.queueTimeoutMs).changes;
      return { voided, expiredQueue: Number(expiredQueue) };
    });
  }

  // ---------- results ----------
  const isInt = (v, lo, hi) => Number.isInteger(v) && v >= lo && v <= hi;
  function processResult(matchId, body) {
    const payloadHash = crypto.createHash('sha256').update(JSON.stringify(body)).digest('hex');
    return tx(() => {
      const m = q.one('SELECT * FROM clan_matches WHERE id = ?', matchId);
      if (!m) fail(404, 'match_not_found', 'Unknown clan match.');
      const existing = q.one('SELECT * FROM clan_match_results WHERE match_id = ?', matchId);
      if (existing) {
        if (existing.payload_hash !== payloadHash) fail(409, 'conflicting_result', 'A different result was already recorded for this match.');
        return { duplicate: true, match: matchView(m, m.clan_a) };
      }
      if (m.status === 'void') fail(409, 'match_void', 'This match was cancelled.');
      if (m.status !== 'live') fail(409, 'match_not_live', 'The match must be started by the game server before a result is accepted.');
      const status = String(body.status || '');
      if (!['completed', 'forfeit', 'crashed'].includes(status)) fail(400, 'invalid_status', "status must be 'completed', 'forfeit' or 'crashed'.");

      if (status === 'crashed') {
        q.run("UPDATE clan_matches SET status = 'void', finished_at = ? WHERE id = ?", now(), m.id);
        q.run('INSERT INTO clan_match_results(match_id, payload_hash, winner_clan, rounds_a, rounds_b, reported_at) VALUES (?,?,?,?,?,?)', m.id, payloadHash, null, 0, 0, now());
        for (const p of q.all('SELECT player_id FROM clan_match_players WHERE match_id = ?', m.id)) notify(p.player_id, 'clanwar', 'Clan war match cancelled', 'The game server reported a failure. No rewards or penalties were applied.');
        fairplay.audit(null, 'server', 'clanwar.crashed', m.id, '');
        return { duplicate: false, match: matchView(q.one('SELECT * FROM clan_matches WHERE id = ?', m.id), m.clan_a) };
      }

      // roster + stats validation
      const roster = new Map(q.all('SELECT mp.*, p.public_id FROM clan_match_players mp JOIN players p ON p.id = mp.player_id WHERE mp.match_id = ?', m.id).map(r => [r.public_id, r]));
      if (!Array.isArray(body.players) || body.players.length > roster.size) fail(400, 'invalid_players', 'players must list roster members only.');
      const seen = new Set();
      const stats = new Map();
      for (const e of body.players) {
        const r = e && roster.get(e.publicId);
        if (!r) fail(422, 'unknown_player', 'A reported player is not on this match roster.');
        if (seen.has(e.publicId)) fail(422, 'duplicate_player', 'A player was reported twice.');
        seen.add(e.publicId);
        if (!isInt(e.kills, 0, 200) || !isInt(e.deaths, 0, 200) || !isInt(e.score, 0, 100000)) fail(422, 'invalid_stats', 'kills, deaths and score must be sensible non-negative integers.');
        stats.set(r.player_id, { row: r, kills: e.kills, deaths: e.deaths, score: e.score, abandoned: e.abandoned === true });
      }
      const sum = (clanId, k) => [...stats.values()].filter(s => s.row.clan_id === clanId).reduce((n, s) => n + s[k], 0);
      // Eliminations must be explainable by the other side's deaths (friendly fire is not tracked as a kill).
      if (sum(m.clan_a, 'kills') > sum(m.clan_b, 'deaths') || sum(m.clan_b, 'kills') > sum(m.clan_a, 'deaths')) fail(422, 'inconsistent_eliminations', 'Eliminations do not match the opposing deaths.');

      let roundsA = 0, roundsB = 0, winnerClan = null, forfeit = false;
      if (status === 'completed') {
        if (!Array.isArray(body.rounds) || body.rounds.length < 1 || body.rounds.length > m.round_limit * 2 - 1) fail(422, 'invalid_rounds', 'rounds must list every played round within the format limit.');
        for (const r of body.rounds) {
          if (roundsA >= m.round_limit || roundsB >= m.round_limit) fail(422, 'invalid_rounds', 'Rounds were reported after a side had already won.');
          if (r.winnerSide === 'a') roundsA++; else if (r.winnerSide === 'b') roundsB++; else fail(422, 'invalid_rounds', "winnerSide must be 'a' or 'b'.");
        }
        if (roundsA === m.round_limit && roundsB < m.round_limit) winnerClan = m.clan_a;
        else if (roundsB === m.round_limit && roundsA < m.round_limit) winnerClan = m.clan_b;
        else if (body.endedByTime === true) winnerClan = roundsA === roundsB ? null : (roundsA > roundsB ? m.clan_a : m.clan_b);
        else fail(422, 'invalid_rounds', 'The round tally does not produce a winner for this format.');
      } else {
        if (!['a', 'b'].includes(body.forfeitSide)) fail(422, 'invalid_forfeit', "forfeitSide must be 'a' or 'b'.");
        forfeit = true;
        winnerClan = body.forfeitSide === 'a' ? m.clan_b : m.clan_a;
        roundsA = winnerClan === m.clan_a ? m.round_limit : 0;
        roundsB = winnerClan === m.clan_b ? m.round_limit : 0;
      }

      const pairCapped = recentPairMatches(m.clan_a, m.clan_b) >= cfg.pairCapPer24h;
      const rated = !pairCapped;
      const used = clanId => q.one(`SELECT COALESCE(SUM(CASE WHEN m.clan_a = ? THEN r.xp_a ELSE r.xp_b END),0) s FROM clan_matches m JOIN clan_match_results r ON r.match_id = m.id
        WHERE (m.clan_a = ? OR m.clan_b = ?) AND r.reverted = 0 AND m.finished_at >= ?`, clanId, clanId, clanId, dayStart()).s;
      const xpFor = clanId => {
        if (!rated) return 0;
        const base = winnerClan === null ? cfg.drawXp : (winnerClan === clanId ? cfg.winXp : cfg.lossXp);
        return Math.max(0, Math.min(base, cfg.dailyClanXpCap - used(clanId)));
      };
      const xpA = xpFor(m.clan_a), xpB = xpFor(m.clan_b);
      const A = clans.clanById(m.clan_a), B = clans.clanById(m.clan_b);
      let dA = 0, dB = 0;
      if (rated && A && B) {
        const scoreA = winnerClan === null ? 0.5 : (winnerClan === m.clan_a ? 1 : 0);
        const expA = 1 / (1 + Math.pow(10, (B.rating - A.rating) / 400));
        dA = Math.round(cfg.ratingK * (scoreA - expA));
        dB = -dA;
      }

      q.run("UPDATE clan_matches SET status = 'completed', finished_at = ? WHERE id = ?", now(), m.id);
      q.run('INSERT INTO clan_match_results(match_id, payload_hash, winner_clan, rounds_a, rounds_b, forfeit, rated, xp_a, xp_b, rating_delta_a, rating_delta_b, reported_at) VALUES (?,?,?,?,?,?,?,?,?,?,?,?)',
        m.id, payloadHash, winnerClan, roundsA, roundsB, forfeit ? 1 : 0, rated ? 1 : 0, xpA, xpB, dA, dB, now());

      for (const [, r] of roster) {
        const s = stats.get(r.player_id);
        const played = !!s;
        const abandoned = !played || s.abandoned;
        const won = winnerClan === r.clan_id;
        const points = rated && played && !abandoned ? s.kills * 2 + (won ? 5 : 0) : 0;
        let coins = 0, rewardStatus = 'none';
        if (rated && played && !abandoned) {
          if (isFlagged(r.player_id)) rewardStatus = 'held';
          else {
            coins = cfg.participationCoins + (won ? cfg.winCoins : 0);
            economy.apply(r.player_id, 'coins', coins, { kind: 'clan_war', refType: 'clan_match', refId: m.id, key: `clanwar:${m.id}:${r.player_id}` });
            rewardStatus = 'paid';
          }
        } else if (abandoned) rewardStatus = 'forfeited';
        else if (!rated) rewardStatus = 'unrated';
        q.run('UPDATE clan_match_players SET kills=?, deaths=?, score=?, abandoned=?, played=?, points=?, coins=?, reward_status=? WHERE match_id=? AND player_id=?',
          s ? s.kills : 0, s ? s.deaths : 0, s ? s.score : 0, abandoned ? 1 : 0, played ? 1 : 0, points, coins, rewardStatus, m.id, r.player_id);
        if (points) q.run('UPDATE clan_members SET contribution_xp = contribution_xp + ? WHERE player_id = ? AND clan_id = ?', points, r.player_id, r.clan_id);
        if (abandoned) fairplay.recordAbandon(r.player_id, m.id); else fairplay.recordCleanMatch(r.player_id, m.id);
      }

      if (rated) {
        for (const [c, xp, d] of [[m.clan_a, xpA, dA], [m.clan_b, xpB, dB]]) {
          if (!clans.clanById(c)) continue;
          if (xp) clans.addXp(c, xp);
          q.run(`UPDATE clans SET rating = max(0, rating + ?), wins = wins + ?, losses = losses + ?, draws = draws + ? WHERE id = ?`, d,
            winnerClan === c ? 1 : 0, winnerClan !== null && winnerClan !== c ? 1 : 0, winnerClan === null ? 1 : 0, c);
        }
      }
      for (const p of roster.values()) notify(p.player_id, 'clanwar', 'Clan war finished', winnerClan === null ? 'The match ended in a draw.' : (winnerClan === p.clan_id ? 'Your clan won!' : 'Your clan lost this one.'));
      fairplay.audit(null, 'server', 'clanwar.result', m.id, `winner=${winnerClan} rated=${rated} forfeit=${forfeit}`);
      return { duplicate: false, match: matchView(q.one('SELECT * FROM clan_matches WHERE id = ?', m.id), m.clan_a) };
    });
  }

  // Reverses clan XP, rating, W/L and contribution of a voided result. Coins already paid are NOT clawed back.
  function revertResult(matchId) {
    const r = q.one('SELECT * FROM clan_match_results WHERE match_id = ?', matchId);
    const m = q.one('SELECT * FROM clan_matches WHERE id = ?', matchId);
    if (!r || r.reverted) return;
    if (r.rated) {
      for (const [c, xp, d] of [[m.clan_a, r.xp_a, r.rating_delta_a], [m.clan_b, r.xp_b, r.rating_delta_b]]) {
        if (!clans.clanById(c)) continue;
        q.run('UPDATE clans SET xp = max(0, xp - ?), rating = max(0, rating - ?), wins = max(0, wins - ?), losses = max(0, losses - ?), draws = max(0, draws - ?) WHERE id = ?',
          xp, d, r.winner_clan === c ? 1 : 0, r.winner_clan !== null && r.winner_clan !== c ? 1 : 0, r.winner_clan === null ? 1 : 0, c);
      }
      for (const p of q.all('SELECT * FROM clan_match_players WHERE match_id = ? AND points > 0', matchId)) {
        q.run('UPDATE clan_members SET contribution_xp = max(0, contribution_xp - ?) WHERE player_id = ? AND clan_id = ?', p.points, p.player_id, p.clan_id);
      }
    }
    q.run('UPDATE clan_match_results SET reverted = 1 WHERE match_id = ?', matchId);
  }

  const eventView = (e, clanId) => {
    const reg = clanId ? q.one('SELECT * FROM clan_war_registrations WHERE event_id = ? AND clan_id = ?', e.id, clanId) : null;
    return {
      id: e.id, title: e.title, teamSize: e.team_size, map: e.map, roundLimit: e.round_limit, durationSeconds: e.duration_seconds,
      startsAt: e.starts_at, registrationClosesAt: e.registration_closes_at, status: e.status,
      registered: !!reg, matchId: reg ? reg.match_id : null,
      registeredClans: q.one('SELECT COUNT(*) c FROM clan_war_registrations WHERE event_id = ?', e.id).c,
    };
  };

  function install(route) {
    route('GET', '/api/clanwars/overview', ({ me }) => {
      sweep(); pairQueue();
      const m = clans.memberOf(me.id);
      const clanId = m ? m.clan_id : null;
      const events = q.all("SELECT * FROM clan_war_events WHERE status IN ('scheduled','matching') AND starts_at > ? ORDER BY starts_at LIMIT 10", now() - 3600000).map(e => eventView(e, clanId));
      const qEntry = clanId ? q.one('SELECT * FROM clan_war_queue WHERE clan_id = ?', clanId) : null;
      const active = clanId ? q.one(`SELECT * FROM clan_matches WHERE (clan_a = ? OR clan_b = ?) AND status IN ('pending','live','disputed') ORDER BY created_at DESC LIMIT 1`, clanId, clanId) : null;
      const history = clanId ? q.all(`SELECT * FROM clan_matches WHERE (clan_a = ? OR clan_b = ?) AND status IN ('completed','void','disputed') ORDER BY created_at DESC LIMIT 15`, clanId, clanId).map(x => matchView(x, clanId)) : [];
      return { body: {
        clanId: clanId || 0, role: m ? m.role : null, canManage: !!m && clans.rank(m.role) >= clans.rank('co_leader'),
        formats: Object.entries(cfg.formats).map(([id, f]) => ({ id, ...f })), maps: cfg.maps,
        rewards: { winXp: cfg.winXp, lossXp: cfg.lossXp, drawXp: cfg.drawXp, winCoins: cfg.winCoins, participationCoins: cfg.participationCoins, pairCapPer24h: cfg.pairCapPer24h, minMemberAgeHours: cfg.minMemberAgeMs / 3600000 },
        events, queue: qEntry ? { formatId: qEntry.format_id, queuedAt: qEntry.queued_at } : null,
        activeMatch: active ? matchView(active, clanId) : null, history,
        eligibility: fairplay.eligibility(me.id),
      } };
    });

    route('POST', '/api/clanwars/queue', ({ me, body }) => {
      rateLimit('clan', me.id);
      const actor = requireWarRole(me);
      const f = formatOf(String(body.formatId || ''));
      if (!f) fail(400, 'invalid_format', 'Unknown match format.');
      sweep();
      const result = tx(() => {
        if (q.one(`SELECT 1 x FROM clan_matches WHERE (clan_a = ? OR clan_b = ?) AND status IN ${ACTIVE}`, actor.clan_id, actor.clan_id)) fail(409, 'clan_in_war', 'Your clan already has a match in progress.');
        if (q.one('SELECT 1 x FROM clan_war_queue WHERE clan_id = ?', actor.clan_id)) fail(409, 'already_queued', 'Your clan is already in the queue.');
        const players = validateRoster(actor.clan_id, f.teamSize, body.players);
        const c = clans.clanById(actor.clan_id);
        q.run('INSERT INTO clan_war_queue(clan_id, format_id, rating, queued_by, queued_at) VALUES (?,?,?,?,?)', c.id, body.formatId, c.rating, me.id, now());
        for (const p of players) q.run('INSERT INTO clan_war_queue_players(clan_id, player_id) VALUES (?,?)', c.id, p.id);
        return true;
      });
      pairQueue();
      const matched = q.one(`SELECT id FROM clan_matches WHERE (clan_a = ? OR clan_b = ?) AND status = 'pending' ORDER BY created_at DESC LIMIT 1`, actor.clan_id, actor.clan_id);
      return { status: 201, body: { queued: !matched && result, matchId: matched ? matched.id : null } };
    });
    route('DELETE', '/api/clanwars/queue', ({ me }) => {
      const actor = requireWarRole(me);
      q.run('DELETE FROM clan_war_queue WHERE clan_id = ?', actor.clan_id);
      return { body: { ok: true } };
    });

    route('POST', '/api/clanwars/events/:id/register', ({ me, params, body }) => {
      rateLimit('clan', me.id);
      const actor = requireWarRole(me);
      return tx(() => {
        const e = q.one("SELECT * FROM clan_war_events WHERE id = ? AND status = 'scheduled'", params.id);
        if (!e) fail(404, 'event_not_found', 'That event is not open for registration.');
        if (now() >= e.registration_closes_at) fail(409, 'registration_closed', 'Registration for this event has closed.');
        const players = validateRoster(actor.clan_id, e.team_size, body.players);
        const c = clans.clanById(actor.clan_id);
        q.run('DELETE FROM clan_war_registrations WHERE event_id = ? AND clan_id = ?', e.id, c.id);
        q.run('INSERT INTO clan_war_registrations(event_id, clan_id, registered_by, rating, created_at) VALUES (?,?,?,?,?)', e.id, c.id, me.id, c.rating, now());
        for (const p of players) q.run('INSERT INTO clan_war_roster(event_id, clan_id, player_id) VALUES (?,?,?)', e.id, c.id, p.id);
        return { status: 201, body: eventView(e, c.id) };
      });
    });
    route('DELETE', '/api/clanwars/events/:id/register', ({ me, params }) => {
      const actor = requireWarRole(me);
      const r = q.run('DELETE FROM clan_war_registrations WHERE event_id = ? AND clan_id = ? AND match_id IS NULL', params.id, actor.clan_id);
      if (!r.changes) fail(404, 'not_registered', 'Your clan is not registered for that event (or it was already matched).');
      return { body: { ok: true } };
    });

    route('GET', '/api/clanwars/matches/:id', ({ me, params }) => {
      const m = q.one('SELECT * FROM clan_matches WHERE id = ?', params.id);
      const mem = clans.memberOf(me.id);
      if (!m || !mem || (mem.clan_id !== m.clan_a && mem.clan_id !== m.clan_b)) fail(404, 'match_not_found', 'Match not found.');
      return { body: matchView(m, mem.clan_id) };
    });

    route('GET', '/api/clanwars/contributions', ({ me }) => {
      const mem = clans.memberOf(me.id);
      if (!mem) fail(403, 'not_in_clan', 'Join a clan first.');
      const rows = q.all(`SELECT p.public_id, p.username, COUNT(mp.match_id) matches, COALESCE(SUM(mp.kills),0) kills, COALESCE(SUM(mp.deaths),0) deaths, COALESCE(SUM(mp.points),0) points
        FROM clan_members cm JOIN players p ON p.id = cm.player_id
        LEFT JOIN (SELECT mp.* FROM clan_match_players mp JOIN clan_matches m ON m.id = mp.match_id JOIN clan_match_results r ON r.match_id = m.id WHERE m.status = 'completed' AND r.rated = 1 AND mp.played = 1) mp
          ON mp.player_id = cm.player_id AND mp.clan_id = cm.clan_id
        WHERE cm.clan_id = ? GROUP BY p.id ORDER BY points DESC, p.username LIMIT 100`, mem.clan_id);
      return { body: { members: rows.map(r => ({ publicId: r.public_id, username: r.username, matches: r.matches, kills: r.kills, deaths: r.deaths, points: r.points })) } };
    });

    route('POST', '/api/clanwars/matches/:id/dispute', ({ me, params, body }) => {
      rateLimit('clan', me.id);
      const actor = requireWarRole(me);
      const reason = requireString(body.reason, 'reason', 10, 400);
      return tx(() => {
        const m = q.one('SELECT * FROM clan_matches WHERE id = ?', params.id);
        if (!m || (actor.clan_id !== m.clan_a && actor.clan_id !== m.clan_b)) fail(404, 'match_not_found', 'Match not found.');
        if (m.status !== 'completed') fail(409, 'not_disputable', 'Only completed matches can be disputed.');
        if (now() - m.finished_at > cfg.disputeWindowMs) fail(409, 'dispute_window_closed', 'The dispute window has closed.');
        q.run('INSERT INTO clan_match_disputes(match_id, raised_by, clan_id, reason, created_at) VALUES (?,?,?,?,?)', m.id, me.id, actor.clan_id, reason, now());
        q.run("UPDATE clan_matches SET status = 'disputed' WHERE id = ?", m.id);
        fairplay.audit(me.id, 'player', 'clanwar.dispute', m.id, '');
        return { status: 201, body: { ok: true } };
      });
    });

    // ---- moderators ----
    route('GET', '/api/admin/clanwars/disputes', ({ me }) => {
      fairplay.moderator(me); rateLimit('admin', me.id);
      return { body: { disputes: q.all("SELECT d.*, m.clan_a_name, m.clan_b_name FROM clan_match_disputes d JOIN clan_matches m ON m.id = d.match_id WHERE d.status = 'open' ORDER BY d.created_at LIMIT 100")
        .map(d => ({ matchId: d.match_id, clanId: d.clan_id, reason: d.reason, createdAt: d.created_at, clanA: d.clan_a_name, clanB: d.clan_b_name })) } };
    });
    route('POST', '/api/admin/clanwars/matches/:id/resolve', ({ me, params, body }) => {
      fairplay.moderator(me); rateLimit('admin', me.id);
      const decision = String(body.decision || '');
      if (!['uphold', 'void'].includes(decision)) fail(400, 'invalid_decision', "decision must be 'uphold' or 'void'.");
      const note = requireString(body.note, 'note', 3, 400);
      return tx(() => {
        const d = q.one("SELECT * FROM clan_match_disputes WHERE match_id = ? AND status = 'open'", params.id);
        if (!d) fail(404, 'dispute_not_found', 'No open dispute for that match.');
        q.run('UPDATE clan_match_disputes SET status = ?, resolved_by = ?, note = ?, resolved_at = ? WHERE match_id = ?', decision === 'void' ? 'voided' : 'upheld', me.id, note, now(), d.match_id);
        if (decision === 'void') { revertResult(d.match_id); q.run("UPDATE clan_matches SET status = 'void' WHERE id = ?", d.match_id); }
        else q.run("UPDATE clan_matches SET status = 'completed' WHERE id = ?", d.match_id);
        fairplay.audit(me.id, 'moderator', 'clanwar.dispute.' + decision, d.match_id, '');
        return { body: { ok: true, status: decision === 'void' ? 'void' : 'completed' } };
      });
    });

    // ---- dedicated server ----
    route('POST', '/api/internal/clanwars/events', ({ req, ip, body }) => {
      ctx.requireServerKey(req, ip);
      const id = requireString(body.id, 'id', 3, 40);
      if (!/^[A-Za-z0-9_-]+$/.test(id)) fail(400, 'invalid_id', 'id may use letters, numbers, dashes and underscores.');
      const title = requireString(body.title, 'title', 3, 60);
      const map = requireString(body.map, 'map', 2, 40);
      if (!cfg.maps.includes(map)) fail(400, 'invalid_map', 'Unknown map. Allowed: ' + cfg.maps.join(', '));
      if (!isInt(body.teamSize, 1, 8) || !isInt(body.roundLimit, 1, 15) || !isInt(body.durationSeconds, 60, 3600)) fail(400, 'invalid_format', 'teamSize 1-8, roundLimit 1-15, durationSeconds 60-3600.');
      if (!isInt(body.startsAt, 1, 4e12) || !isInt(body.registrationClosesAt, 1, 4e12) || body.registrationClosesAt > body.startsAt) fail(400, 'invalid_schedule', 'registrationClosesAt must be <= startsAt.');
      const r = q.run('INSERT OR IGNORE INTO clan_war_events(id, title, team_size, map, round_limit, duration_seconds, starts_at, registration_closes_at, created_at) VALUES (?,?,?,?,?,?,?,?,?)',
        id, title, body.teamSize, map, body.roundLimit, body.durationSeconds, body.startsAt, body.registrationClosesAt, now());
      return { status: r.changes ? 201 : 200, body: { id, created: !!r.changes } };
    }, { auth: false });

    route('POST', '/api/internal/clanwars/events/:id/pair', ({ req, ip, params }) => {
      ctx.requireServerKey(req, ip);
      return tx(() => {
        const e = q.one("SELECT * FROM clan_war_events WHERE id = ? AND status IN ('scheduled','matching')", params.id);
        if (!e) fail(404, 'event_not_found', 'Unknown or finished event.');
        if (now() < e.registration_closes_at) fail(409, 'registration_open', 'Registration has not closed yet.');
        const regs = q.all('SELECT * FROM clan_war_registrations WHERE event_id = ? AND match_id IS NULL ORDER BY rating DESC, clan_id', e.id);
        const matches = [];
        for (let i = 0; i + 1 < regs.length; i += 2) {
          const a = regs[i], b = regs[i + 1];
          const roster = r => q.all('SELECT player_id FROM clan_war_roster WHERE event_id = ? AND clan_id = ?', e.id, r.clan_id).map(x => x.player_id);
          const id = createMatch({ eventId: e.id, a: a.clan_id, b: b.clan_id, rosterA: roster(a), rosterB: roster(b), teamSize: e.team_size, map: e.map, roundLimit: e.round_limit, durationSeconds: e.duration_seconds });
          q.run('UPDATE clan_war_registrations SET match_id = ? WHERE event_id = ? AND clan_id IN (?,?)', id, e.id, a.clan_id, b.clan_id);
          matches.push(id);
        }
        const bye = regs.length % 2 ? regs[regs.length - 1] : null;
        if (bye) { for (const pid of clans.memberIds(bye.clan_id)) notify(pid, 'clanwar', 'No opponent this time', `No opposing clan was available for ${e.title}.`); q.run('UPDATE clan_war_registrations SET match_id = ? WHERE event_id = ? AND clan_id = ?', 'bye', e.id, bye.clan_id); }
        q.run("UPDATE clan_war_events SET status = 'matching' WHERE id = ?", e.id);
        return { body: { matches, bye: bye ? bye.clan_id : null } };
      });
    }, { auth: false });

    route('GET', '/api/internal/clanwars/matches', ({ req, ip, query }) => {
      ctx.requireServerKey(req, ip);
      sweep(); pairQueue();
      const status = query.get('status') || 'pending';
      if (!['pending', 'live'].includes(status)) fail(400, 'invalid_status', 'status must be pending or live.');
      return { body: { matches: q.all('SELECT * FROM clan_matches WHERE status = ? ORDER BY created_at LIMIT 50', status).map(m => ({
        ...matchView(m, m.clan_a),
        roster: q.all('SELECT mp.clan_id, p.public_id FROM clan_match_players mp JOIN players p ON p.id = mp.player_id WHERE mp.match_id = ?', m.id).map(r => ({ clanId: r.clan_id, publicId: r.public_id, side: r.clan_id === m.clan_a ? 'a' : 'b' })),
      })) } };
    }, { auth: false });

    route('POST', '/api/internal/clanwars/matches/:id/start', ({ req, ip, params, body }) => {
      ctx.requireServerKey(req, ip);
      const serverId = requireString(body.serverId, 'serverId', 2, 64);
      return tx(() => {
        const m = q.one('SELECT * FROM clan_matches WHERE id = ?', params.id);
        if (!m) fail(404, 'match_not_found', 'Unknown clan match.');
        if (m.status === 'live' && m.server_id === serverId) return { body: { started: true, duplicate: true } };
        if (m.status !== 'pending') fail(409, 'invalid_state', 'Only pending matches can be started.');
        q.run("UPDATE clan_matches SET status = 'live', server_id = ?, started_at = ? WHERE id = ?", serverId, now(), m.id);
        return { body: { started: true, duplicate: false } };
      });
    }, { auth: false });

    route('POST', '/api/internal/clanwars/matches/:id/result', ({ req, ip, params, body }) => {
      ctx.requireServerKey(req, ip);
      return { body: processResult(params.id, body) };
    }, { auth: false });

    route('POST', '/api/internal/clanwars/sweep', ({ req, ip }) => {
      ctx.requireServerKey(req, ip);
      return { body: { ...sweep(), paired: pairQueue().length } };
    }, { auth: false });
  }

  return { install, sweep, pairQueue, processResult, cfg };
}

module.exports = { create, DEFAULTS };
