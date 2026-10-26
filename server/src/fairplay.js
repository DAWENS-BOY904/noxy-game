'use strict';
// Fair play: reports (extends the existing `reports` table), reputation, moderation, appeals, audit trail.
// Design rules:
//  - Report COUNTS never punish anyone. Only a moderator decision (or a server-confirmed abandon pattern) changes reputation.
//  - Moderation tools require a row in `moderators` (granted by the operator in the DB, never through the API).
//  - Players can only see their own reports (status only) and actions against themselves.
//  - audit_log is append-only (triggers). Reports/evidence are purged after a retention period.

const DAY = 86400000;

const DEFAULTS = {
  startScore: 100,
  abandonGraceCount: 2, // abandons within the window that cost nothing (accidental disconnects)
  abandonWindowDays: 7,
  abandonPenalty: 3,
  cleanMatchBonus: 1,
  actionPenalty: { warn: 5, mute: 10, suspend: 25 },
  levels: [
    { id: 'trusted', min: 90 },
    { id: 'good', min: 70 },
    { id: 'caution', min: 40 },
    { id: 'restricted', min: 0 },
  ],
  restrictedBlocksCompetitive: true,
  maxReportsPerDay: 15,
  lowCredibilityDismissed: 5, // dismissed reports in 30 days before the reporter is limited
  lowCredibilityPerDay: 2,
  maxEvidenceLength: 500,
  closedReportRetentionDays: 90,
  evidenceRetentionDays: 30,
  openReportMaxAgeDays: 180,
  appealWindowDays: 14,
};

const SCHEMA = `
CREATE TABLE IF NOT EXISTS moderators (
  player_id  INTEGER PRIMARY KEY REFERENCES players(id) ON DELETE CASCADE,
  role       TEXT NOT NULL CHECK (role IN ('moderator','admin')),
  created_at INTEGER NOT NULL
);
CREATE TABLE IF NOT EXISTS player_reputation (
  player_id         INTEGER PRIMARY KEY REFERENCES players(id) ON DELETE CASCADE,
  score             INTEGER NOT NULL CHECK (score BETWEEN 0 AND 100),
  abandons          INTEGER NOT NULL DEFAULT 0,
  confirmed_actions INTEGER NOT NULL DEFAULT 0,
  clean_matches     INTEGER NOT NULL DEFAULT 0,
  updated_at        INTEGER NOT NULL
);
CREATE TABLE IF NOT EXISTS reputation_events (
  id         INTEGER PRIMARY KEY AUTOINCREMENT,
  player_id  INTEGER NOT NULL REFERENCES players(id) ON DELETE CASCADE,
  kind       TEXT NOT NULL,
  delta      INTEGER NOT NULL,
  ref_id     TEXT,
  key        TEXT NOT NULL UNIQUE,
  created_at INTEGER NOT NULL
);
CREATE INDEX IF NOT EXISTS idx_rep_events_player ON reputation_events(player_id, kind, created_at);
CREATE TABLE IF NOT EXISTS moderation_actions (
  id           INTEGER PRIMARY KEY AUTOINCREMENT,
  target_id    INTEGER NOT NULL REFERENCES players(id) ON DELETE CASCADE,
  moderator_id INTEGER,
  report_id    INTEGER,
  action       TEXT NOT NULL CHECK (action IN ('warn','mute','suspend','dismiss','note')),
  public_reason TEXT,
  internal_note TEXT,
  expires_at   INTEGER,
  revoked_at   INTEGER,
  created_at   INTEGER NOT NULL
);
CREATE INDEX IF NOT EXISTS idx_mod_actions_target ON moderation_actions(target_id, created_at);
CREATE TABLE IF NOT EXISTS appeals (
  id              INTEGER PRIMARY KEY AUTOINCREMENT,
  action_id       INTEGER NOT NULL UNIQUE REFERENCES moderation_actions(id) ON DELETE CASCADE,
  player_id       INTEGER NOT NULL REFERENCES players(id) ON DELETE CASCADE,
  message         TEXT NOT NULL,
  status          TEXT NOT NULL DEFAULT 'open' CHECK (status IN ('open','upheld','overturned')),
  resolved_by     INTEGER,
  resolution_note TEXT,
  created_at      INTEGER NOT NULL,
  resolved_at     INTEGER
);
CREATE TABLE IF NOT EXISTS audit_log (
  id         INTEGER PRIMARY KEY AUTOINCREMENT,
  actor_id   INTEGER,
  actor_kind TEXT NOT NULL,
  action     TEXT NOT NULL,
  target     TEXT,
  detail     TEXT,
  created_at INTEGER NOT NULL
);
CREATE TRIGGER IF NOT EXISTS audit_log_no_update BEFORE UPDATE ON audit_log BEGIN SELECT RAISE(ABORT, 'audit_log is append-only'); END;
CREATE TRIGGER IF NOT EXISTS audit_log_no_delete BEFORE DELETE ON audit_log BEGIN SELECT RAISE(ABORT, 'audit_log is append-only'); END;
CREATE TRIGGER IF NOT EXISTS mod_actions_immutable BEFORE UPDATE OF target_id, moderator_id, report_id, action, public_reason, internal_note, created_at ON moderation_actions
  BEGIN SELECT RAISE(ABORT, 'moderation_actions are immutable (only revoked_at may change)'); END;
`;

