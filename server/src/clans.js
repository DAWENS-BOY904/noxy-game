'use strict';
// Persistent clans: membership, roles, invitations, applications, chat, reports and moderation.
// Every privileged action is re-validated here from the caller's stored role; the client never says what it is allowed to do.
// Clan XP/stats are mutated only by this module and by clanwars.js after a dedicated-server result.

const DAY = 86400000;
const ROLES = ['member', 'officer', 'co_leader', 'leader'];
const rank = r => ROLES.indexOf(r) + 1;

const DEFAULTS = {
  nameMin: 3, nameMax: 20, tagMin: 2, tagMax: 5,
  emblemCount: 12, emblemColors: 8,
  baseMemberLimit: 20, memberLimitPerLevel: 2, maxMemberLimit: 50, minMemberLimit: 5,
  xpPerLevel: 1000, maxLevel: 20,
  createCostCoins: 0, // configurable; 0 = free
  inviteTtlMs: 3 * DAY,
  maxPendingInvites: 30, maxPendingApplications: 5,
  chatMax: 200, chatHistory: 50,
  bannedWords: ['admin', 'moderator', 'noxy staff', 'fuck', 'shit', 'nazi'],
};

const SCHEMA = `
CREATE TABLE IF NOT EXISTS clans (
  id           INTEGER PRIMARY KEY AUTOINCREMENT,
  name         TEXT NOT NULL,
  name_key     TEXT NOT NULL UNIQUE,
  tag          TEXT NOT NULL UNIQUE COLLATE NOCASE,
  emblem_id    INTEGER NOT NULL DEFAULT 1,
  emblem_color INTEGER NOT NULL DEFAULT 0,
  description  TEXT NOT NULL DEFAULT '',
  join_policy  TEXT NOT NULL DEFAULT 'request' CHECK (join_policy IN ('open','request','invite')),
  member_limit INTEGER NOT NULL DEFAULT 20 CHECK (member_limit BETWEEN 1 AND 100),
  xp           INTEGER NOT NULL DEFAULT 0 CHECK (xp >= 0),
  wins         INTEGER NOT NULL DEFAULT 0,
  losses       INTEGER NOT NULL DEFAULT 0,
  draws        INTEGER NOT NULL DEFAULT 0,
  rating       INTEGER NOT NULL DEFAULT 1000,
  created_by   INTEGER,
  created_at   INTEGER NOT NULL
);
CREATE TABLE IF NOT EXISTS clan_members (
  clan_id         INTEGER NOT NULL REFERENCES clans(id) ON DELETE CASCADE,
  player_id       INTEGER NOT NULL UNIQUE REFERENCES players(id) ON DELETE CASCADE,
  role            TEXT NOT NULL CHECK (role IN ('leader','co_leader','officer','member')),
  joined_at       INTEGER NOT NULL,
  contribution_xp INTEGER NOT NULL DEFAULT 0,
  PRIMARY KEY (clan_id, player_id)
);
CREATE UNIQUE INDEX IF NOT EXISTS uq_clan_one_leader ON clan_members(clan_id) WHERE role = 'leader';
CREATE TABLE IF NOT EXISTS clan_invitations (
  id         INTEGER PRIMARY KEY AUTOINCREMENT,
  clan_id    INTEGER NOT NULL REFERENCES clans(id) ON DELETE CASCADE,
  from_id    INTEGER NOT NULL REFERENCES players(id) ON DELETE CASCADE,
  to_id      INTEGER NOT NULL REFERENCES players(id) ON DELETE CASCADE,
  status     TEXT NOT NULL DEFAULT 'pending' CHECK (status IN ('pending','accepted','rejected','cancelled','expired')),
  created_at INTEGER NOT NULL,
  expires_at INTEGER NOT NULL
);
CREATE UNIQUE INDEX IF NOT EXISTS uq_clan_invite_pending ON clan_invitations(clan_id, to_id) WHERE status = 'pending';
CREATE INDEX IF NOT EXISTS idx_clan_invite_to ON clan_invitations(to_id, status);
CREATE TABLE IF NOT EXISTS clan_applications (
  id         INTEGER PRIMARY KEY AUTOINCREMENT,
  clan_id    INTEGER NOT NULL REFERENCES clans(id) ON DELETE CASCADE,
  player_id  INTEGER NOT NULL REFERENCES players(id) ON DELETE CASCADE,
  message    TEXT NOT NULL DEFAULT '',
  status     TEXT NOT NULL DEFAULT 'pending' CHECK (status IN ('pending','accepted','rejected','cancelled')),
  decided_by INTEGER,
  created_at INTEGER NOT NULL
);
CREATE UNIQUE INDEX IF NOT EXISTS uq_clan_app_pending ON clan_applications(clan_id, player_id) WHERE status = 'pending';
CREATE TABLE IF NOT EXISTS clan_messages (
  id         INTEGER PRIMARY KEY AUTOINCREMENT,
  clan_id    INTEGER NOT NULL REFERENCES clans(id) ON DELETE CASCADE,
  player_id  INTEGER NOT NULL REFERENCES players(id) ON DELETE CASCADE,
  body       TEXT NOT NULL,
  created_at INTEGER NOT NULL
);
CREATE INDEX IF NOT EXISTS idx_clan_messages ON clan_messages(clan_id, id);
CREATE TABLE IF NOT EXISTS clan_reports (
  id           INTEGER PRIMARY KEY AUTOINCREMENT,
  reporter_id  INTEGER NOT NULL REFERENCES players(id) ON DELETE CASCADE,
  clan_id      INTEGER NOT NULL REFERENCES clans(id) ON DELETE CASCADE,
  message_id   INTEGER,
  category     TEXT NOT NULL CHECK (category IN ('offensive_name','offensive_description','chat_abuse','harassment','other')),
  reason       TEXT NOT NULL,
  status       TEXT NOT NULL DEFAULT 'open' CHECK (status IN ('open','dismissed','actioned')),
  reviewed_by  INTEGER,
  created_at   INTEGER NOT NULL,
  resolved_at  INTEGER
);
CREATE INDEX IF NOT EXISTS idx_clan_reports_status ON clan_reports(status, id);
`;