function create(ctx) {
  const { db, q, tx, fail, now, config, requireString, rateLimit, playerByPublicId, notify } = ctx;
  const cfg = { ...DEFAULTS, ...(config.fairplay || {}) };

  db.exec(SCHEMA);
  const cols = db.prepare('PRAGMA table_info(reports)').all().map(c => c.name);
  const addCol = (name, ddl) => { if (!cols.includes(name)) db.exec(`ALTER TABLE reports ADD COLUMN ${name} ${ddl}`); };
  addCol('status', "TEXT NOT NULL DEFAULT 'open' CHECK (status IN ('open','dismissed','confirmed','expired'))");
  addCol('evidence', 'TEXT');
  addCol('match_id', 'TEXT');
  addCol('reviewed_by', 'INTEGER');
  addCol('resolved_at', 'INTEGER');
  db.exec('CREATE INDEX IF NOT EXISTS idx_reports_status ON reports(status, created_at)');
  db.exec('CREATE INDEX IF NOT EXISTS idx_reports_target ON reports(target_id, status)');

  const inTx = fn => (db.isTransaction ? fn() : tx(fn));
  const clamp = (n, lo, hi) => Math.min(hi, Math.max(lo, n));

  function audit(actorId, actorKind, action, target, detail) {
    q.run('INSERT INTO audit_log(actor_id, actor_kind, action, target, detail, created_at) VALUES (?,?,?,?,?,?)',
      actorId ?? null, actorKind, action, target == null ? null : String(target), detail == null ? null : String(detail).slice(0, 500), now());
  }

  function ensureRep(id) {
    q.run('INSERT OR IGNORE INTO player_reputation(player_id, score, updated_at) VALUES (?,?,?)', id, cfg.startScore, now());
    return q.one('SELECT * FROM player_reputation WHERE player_id = ?', id);
  }
  const levelFor = score => (cfg.levels.find(l => score >= l.min) || cfg.levels[cfg.levels.length - 1]).id;

  // Idempotent: the same key can never change the score twice.
  function addEvent(playerId, kind, delta, refId, key) {
    return inTx(() => {
      if (q.one('SELECT 1 x FROM reputation_events WHERE key = ?', key)) return { duplicate: true };
      const rep = ensureRep(playerId);
      const score = clamp(rep.score + delta, 0, 100);
      q.run('INSERT INTO reputation_events(player_id, kind, delta, ref_id, key, created_at) VALUES (?,?,?,?,?,?)', playerId, kind, score - rep.score, refId == null ? null : String(refId), key, now());
      q.run('UPDATE player_reputation SET score = ?, updated_at = ? WHERE player_id = ?', score, now(), playerId);
      return { duplicate: false, score };
    });
  }

  const activeAction = (playerId, action) => q.one(
    'SELECT * FROM moderation_actions WHERE target_id = ? AND action = ? AND revoked_at IS NULL AND (expires_at IS NULL OR expires_at > ?) ORDER BY id DESC LIMIT 1', playerId, action, now());

  function eligibility(playerId) {
    const rep = ensureRep(playerId);
    const level = levelFor(rep.score);
    const suspension = activeAction(playerId, 'suspend');
    const mute = activeAction(playerId, 'mute');
    const reasons = [];
    if (suspension) reasons.push('Competitive play is suspended until ' + new Date(suspension.expires_at || 0).toISOString() + '.');
    if (cfg.restrictedBlocksCompetitive && level === 'restricted') reasons.push('Reputation level is Restricted. Play clean matches to recover.');
    return {
      score: rep.score, level,
      competitiveAllowed: reasons.length === 0,
      chatAllowed: !mute,
      suspendedUntil: suspension ? suspension.expires_at : null,
      mutedUntil: mute ? mute.expires_at : null,
      reasons,
    };
  }
  function requireCompetitive(playerId) {
    const e = eligibility(playerId);
    if (!e.competitiveAllowed) fail(403, 'competitive_restricted', e.reasons.join(' '));
  }
  function requireChat(playerId) {
    const e = eligibility(playerId);
    if (!e.chatAllowed) fail(403, 'muted', 'You are muted from clan chat right now.');
  }

  function rules() {
    return {
      startScore: cfg.startScore,
      levels: cfg.levels,
      abandon: { graceCount: cfg.abandonGraceCount, windowDays: cfg.abandonWindowDays, penalty: cfg.abandonPenalty, note: 'Only abandons confirmed by the game server count, and the first ones in the window are free.' },
      cleanMatchBonus: cfg.cleanMatchBonus,
      actionPenalty: cfg.actionPenalty,
      restrictedBlocksCompetitive: cfg.restrictedBlocksCompetitive,
      reportsNeverAutoPunish: true,
      appealWindowDays: cfg.appealWindowDays,
      retention: { closedReportDays: cfg.closedReportRetentionDays, evidenceDays: cfg.evidenceRetentionDays },
    };
  }

  // ---------- reports ----------
  function checkReporter(reporterId) {
    const dayAgo = now() - DAY;
    const today = q.one('SELECT COUNT(*) c FROM reports WHERE reporter_id = ? AND created_at > ?', reporterId, dayAgo).c;
    const dismissed = q.one("SELECT COUNT(*) c FROM reports WHERE reporter_id = ? AND status = 'dismissed' AND created_at > ?", reporterId, now() - 30 * DAY).c;
    const cap = dismissed >= cfg.lowCredibilityDismissed ? cfg.lowCredibilityPerDay : cfg.maxReportsPerDay;
    if (today >= cap) fail(429, 'report_limit', 'You have reached your daily report limit. Reports are reviewed by people; please wait before sending more.');
  }
  function cleanEvidence(v) {
    if (v === undefined || v === null || v === '') return null;
    if (typeof v !== 'string' || v.length > cfg.maxEvidenceLength) fail(400, 'invalid_evidence', `Evidence must be text up to ${cfg.maxEvidenceLength} characters.`);
    return v;
  }
  function cleanMatchId(v) {
    if (v === undefined || v === null || v === '') return null;
    if (typeof v !== 'string' || !/^[A-Za-z0-9_-]{4,64}$/.test(v)) fail(400, 'invalid_match', 'matchId is not valid.');
    return v;
  }

  // ---------- moderation ----------
  function moderator(me) {
    const m = q.one('SELECT role FROM moderators WHERE player_id = ?', me.id);
    if (!m) fail(403, 'forbidden', 'Moderator access required.');
    return m.role;
  }
  const requireAdmin = me => { if (moderator(me) !== 'admin') fail(403, 'forbidden', 'Administrator access required.'); };

  function applyAction({ targetId, moderatorId, reportId, action, publicReason, note, hours }) {
    const expires = action === 'warn' ? null : now() + clamp(hours || 24, 1, 24 * 365) * 3600000;
    const r = q.run('INSERT INTO moderation_actions(target_id, moderator_id, report_id, action, public_reason, internal_note, expires_at, created_at) VALUES (?,?,?,?,?,?,?,?)',
      targetId, moderatorId, reportId ?? null, action, publicReason ?? null, note ?? null, expires, now());
    const id = Number(r.lastInsertRowid);
    const penalty = cfg.actionPenalty[action] || 0;
    if (penalty) {
      addEvent(targetId, 'moderation_' + action, -penalty, id, `action:${id}`);
      q.run('UPDATE player_reputation SET confirmed_actions = confirmed_actions + 1 WHERE player_id = ?', targetId);
    }
    return id;
  }

  const reportView = r => ({
    id: r.id, category: r.category, reason: r.reason, evidence: r.evidence, matchId: r.match_id, status: r.status, createdAt: r.created_at,
    reporterPublicId: r.reporter_public_id, targetPublicId: r.target_public_id, targetUsername: r.target_username,
    distinctReportersAgainstTarget: r.distinct_reporters,
  });

  function install(route) {
    route('GET', '/api/fairplay/rules', () => ({ body: rules() }));

    route('GET', '/api/fairplay/me', ({ me }) => {
      const e = eligibility(me.id);
      const rep = ensureRep(me.id);
      const since = now() - cfg.abandonWindowDays * DAY;
      const abandons = q.one("SELECT COUNT(*) c FROM reputation_events WHERE player_id = ? AND kind = 'abandon_seen' AND created_at > ?", me.id, since).c;
      const actions = q.all("SELECT * FROM moderation_actions WHERE target_id = ? AND action IN ('warn','mute','suspend') ORDER BY id DESC LIMIT 20", me.id).map(a => {
        const ap = q.one('SELECT status FROM appeals WHERE action_id = ?', a.id);
        return {
          id: a.id, action: a.action, reason: a.public_reason, createdAt: a.created_at, expiresAt: a.expires_at, revoked: a.revoked_at != null,
          appealStatus: ap ? ap.status : null,
          canAppeal: !ap && a.revoked_at == null && now() - a.created_at < cfg.appealWindowDays * DAY,
        };
      });
      const myReports = q.all(`SELECT r.id, r.category, r.status, r.created_at, p.username target_username FROM reports r JOIN players p ON p.id = r.target_id
        WHERE r.reporter_id = ? ORDER BY r.id DESC LIMIT 20`, me.id).map(r => ({ id: r.id, category: r.category, status: r.status === 'open' ? 'open' : 'reviewed', targetUsername: r.target_username, createdAt: r.created_at }));
      return { body: { score: e.score, level: e.level, competitiveAllowed: e.competitiveAllowed, chatAllowed: e.chatAllowed, reasons: e.reasons, abandonsInWindow: abandons, confirmedActions: rep.confirmed_actions, cleanMatches: rep.clean_matches, actions, myReports } };
    });

    route('POST', '/api/fairplay/appeals', ({ me, body }) => {
      rateLimit('appeal', me.id);
      const actionId = Number.isInteger(body.actionId) ? body.actionId : -1;
      const message = requireString(body.message, 'message', 10, 500);
      return tx(() => {
        const a = q.one('SELECT * FROM moderation_actions WHERE id = ? AND target_id = ?', actionId, me.id);
        if (!a || !['warn', 'mute', 'suspend'].includes(a.action)) fail(404, 'action_not_found', 'No such moderation action on your account.');
        if (a.revoked_at != null) fail(409, 'already_revoked', 'That action was already lifted.');
        if (now() - a.created_at > cfg.appealWindowDays * DAY) fail(409, 'appeal_window_closed', 'The appeal window for this action has closed.');
        if (q.one('SELECT 1 x FROM appeals WHERE action_id = ?', a.id)) fail(409, 'already_appealed', 'You already appealed this action.');
        const r = q.run('INSERT INTO appeals(action_id, player_id, message, created_at) VALUES (?,?,?,?)', a.id, me.id, message, now());
        audit(me.id, 'player', 'appeal.create', a.id, '');
        return { status: 201, body: { appealId: Number(r.lastInsertRowid), status: 'open' } };
      });
    });

    // --- trusted game server ---
    route('POST', '/api/internal/fairplay/abandon', ({ req, ip, body }) => {
      ctx.requireServerKey(req, ip);
      const p = playerByPublicId(body.publicId);
      if (!p) fail(404, 'player_not_found', 'Unknown player.');
      const matchId = requireString(body.matchId, 'matchId', 4, 64);
      return { body: recordAbandon(p.id, matchId) };
    }, { auth: false });

    route('POST', '/api/internal/fairplay/clean-match', ({ req, ip, body }) => {
      ctx.requireServerKey(req, ip);
      const p = playerByPublicId(body.publicId);
      if (!p) fail(404, 'player_not_found', 'Unknown player.');
      const matchId = requireString(body.matchId, 'matchId', 4, 64);
      return { body: recordCleanMatch(p.id, matchId) };
    }, { auth: false });

    route('GET', '/api/internal/players/:publicId/eligibility', ({ req, ip, params }) => {
      ctx.requireServerKey(req, ip);
      const p = playerByPublicId(params.publicId);
      if (!p) fail(404, 'player_not_found', 'Unknown player.');
      return { body: eligibility(p.id) };
    }, { auth: false });

    route('POST', '/api/internal/maintenance/purge', ({ req, ip }) => {
      ctx.requireServerKey(req, ip);
      return { body: purge() };
    }, { auth: false });

    // --- moderator tools ---
    route('GET', '/api/admin/reports', ({ me, query }) => {
      moderator(me); rateLimit('admin', me.id);
      const status = query.get('status') || 'open';
      if (!['open', 'dismissed', 'confirmed', 'expired'].includes(status)) fail(400, 'invalid_status', 'Unknown status.');
      const rows = q.all(`SELECT r.*, rp.public_id reporter_public_id, tp.public_id target_public_id, tp.username target_username,
          (SELECT COUNT(DISTINCT x.reporter_id) FROM reports x WHERE x.target_id = r.target_id AND x.status = 'open') distinct_reporters
        FROM reports r JOIN players rp ON rp.id = r.reporter_id JOIN players tp ON tp.id = r.target_id
        WHERE r.status = ? ORDER BY r.id ASC LIMIT 100`, status);
      return { body: { reports: rows.map(reportView) } };
    });

    route('POST', '/api/admin/reports/:id/resolve', ({ me, params, body }) => {
      const role = moderator(me); rateLimit('admin', me.id);
      const outcome = String(body.outcome || '');
      const action = String(body.action || 'none');
      if (!['dismiss', 'confirm'].includes(outcome)) fail(400, 'invalid_outcome', "outcome must be 'dismiss' or 'confirm'.");
      if (!['none', 'warn', 'mute', 'suspend'].includes(action)) fail(400, 'invalid_action', 'Unknown action.');
      if (outcome === 'dismiss' && action !== 'none') fail(400, 'invalid_action', 'A dismissed report cannot carry an action.');
      if (action === 'suspend' && role !== 'admin') fail(403, 'forbidden', 'Only administrators can suspend players.');
      const note = body.note === undefined ? null : requireString(body.note, 'note', 1, 500);
      const publicReason = action === 'none' ? null : requireString(body.publicReason, 'publicReason', 5, 200);
      return tx(() => {
        const r = q.one('SELECT * FROM reports WHERE id = ?', Number.parseInt(params.id, 10) || -1);
        if (!r) fail(404, 'report_not_found', 'Unknown report.');
        if (r.status !== 'open') fail(409, 'already_resolved', 'That report was already reviewed.');
        if (r.target_id === me.id || r.reporter_id === me.id) fail(403, 'conflict_of_interest', 'You cannot review a report you filed or one about yourself.');
        q.run('UPDATE reports SET status = ?, reviewed_by = ?, resolved_at = ? WHERE id = ?', outcome === 'dismiss' ? 'dismissed' : 'confirmed', me.id, now(), r.id);
        let actionId = null;
        if (outcome === 'confirm' && action !== 'none') {
          actionId = applyAction({ targetId: r.target_id, moderatorId: me.id, reportId: r.id, action, publicReason, note, hours: Number(body.durationHours) });
          notify(r.target_id, 'moderation', 'Account notice', publicReason);
        } else {
          q.run('INSERT INTO moderation_actions(target_id, moderator_id, report_id, action, internal_note, created_at) VALUES (?,?,?,?,?,?)', r.target_id, me.id, r.id, 'dismiss', note, now());
        }
        if (outcome === 'dismiss' || action === 'none') {
          // Review hold from earlier report counts is released once nothing else is open against the player.
          if (!q.one("SELECT 1 x FROM reports WHERE target_id = ? AND status = 'open'", r.target_id)) q.run('UPDATE cheat_strikes SET flagged = 0 WHERE player_id = ?', r.target_id);
        }
        audit(me.id, 'moderator', 'report.' + outcome, r.id, `action=${action}`);
        return { body: { reportId: r.id, status: outcome === 'dismiss' ? 'dismissed' : 'confirmed', actionId } };
      });
    });

    route('GET', '/api/admin/players/:publicId', ({ me, params }) => {
      moderator(me); rateLimit('admin', me.id);
      const p = playerByPublicId(params.publicId);
      if (!p) fail(404, 'player_not_found', 'Unknown player.');
      const e = eligibility(p.id);
      return { body: {
        publicId: p.public_id, username: p.username, eligibility: e,
        actions: q.all('SELECT * FROM moderation_actions WHERE target_id = ? ORDER BY id DESC LIMIT 50', p.id),
        reportsAgainst: q.one('SELECT COUNT(*) c FROM reports WHERE target_id = ?', p.id).c,
        reputationEvents: q.all('SELECT kind, delta, ref_id, created_at FROM reputation_events WHERE player_id = ? ORDER BY id DESC LIMIT 50', p.id),
      } };
    });

    route('POST', '/api/admin/players/:publicId/notes', ({ me, params, body }) => {
      moderator(me); rateLimit('admin', me.id);
      const p = playerByPublicId(params.publicId);
      if (!p) fail(404, 'player_not_found', 'Unknown player.');
      const note = requireString(body.note, 'note', 1, 500);
      const r = q.run("INSERT INTO moderation_actions(target_id, moderator_id, action, internal_note, created_at) VALUES (?,?,'note',?,?)", p.id, me.id, note, now());
      audit(me.id, 'moderator', 'note.add', p.public_id, '');
      return { status: 201, body: { noteId: Number(r.lastInsertRowid) } };
    });

    route('GET', '/api/admin/appeals', ({ me, query }) => {
      moderator(me); rateLimit('admin', me.id);
      const status = query.get('status') || 'open';
      if (!['open', 'upheld', 'overturned'].includes(status)) fail(400, 'invalid_status', 'Unknown status.');
      return { body: { appeals: q.all(`SELECT a.id, a.status, a.message, a.created_at createdAt, a.action_id actionId, m.action, m.public_reason publicReason, p.public_id playerPublicId
        FROM appeals a JOIN moderation_actions m ON m.id = a.action_id JOIN players p ON p.id = a.player_id WHERE a.status = ? ORDER BY a.id LIMIT 100`, status) } };
    });

    route('POST', '/api/admin/appeals/:id/resolve', ({ me, params, body }) => {
      moderator(me); rateLimit('admin', me.id);
      const decision = String(body.decision || '');
      if (!['uphold', 'overturn'].includes(decision)) fail(400, 'invalid_decision', "decision must be 'uphold' or 'overturn'.");
      const note = requireString(body.note, 'note', 3, 500);
      return tx(() => {
        const a = q.one('SELECT * FROM appeals WHERE id = ?', Number.parseInt(params.id, 10) || -1);
        if (!a) fail(404, 'appeal_not_found', 'Unknown appeal.');
        if (a.status !== 'open') fail(409, 'already_resolved', 'That appeal was already decided.');
        const act = q.one('SELECT * FROM moderation_actions WHERE id = ?', a.action_id);
        if (act.moderator_id === me.id) fail(403, 'conflict_of_interest', 'A different moderator must review this appeal.');
        q.run('UPDATE appeals SET status = ?, resolved_by = ?, resolution_note = ?, resolved_at = ? WHERE id = ?', decision === 'uphold' ? 'upheld' : 'overturned', me.id, note, now(), a.id);
        if (decision === 'overturn') {
          q.run('UPDATE moderation_actions SET revoked_at = ? WHERE id = ?', now(), act.id);
          const penalty = cfg.actionPenalty[act.action] || 0;
          if (penalty) addEvent(a.player_id, 'appeal_restore', penalty, act.id, `restore:${act.id}`);
          if (act.report_id) q.run("UPDATE reports SET status = 'dismissed' WHERE id = ?", act.report_id);
        }
        notify(a.player_id, 'moderation', 'Appeal ' + (decision === 'overturn' ? 'accepted' : 'reviewed'), decision === 'overturn' ? 'The action on your account was lifted.' : 'The original decision was kept.');
        audit(me.id, 'moderator', 'appeal.' + decision, a.id, '');
        return { body: { appealId: a.id, status: decision === 'uphold' ? 'upheld' : 'overturned' } };
      });
    });

    route('GET', '/api/admin/audit', ({ me, query }) => {
      requireAdmin(me); rateLimit('admin', me.id);
      const limit = clamp(Number.parseInt(query.get('limit') || '50', 10) || 50, 1, 200);
      return { body: { entries: q.all('SELECT * FROM audit_log ORDER BY id DESC LIMIT ?', limit) } };
    });
  }

  // Abandon penalties apply only after `abandonGraceCount` earlier abandons inside the window.
  function recordAbandon(playerId, matchId) {
    return inTx(() => {
      const seen = addEvent(playerId, 'abandon_seen', 0, matchId, `abandon_seen:${matchId}:${playerId}`);
      if (seen.duplicate) return { duplicate: true, penalized: false };
      q.run('UPDATE player_reputation SET abandons = abandons + 1 WHERE player_id = ?', playerId);
      const recent = q.one("SELECT COUNT(*) c FROM reputation_events WHERE player_id = ? AND kind = 'abandon_seen' AND created_at > ?", playerId, now() - cfg.abandonWindowDays * DAY).c;
      const penalized = recent > cfg.abandonGraceCount;
      if (penalized) addEvent(playerId, 'abandon', -cfg.abandonPenalty, matchId, `abandon:${matchId}:${playerId}`);
      audit(null, 'server', 'abandon.recorded', playerId, `match=${matchId} penalized=${penalized}`);
      return { duplicate: false, penalized, abandonsInWindow: recent };
    });
  }

  function recordCleanMatch(playerId, matchId) {
    return inTx(() => {
      const r = addEvent(playerId, 'clean_match', cfg.cleanMatchBonus, matchId, `clean:${matchId}:${playerId}`);
      if (!r.duplicate) q.run('UPDATE player_reputation SET clean_matches = clean_matches + 1 WHERE player_id = ?', playerId);
      return { duplicate: r.duplicate };
    });
  }

  // Retention: evidence disappears first, then closed reports; very old open reports expire.
  function purge() {
    return inTx(() => {
      const evidence = q.run("UPDATE reports SET evidence = NULL WHERE evidence IS NOT NULL AND status <> 'open' AND resolved_at IS NOT NULL AND resolved_at < ?", now() - cfg.evidenceRetentionDays * DAY).changes;
      const expired = q.run("UPDATE reports SET status = 'expired', evidence = NULL, resolved_at = ? WHERE status = 'open' AND created_at < ?", now(), now() - cfg.openReportMaxAgeDays * DAY).changes;
      const closed = q.run("DELETE FROM reports WHERE status <> 'open' AND resolved_at IS NOT NULL AND resolved_at < ?", now() - cfg.closedReportRetentionDays * DAY).changes;
      audit(null, 'system', 'retention.purge', null, `evidence=${evidence} expired=${expired} deleted=${closed}`);
      return { evidenceCleared: Number(evidence), expired: Number(expired), deleted: Number(closed) };
    });
  }
  purge();

  return { install, audit, eligibility, requireCompetitive, requireChat, recordAbandon, recordCleanMatch, checkReporter, cleanEvidence, cleanMatchId, rules, purge, moderator };
}

module.exports = { create, DEFAULTS };