function create(ctx) {
  const { db, q, tx, fail, now, config, requireString, rateLimit, playerByPublicId, notify, fairplay, economy } = ctx;
  const cfg = { ...DEFAULTS, ...(config.clans || {}) };
  db.exec(SCHEMA);
  const inTx = fn => (db.isTransaction ? fn() : tx(fn));

  const levelOf = xp => Math.min(cfg.maxLevel, 1 + Math.floor(xp / cfg.xpPerLevel));
  const limitCap = level => Math.min(cfg.maxMemberLimit, cfg.baseMemberLimit + (level - 1) * cfg.memberLimitPerLevel);
  const clanById = id => q.one('SELECT * FROM clans WHERE id = ?', id);
  const memberOf = playerId => q.one('SELECT * FROM clan_members WHERE player_id = ?', playerId);
  const memberCount = clanId => q.one('SELECT COUNT(*) c FROM clan_members WHERE clan_id = ?', clanId).c;
  const memberIds = clanId => q.all('SELECT player_id FROM clan_members WHERE clan_id = ?', clanId).map(r => r.player_id);

  function normalizeName(v) {
    const name = requireString(typeof v === 'string' ? v.trim().replace(/\s+/g, ' ') : v, 'name', cfg.nameMin, cfg.nameMax);
    if (!/^[A-Za-z0-9 _.-]+$/.test(name)) fail(400, 'invalid_name', 'Clan name may use letters, numbers, spaces, dots, dashes and underscores.');
    return name;
  }
  const nameKey = name => name.toLowerCase().replace(/[\s._-]+/g, '');
  function normalizeTag(v) {
    const tag = requireString(typeof v === 'string' ? v.trim().toUpperCase() : v, 'tag', cfg.tagMin, cfg.tagMax);
    if (!/^[A-Z0-9]+$/.test(tag)) fail(400, 'invalid_tag', 'Clan tag may only use letters and numbers.');
    return tag;
  }
  function checkClean(text, field) {
    const t = String(text).toLowerCase().replace(/[^a-z0-9 ]/g, '');
    for (const w of cfg.bannedWords) if (t.replace(/ /g, '').includes(w.replace(/ /g, ''))) fail(400, 'inappropriate_' + field, `That ${field} is not allowed.`);
  }
  function emblem(body, cur) {
    const id = body.emblem === undefined ? cur.id : body.emblem;
    const color = body.emblemColor === undefined ? cur.color : body.emblemColor;
    if (!Number.isInteger(id) || id < 1 || id > cfg.emblemCount) fail(400, 'invalid_emblem', `emblem must be 1-${cfg.emblemCount}.`);
    if (!Number.isInteger(color) || color < 0 || color >= cfg.emblemColors) fail(400, 'invalid_emblem', `emblemColor must be 0-${cfg.emblemColors - 1}.`);
    return { id, color };
  }
  const POLICIES = ['open', 'request', 'invite'];

  function clanSummary(c) {
    return {
      id: c.id, name: c.name, tag: c.tag, emblem: c.emblem_id, emblemColor: c.emblem_color, description: c.description, joinPolicy: c.join_policy,
      level: levelOf(c.xp), xp: c.xp, xpIntoLevel: c.xp % cfg.xpPerLevel, xpPerLevel: cfg.xpPerLevel,
      memberCount: memberCount(c.id), memberLimit: c.member_limit, wins: c.wins, losses: c.losses, draws: c.draws, rating: c.rating,
    };
  }

  function requireMember(playerId, clanId) {
    const m = memberOf(playerId);
    if (!m || m.clan_id !== clanId) fail(403, 'not_a_member', 'You are not a member of that clan.');
    return m;
  }
  function requireRole(playerId, clanId, minRole) {
    const m = requireMember(playerId, clanId);
    if (rank(m.role) < rank(minRole)) fail(403, 'insufficient_role', 'Your clan role does not allow that action.');
    return m;
  }
  function clanFromParam(params) {
    const c = clanById(Number.parseInt(params.id, 10) || -1);
    if (!c) fail(404, 'clan_not_found', 'Clan not found.');
    return c;
  }
  const memberView = m => ({ publicId: m.public_id, username: m.username, role: m.role, joinedAt: m.joined_at, contributionXp: m.contribution_xp });

  function activity(clanId) {
    const since = now() - 7 * DAY;
    const messages = q.one('SELECT COUNT(*) c FROM clan_messages WHERE clan_id = ? AND created_at > ?', clanId, since).c;
    const hasWars = !!q.one("SELECT 1 x FROM sqlite_master WHERE type='table' AND name='clan_matches'");
    const matches = hasWars ? q.one("SELECT COUNT(*) c FROM clan_matches WHERE (clan_a = ? OR clan_b = ?) AND status = 'completed' AND finished_at > ?", clanId, clanId, since).c : 0;
    const activeChatters = q.one('SELECT COUNT(DISTINCT player_id) c FROM clan_messages WHERE clan_id = ? AND created_at > ?', clanId, since).c;
    return { messages7d: messages, matches7d: matches, activeMembers7d: activeChatters };
  }

  function fullView(c, viewer) {
    const view = clanSummary(c);
    const mine = viewer ? memberOf(viewer) : null;
    const isMember = !!mine && mine.clan_id === c.id;
    view.isMember = isMember;
    view.myRole = isMember ? mine.role : null;
    view.activity = activity(c.id);
    if (isMember) {
      view.members = q.all(`SELECT m.*, p.public_id, p.username FROM clan_members m JOIN players p ON p.id = m.player_id WHERE m.clan_id = ?
        ORDER BY CASE m.role WHEN 'leader' THEN 0 WHEN 'co_leader' THEN 1 WHEN 'officer' THEN 2 ELSE 3 END, m.joined_at`, c.id).map(memberView);
      if (rank(mine.role) >= rank('officer')) {
        view.applications = q.all(`SELECT a.id, a.message, a.created_at, p.public_id, p.username, p.level FROM clan_applications a JOIN players p ON p.id = a.player_id
          WHERE a.clan_id = ? AND a.status = 'pending' ORDER BY a.id`, c.id).map(a => ({ id: a.id, message: a.message, createdAt: a.created_at, publicId: a.public_id, username: a.username, level: a.level }));
        view.invitations = q.all(`SELECT i.id, i.created_at, p.public_id, p.username FROM clan_invitations i JOIN players p ON p.id = i.to_id
          WHERE i.clan_id = ? AND i.status = 'pending' AND i.expires_at > ?`, c.id, now()).map(i => ({ id: i.id, createdAt: i.created_at, publicId: i.public_id, username: i.username }));
      }
    }
    return view;
  }

  function addMember(clanId, playerId, role) {
    q.run('INSERT INTO clan_members(clan_id, player_id, role, joined_at) VALUES (?,?,?,?)', clanId, playerId, role, now());
    // A player in a clan can't also hold dangling requests elsewhere.
    q.run("UPDATE clan_applications SET status = 'cancelled' WHERE player_id = ? AND status = 'pending'", playerId);
    q.run("UPDATE clan_invitations SET status = 'cancelled' WHERE to_id = ? AND status = 'pending'", playerId);
  }
  function notifyClan(clanId, title, body, exceptId) {
    for (const id of memberIds(clanId)) if (id !== exceptId) notify(id, 'clan', title, body);
  }
  function expireInvites() {
    q.run("UPDATE clan_invitations SET status = 'expired' WHERE status = 'pending' AND expires_at <= ?", now());
  }
  function ensureNotInClan(playerId) {
    if (memberOf(playerId)) fail(409, 'already_in_clan', 'That player is already in a clan.');
  }

  // Exposed to clanwars.js
  function addXp(clanId, xp) {
    const c = clanById(clanId);
    const before = levelOf(c.xp);
    q.run('UPDATE clans SET xp = max(0, xp + ?) WHERE id = ?', xp, clanId);
    const after = levelOf(c.xp + xp);
    if (after > before) {
      notifyClan(clanId, 'Clan level up', `${c.name} reached level ${after}!`);
    }
  }

  function disband(clanId) {
    const c = clanById(clanId);
    q.run('DELETE FROM clans WHERE id = ?', clanId);
    fairplay.audit(null, 'system', 'clan.disband', clanId, c ? c.name : '');
  }

  function install(route) {
    route('GET', '/api/clans/me', ({ me }) => {
      expireInvites();
      const m = memberOf(me.id);
      const invitations = q.all(`SELECT i.id, i.created_at, c.id clan_id, c.name, c.tag, p.username inviter FROM clan_invitations i JOIN clans c ON c.id = i.clan_id JOIN players p ON p.id = i.from_id
        WHERE i.to_id = ? AND i.status = 'pending' ORDER BY i.id DESC`, me.id).map(i => ({ id: i.id, clanId: i.clan_id, clanName: i.name, clanTag: i.tag, inviter: i.inviter, createdAt: i.created_at }));
      const applications = q.all(`SELECT a.id, a.created_at, c.id clan_id, c.name, c.tag FROM clan_applications a JOIN clans c ON c.id = a.clan_id WHERE a.player_id = ? AND a.status = 'pending'`, me.id)
        .map(a => ({ id: a.id, clanId: a.clan_id, clanName: a.name, clanTag: a.tag, createdAt: a.created_at }));
      return { body: { clan: m ? fullView(clanById(m.clan_id), me.id) : null, invitations, applications, createCostCoins: cfg.createCostCoins } };
    });

    route('GET', '/api/clans/search', ({ me, query }) => {
      rateLimit('search', me.id);
      const term = (query.get('q') || '').trim().slice(0, 30);
      const esc = term.replace(/[\\%_]/g, m => '\\' + m);
      const rows = term
        ? q.all("SELECT * FROM clans WHERE name LIKE ? ESCAPE '\\' OR tag LIKE ? ESCAPE '\\' ORDER BY xp DESC LIMIT 20", esc + '%', esc + '%')
        : q.all('SELECT * FROM clans ORDER BY xp DESC LIMIT 20');
      return { body: { clans: rows.map(clanSummary) } };
    });

    route('GET', '/api/clans/leaderboard', ({ query }) => {
      const by = query.get('by') || 'xp';
      const col = { xp: 'xp', wins: 'wins', rating: 'rating' }[by];
      if (!col) fail(400, 'invalid_sort', 'by must be xp, wins or rating.');
      return { body: { by, clans: q.all(`SELECT * FROM clans ORDER BY ${col} DESC, id ASC LIMIT 50`).map((c, i) => ({ rank: i + 1, ...clanSummary(c) })) } };
    });

    route('POST', '/api/clans', ({ me, body }) => {
      rateLimit('clan', me.id);
      fairplay.requireChat(me.id);
      const name = normalizeName(body.name); const tag = normalizeTag(body.tag);
      checkClean(name, 'name'); checkClean(tag, 'tag');
      const em = emblem(body, { id: 1, color: 0 });
      const description = body.description === undefined ? '' : requireString(body.description, 'description', 0, 200);
      checkClean(description, 'description');
      const policy = body.joinPolicy === undefined ? 'request' : body.joinPolicy;
      if (!POLICIES.includes(policy)) fail(400, 'invalid_policy', 'joinPolicy must be open, request or invite.');
      return tx(() => {
        ensureNotInClan(me.id);
        if (q.one('SELECT 1 x FROM clans WHERE name_key = ?', nameKey(name))) fail(409, 'name_taken', 'That clan name is already taken.');
        if (q.one('SELECT 1 x FROM clans WHERE tag = ?', tag)) fail(409, 'tag_taken', 'That clan tag is already taken.');
        if (cfg.createCostCoins > 0) {
          try { economy.apply(me.id, 'coins', -cfg.createCostCoins, { kind: 'clan_create', refType: 'clan', refId: nameKey(name), key: `clan_create:${me.id}:${nameKey(name)}` }); }
          catch (e) { fail(402, 'insufficient_funds', `Creating a clan costs ${cfg.createCostCoins} coins.`); }
        }
        const r = q.run('INSERT INTO clans(name, name_key, tag, emblem_id, emblem_color, description, join_policy, member_limit, created_by, created_at) VALUES (?,?,?,?,?,?,?,?,?,?)',
          name, nameKey(name), tag, em.id, em.color, description, policy, cfg.baseMemberLimit, me.id, now());
        const id = Number(r.lastInsertRowid);
        addMember(id, me.id, 'leader');
        fairplay.audit(me.id, 'player', 'clan.create', id, name);
        return { status: 201, body: fullView(clanById(id), me.id) };
      });
    });

    route('GET', '/api/clans/:id', ({ me, params }) => ({ body: fullView(clanFromParam(params), me.id) }));

    route('PATCH', '/api/clans/:id', ({ me, params, body }) => {
      rateLimit('clan', me.id);
      const c = clanFromParam(params);
      requireRole(me.id, c.id, 'co_leader');
      const cur = { id: c.emblem_id, color: c.emblem_color };
      const em = emblem(body, cur);
      let description = c.description;
      if (body.description !== undefined) { description = requireString(body.description, 'description', 0, 200); checkClean(description, 'description'); }
      let policy = c.join_policy;
      if (body.joinPolicy !== undefined) { if (!POLICIES.includes(body.joinPolicy)) fail(400, 'invalid_policy', 'joinPolicy must be open, request or invite.'); policy = body.joinPolicy; }
      let limit = c.member_limit;
      if (body.memberLimit !== undefined) {
        const cap = limitCap(levelOf(c.xp));
        if (!Number.isInteger(body.memberLimit) || body.memberLimit < cfg.minMemberLimit || body.memberLimit > cap) fail(400, 'invalid_member_limit', `memberLimit must be ${cfg.minMemberLimit}-${cap} at clan level ${levelOf(c.xp)}.`);
        if (body.memberLimit < memberCount(c.id)) fail(409, 'limit_below_members', 'The limit cannot be lower than the current member count.');
        limit = body.memberLimit;
      }
      q.run('UPDATE clans SET emblem_id = ?, emblem_color = ?, description = ?, join_policy = ?, member_limit = ? WHERE id = ?', em.id, em.color, description, policy, limit, c.id);
      return { body: fullView(clanById(c.id), me.id) };
    });

    route('DELETE', '/api/clans/:id', ({ me, params }) => {
      rateLimit('clan', me.id);
      const c = clanFromParam(params);
      requireRole(me.id, c.id, 'leader');
      tx(() => { ctx.clanwarsGuard && ctx.clanwarsGuard(c.id); disband(c.id); });
      return { body: { ok: true } };
    });

    // ---- invitations ----
    route('POST', '/api/clans/:id/invitations', ({ me, params, body }) => {
      rateLimit('clan', me.id);
      const c = clanFromParam(params);
      requireRole(me.id, c.id, 'officer');
      const target = playerByPublicId(body.publicId);
      if (!target || target.id === me.id) fail(404, 'player_not_found', 'Player not found.');
      return tx(() => {
        expireInvites();
        ensureNotInClan(target.id);
        if (memberCount(c.id) >= c.member_limit) fail(409, 'clan_full', 'The clan is full.');
        if (q.one('SELECT 1 x FROM player_blocks WHERE blocker_id = ? AND blocked_id = ?', target.id, me.id)) fail(403, 'cannot_invite', 'That player cannot be invited.');
        if (q.one("SELECT 1 x FROM clan_invitations WHERE clan_id = ? AND to_id = ? AND status = 'pending'", c.id, target.id)) fail(409, 'already_invited', 'That player already has a pending invitation.');
        if (q.one("SELECT COUNT(*) c FROM clan_invitations WHERE clan_id = ? AND status = 'pending'", c.id).c >= cfg.maxPendingInvites) fail(429, 'too_many_invites', 'Too many pending invitations.');
        const r = q.run('INSERT INTO clan_invitations(clan_id, from_id, to_id, created_at, expires_at) VALUES (?,?,?,?,?)', c.id, me.id, target.id, now(), now() + cfg.inviteTtlMs);
        notify(target.id, 'clan_invite', 'Clan invitation', `${me.username} invited you to join ${c.name} [${c.tag}].`);
        return { status: 201, body: { invitationId: Number(r.lastInsertRowid) } };
      });
    });

    route('POST', '/api/clans/invitations/:id/accept', ({ me, params }) => {
      rateLimit('clan', me.id);
      return tx(() => {
        expireInvites();
        const inv = q.one("SELECT * FROM clan_invitations WHERE id = ? AND to_id = ? AND status = 'pending'", Number.parseInt(params.id, 10) || -1, me.id);
        if (!inv) fail(404, 'invitation_not_found', 'Invitation not found or expired.');
        ensureNotInClan(me.id);
        const c = clanById(inv.clan_id);
        if (memberCount(c.id) >= c.member_limit) fail(409, 'clan_full', 'The clan is full.');
        q.run("UPDATE clan_invitations SET status = 'accepted' WHERE id = ?", inv.id);
        addMember(c.id, me.id, 'member');
        notifyClan(c.id, 'New member', `${me.username} joined the clan.`, me.id);
        return { body: fullView(clanById(c.id), me.id) };
      });
    });

    const inviteEnd = (status) => ({ me, params }) => {
      rateLimit('clan', me.id);
      const inv = q.one("SELECT * FROM clan_invitations WHERE id = ? AND status = 'pending'", Number.parseInt(params.id, 10) || -1);
      if (!inv) fail(404, 'invitation_not_found', 'Invitation not found.');
      if (status === 'rejected') { if (inv.to_id !== me.id) fail(404, 'invitation_not_found', 'Invitation not found.'); }
      else requireRole(me.id, inv.clan_id, 'officer');
      q.run('UPDATE clan_invitations SET status = ? WHERE id = ?', status, inv.id);
      return { body: { ok: true } };
    };
    route('POST', '/api/clans/invitations/:id/reject', inviteEnd('rejected'));
    route('POST', '/api/clans/invitations/:id/cancel', inviteEnd('cancelled'));

    // ---- applications / joining ----
    route('POST', '/api/clans/:id/applications', ({ me, params, body }) => {
      rateLimit('clan', me.id);
      const c = clanFromParam(params);
      const message = body.message === undefined ? '' : requireString(body.message, 'message', 0, 140);
      return tx(() => {
        ensureNotInClan(me.id);
        if (c.join_policy === 'invite') fail(403, 'invite_only', 'This clan is invite-only.');
        if (memberCount(c.id) >= c.member_limit) fail(409, 'clan_full', 'The clan is full.');
        if (c.join_policy === 'open') {
          addMember(c.id, me.id, 'member');
          notifyClan(c.id, 'New member', `${me.username} joined the clan.`, me.id);
          return { status: 201, body: { joined: true, clan: fullView(clanById(c.id), me.id) } };
        }
        if (q.one("SELECT 1 x FROM clan_applications WHERE clan_id = ? AND player_id = ? AND status = 'pending'", c.id, me.id)) fail(409, 'already_applied', 'You already applied to this clan.');
        if (q.one("SELECT COUNT(*) c FROM clan_applications WHERE player_id = ? AND status = 'pending'", me.id).c >= cfg.maxPendingApplications) fail(429, 'too_many_applications', 'You have too many pending applications.');
        const r = q.run('INSERT INTO clan_applications(clan_id, player_id, message, created_at) VALUES (?,?,?,?)', c.id, me.id, message, now());
        for (const m of q.all("SELECT player_id FROM clan_members WHERE clan_id = ? AND role <> 'member'", c.id)) notify(m.player_id, 'clan', 'New clan application', `${me.username} wants to join.`);
        return { status: 201, body: { joined: false, applicationId: Number(r.lastInsertRowid) } };
      });
    });

    route('POST', '/api/clans/applications/:id/accept', ({ me, params }) => {
      rateLimit('clan', me.id);
      return tx(() => {
        const a = q.one("SELECT * FROM clan_applications WHERE id = ? AND status = 'pending'", Number.parseInt(params.id, 10) || -1);
        if (!a) fail(404, 'application_not_found', 'Application not found.');
        requireRole(me.id, a.clan_id, 'officer');
        const c = clanById(a.clan_id);
        if (memberOf(a.player_id)) { q.run("UPDATE clan_applications SET status = 'cancelled' WHERE id = ?", a.id); fail(409, 'already_in_clan', 'That player already joined a clan.'); }
        if (memberCount(c.id) >= c.member_limit) fail(409, 'clan_full', 'The clan is full.');
        q.run("UPDATE clan_applications SET status = 'accepted', decided_by = ? WHERE id = ?", me.id, a.id);
        addMember(c.id, a.player_id, 'member');
        notify(a.player_id, 'clan', 'Application accepted', `You joined ${c.name} [${c.tag}].`);
        return { body: { ok: true } };
      });
    });
    route('POST', '/api/clans/applications/:id/reject', ({ me, params }) => {
      rateLimit('clan', me.id);
      const a = q.one("SELECT * FROM clan_applications WHERE id = ? AND status = 'pending'", Number.parseInt(params.id, 10) || -1);
      if (!a) fail(404, 'application_not_found', 'Application not found.');
      requireRole(me.id, a.clan_id, 'officer');
      q.run("UPDATE clan_applications SET status = 'rejected', decided_by = ? WHERE id = ?", me.id, a.id);
      return { body: { ok: true } };
    });
    route('POST', '/api/clans/applications/:id/cancel', ({ me, params }) => {
      const r = q.run("UPDATE clan_applications SET status = 'cancelled' WHERE id = ? AND player_id = ? AND status = 'pending'", Number.parseInt(params.id, 10) || -1, me.id);
      if (!r.changes) fail(404, 'application_not_found', 'Application not found.');
      return { body: { ok: true } };
    });

    // ---- roles ----
    function target(params, c) {
      const p = playerByPublicId(params.publicId);
      const m = p && q.one('SELECT * FROM clan_members WHERE clan_id = ? AND player_id = ?', c.id, p.id);
      if (!m) fail(404, 'member_not_found', 'That player is not in this clan.');
      return { p, m };
    }
    route('POST', '/api/clans/:id/members/:publicId/promote', ({ me, params }) => {
      rateLimit('clan', me.id);
      const c = clanFromParam(params);
      return tx(() => {
        const actor = requireRole(me.id, c.id, 'co_leader');
        const { p, m } = target(params, c);
        if (p.id === me.id) fail(400, 'invalid_target', 'You cannot change your own role.');
        const next = ROLES[rank(m.role)];
        if (!next || next === 'leader') fail(409, 'cannot_promote', 'Use leadership transfer to make someone the leader.');
        if (rank(next) >= rank(actor.role)) fail(403, 'insufficient_role', 'You can only promote to a role below your own.');
        q.run('UPDATE clan_members SET role = ? WHERE clan_id = ? AND player_id = ?', next, c.id, p.id);
        notify(p.id, 'clan', 'Promoted', `You are now ${next.replace('_', '-')} of ${c.name}.`);
        fairplay.audit(me.id, 'player', 'clan.promote', c.id, `${p.public_id}->${next}`);
        return { body: { publicId: p.public_id, role: next } };
      });
    });
    route('POST', '/api/clans/:id/members/:publicId/demote', ({ me, params }) => {
      rateLimit('clan', me.id);
      const c = clanFromParam(params);
      return tx(() => {
        const actor = requireRole(me.id, c.id, 'co_leader');
        const { p, m } = target(params, c);
        if (p.id === me.id) fail(400, 'invalid_target', 'You cannot change your own role.');
        if (rank(m.role) >= rank(actor.role)) fail(403, 'insufficient_role', 'You can only demote members below your own role.');
        if (m.role === 'member') fail(409, 'cannot_demote', 'That member already has the lowest role.');
        const next = ROLES[rank(m.role) - 2];
        q.run('UPDATE clan_members SET role = ? WHERE clan_id = ? AND player_id = ?', next, c.id, p.id);
        fairplay.audit(me.id, 'player', 'clan.demote', c.id, `${p.public_id}->${next}`);
        return { body: { publicId: p.public_id, role: next } };
      });
    });
    route('POST', '/api/clans/:id/members/:publicId/kick', ({ me, params }) => {
      rateLimit('clan', me.id);
      const c = clanFromParam(params);
      return tx(() => {
        const actor = requireRole(me.id, c.id, 'officer');
        const { p, m } = target(params, c);
        if (p.id === me.id) fail(400, 'invalid_target', 'Use Leave to leave the clan.');
        if (rank(m.role) >= rank(actor.role)) fail(403, 'insufficient_role', 'You can only remove members with a lower role than yours.');
        if (ctx.clanwarsGuardPlayer) ctx.clanwarsGuardPlayer(c.id, p.id);
        q.run('DELETE FROM clan_members WHERE clan_id = ? AND player_id = ?', c.id, p.id);
        notify(p.id, 'clan', 'Removed from clan', `You were removed from ${c.name}.`);
        fairplay.audit(me.id, 'player', 'clan.kick', c.id, p.public_id);
        return { body: { ok: true } };
      });
    });
    route('POST', '/api/clans/:id/transfer', ({ me, params, body }) => {
      rateLimit('clan', me.id);
      const c = clanFromParam(params);
      return tx(() => {
        requireRole(me.id, c.id, 'leader');
        const { p } = target({ publicId: body.publicId }, c);
        if (p.id === me.id) fail(400, 'invalid_target', 'You already lead this clan.');
        if (ctx.clanwarsGuard) ctx.clanwarsGuard(c.id);
        // The partial unique index allows only one leader, so demote first.
        q.run("UPDATE clan_members SET role = 'co_leader' WHERE clan_id = ? AND player_id = ?", c.id, me.id);
        q.run("UPDATE clan_members SET role = 'leader' WHERE clan_id = ? AND player_id = ?", c.id, p.id);
        notifyClan(c.id, 'New clan leader', `${p.username} is now the clan leader.`);
        fairplay.audit(me.id, 'player', 'clan.transfer', c.id, p.public_id);
        return { body: fullView(clanById(c.id), me.id) };
      });
    });
    route('POST', '/api/clans/:id/leave', ({ me, params }) => {
      rateLimit('clan', me.id);
      const c = clanFromParam(params);
      return tx(() => {
        const m = requireMember(me.id, c.id);
        if (m.role === 'leader') {
          if (memberCount(c.id) > 1) fail(409, 'leader_must_transfer', 'Transfer leadership before leaving.');
          if (ctx.clanwarsGuard) ctx.clanwarsGuard(c.id);
          disband(c.id);
          return { body: { ok: true, disbanded: true } };
        }
        if (ctx.clanwarsGuardPlayer) ctx.clanwarsGuardPlayer(c.id, me.id);
        q.run('DELETE FROM clan_members WHERE clan_id = ? AND player_id = ?', c.id, me.id);
        return { body: { ok: true } };
      });
    });

    // ---- chat ----
    route('GET', '/api/clans/:id/chat', ({ me, params, query }) => {
      const c = clanFromParam(params);
      requireMember(me.id, c.id);
      const after = Number.parseInt(query.get('after') || '0', 10) || 0;
      const rows = q.all(`SELECT m.id, m.body, m.created_at, p.public_id, p.username FROM clan_messages m JOIN players p ON p.id = m.player_id
        WHERE m.clan_id = ? AND m.id > ? ORDER BY m.id DESC LIMIT ?`, c.id, after, cfg.chatHistory).reverse();
      // Messages from players the viewer blocked are hidden for that viewer only.
      const blocked = new Set(q.all('SELECT p.public_id FROM player_blocks b JOIN players p ON p.id = b.blocked_id WHERE b.blocker_id = ?', me.id).map(r => r.public_id));
      return { body: { messages: rows.filter(r => !blocked.has(r.public_id)).map(r => ({ id: r.id, text: r.body, createdAt: r.created_at, publicId: r.public_id, username: r.username })) } };
    });
    route('POST', '/api/clans/:id/chat', ({ me, params, body }) => {
      rateLimit('clanChat', me.id);
      const c = clanFromParam(params);
      requireMember(me.id, c.id);
      fairplay.requireChat(me.id);
      const text = requireString(typeof body.text === 'string' ? body.text.replace(/[\u0000-\u001f\u007f]/g, ' ').trim() : body.text, 'text', 1, cfg.chatMax);
      const r = q.run('INSERT INTO clan_messages(clan_id, player_id, body, created_at) VALUES (?,?,?,?)', c.id, me.id, text, now());
      q.run('DELETE FROM clan_messages WHERE clan_id = ? AND id <= (SELECT id FROM clan_messages WHERE clan_id = ? ORDER BY id DESC LIMIT 1 OFFSET 500)', c.id, c.id);
      return { status: 201, body: { id: Number(r.lastInsertRowid) } };
    });

    // ---- reports & moderation ----
    route('POST', '/api/clans/:id/report', ({ me, params, body }) => {
      rateLimit('report', me.id);
      const c = clanFromParam(params);
      const category = String(body.category || '');
      if (!['offensive_name', 'offensive_description', 'chat_abuse', 'harassment', 'other'].includes(category)) fail(400, 'invalid_category', 'Unknown report category.');
      const reason = requireString(body.reason, 'reason', 3, 300);
      let messageId = null;
      if (body.messageId !== undefined) {
        if (!Number.isInteger(body.messageId) || !q.one('SELECT 1 x FROM clan_messages WHERE id = ? AND clan_id = ?', body.messageId, c.id)) fail(404, 'message_not_found', 'That message was not found in this clan.');
        messageId = body.messageId;
      }
      if (q.one("SELECT 1 x FROM clan_reports WHERE reporter_id = ? AND clan_id = ? AND category = ? AND created_at > ?", me.id, c.id, category, now() - DAY)) return { body: { ok: true, duplicate: true } };
      const dayCount = q.one('SELECT COUNT(*) c FROM clan_reports WHERE reporter_id = ? AND created_at > ?', me.id, now() - DAY).c;
      if (dayCount >= 10) fail(429, 'report_limit', 'You have reached your daily report limit.');
      q.run('INSERT INTO clan_reports(reporter_id, clan_id, message_id, category, reason, created_at) VALUES (?,?,?,?,?,?)', me.id, c.id, messageId, category, reason, now());
      return { status: 201, body: { ok: true } };
    });

    route('GET', '/api/admin/clan-reports', ({ me }) => {
      fairplay.moderator(me); rateLimit('admin', me.id);
      return { body: { reports: q.all(`SELECT r.id, r.category, r.reason, r.status, r.created_at createdAt, r.message_id messageId, c.id clanId, c.name clanName, c.tag clanTag, c.description,
        (SELECT body FROM clan_messages WHERE id = r.message_id) messageText FROM clan_reports r JOIN clans c ON c.id = r.clan_id WHERE r.status = 'open' ORDER BY r.id LIMIT 100`) } };
    });
    route('POST', '/api/admin/clan-reports/:id/resolve', ({ me, params, body }) => {
      const role = fairplay.moderator(me); rateLimit('admin', me.id);
      const action = String(body.action || 'dismiss');
      if (!['dismiss', 'scrub', 'disband'].includes(action)) fail(400, 'invalid_action', "action must be 'dismiss', 'scrub' or 'disband'.");
      if (action === 'disband' && role !== 'admin') fail(403, 'forbidden', 'Only administrators can disband clans.');
      return tx(() => {
        const r = q.one("SELECT * FROM clan_reports WHERE id = ? AND status = 'open'", Number.parseInt(params.id, 10) || -1);
        if (!r) fail(404, 'report_not_found', 'Open report not found.');
        if (action === 'scrub') {
          const nm = 'Renamed Clan ' + r.clan_id;
          q.run("UPDATE clans SET name = ?, name_key = ?, tag = ?, description = '', emblem_id = 1, emblem_color = 0 WHERE id = ?", nm, nameKey(nm), 'C' + r.clan_id, r.clan_id);
          notifyClan(r.clan_id, 'Clan moderated', 'Your clan name, tag and description were reset by a moderator.');
        } else if (action === 'disband') disband(r.clan_id);
        q.run('UPDATE clan_reports SET status = ?, reviewed_by = ?, resolved_at = ? WHERE id = ?', action === 'dismiss' ? 'dismissed' : 'actioned', me.id, now(), r.id);
        fairplay.audit(me.id, 'moderator', 'clan_report.' + action, r.clan_id, '');
        return { body: { ok: true } };
      });
    });
  }

  return { install, addXp, clanById, memberOf, memberIds, clanSummary, levelOf, notifyClan, disband, rank, cfg };
}

module.exports = { create, DEFAULTS, ROLES };
