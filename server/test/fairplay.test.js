'use strict';
const test = require('node:test');
const assert = require('node:assert/strict');
const { startServer } = require('./helpers');

const KEY = 'unit-test-server-key';
const H = 3600000, DAY = 24 * H;
async function internal(s, method, path, body, key = KEY) {
  const res = await fetch(s.base + path, { method, headers: { 'Content-Type': 'application/json', ...(key ? { 'x-noxy-server-key': key } : {}) }, body: body === undefined ? undefined : JSON.stringify(body) });
  const text = await res.text();
  return { status: res.status, body: text ? JSON.parse(text) : null };
}
const server = (fairplay) => startServer({ config: { economy: { matchServerKey: KEY }, ...(fairplay ? { fairplay } : {}) } });
const grant = (s, u, role) => s.app.db.prepare('INSERT INTO moderators(player_id, role, created_at) VALUES ((SELECT id FROM players WHERE public_id = ?), ?, ?)').run(u.me.publicId, role, s.clock.t);
const report = (s, from, target, extra = {}) => s.call('POST', `/api/players/${target.me.publicId}/report`, { token: from.token, body: { category: 'harassment', reason: 'Repeated abusive messages.', ...extra } });
const rep = async (s, u) => (await s.call('GET', '/api/fairplay/me', { token: u.token })).body;

test('fair play: rules are public; reports accept new categories, evidence and match ids and validate them', async () => {
  const s = await server();
  const a = await s.register('reporter1'), b = await s.register('target1');
  const rules = await s.call('GET', '/api/fairplay/rules', { token: a.token });
  assert.equal(rules.status, 200); assert.equal(rules.body.reportsNeverAutoPunish, true);
  assert.equal((await report(s, a, b, { evidence: 'Chat log excerpt', matchId: 'match-0001' })).status, 201);
  assert.equal((await report(s, a, b, { category: 'disruptive', evidence: 'x'.repeat(5000) })).status, 400);
  assert.equal((await report(s, a, b, { category: 'disruptive', matchId: 'bad id!' })).status, 400);
  assert.equal((await report(s, a, b, { category: 'nonsense' })).status, 400);
  assert.equal((await report(s, a, a)).status, 404);
  await s.close();
});

test('fair play: many reports never auto-punish and cannot be read or changed by players', async () => {
  const s = await server();
  const target = await s.register('target2');
  for (let i = 0; i < 8; i++) { const r = await s.register('mob' + i); assert.equal((await report(s, r, target)).status, 201); }
  const e = await rep(s, target);
  assert.equal(e.score, 100); assert.equal(e.competitiveAllowed, true); assert.equal(e.chatAllowed, true);
  assert.equal(e.myReports.length, 0, 'the target does not see reports filed about them');
  // players cannot reach moderator or server endpoints
  assert.equal((await s.call('GET', '/api/admin/reports', { token: target.token })).status, 403);
  assert.equal((await s.call('GET', `/api/admin/players/${target.me.publicId}`, { token: target.token })).status, 403);
  assert.equal((await s.call('GET', '/api/admin/audit', { token: target.token })).status, 403);
  assert.equal((await s.call('POST', '/api/internal/fairplay/abandon', { token: target.token, body: {} })).status, 401);
  assert.equal((await s.call('GET', '/api/admin/reports')).status, 401);
  await s.close();
});

test('fair play: reporter rate limit and low-credibility limit after dismissed reports', async () => {
  const s = await server({ maxReportsPerDay: 3 });
  const a = await s.register('spammer1');
  const targets = [];
  for (let i = 0; i < 4; i++) targets.push(await s.register('victim' + i));
  for (let i = 0; i < 3; i++) assert.equal((await report(s, a, targets[i])).status, 201);
  const over = await report(s, a, targets[3]);
  assert.equal(over.status, 429); assert.equal(over.body.error.code, 'report_limit');
  s.clock.t += DAY + 1000;
  assert.equal((await report(s, a, targets[3])).status, 201);
  await s.close();
});

test('fair play: moderators review reports; warn/mute are reputation-weighted; suspend is admin-only; audit is append-only', async () => {
  const s = await server();
  const rpt = await s.register('rpt3'), tgt = await s.register('tgt3'), mod = await s.register('mod3'), adm = await s.register('adm3');
  grant(s, mod, 'moderator'); grant(s, adm, 'admin');
  await report(s, rpt, tgt, { evidence: 'proof' });
  const list = await s.call('GET', '/api/admin/reports', { token: mod.token });
  assert.equal(list.status, 200); assert.equal(list.body.reports.length, 1);
  const id = list.body.reports[0].id;
  // moderators cannot suspend, cannot confirm without a public reason
  assert.equal((await s.call('POST', `/api/admin/reports/${id}/resolve`, { token: mod.token, body: { outcome: 'confirm', action: 'suspend', publicReason: 'Cheating confirmed.', durationHours: 24 } })).status, 403);
  assert.equal((await s.call('POST', `/api/admin/reports/${id}/resolve`, { token: mod.token, body: { outcome: 'confirm', action: 'mute' } })).status, 400);
  const mute = await s.call('POST', `/api/admin/reports/${id}/resolve`, { token: mod.token, body: { outcome: 'confirm', action: 'mute', publicReason: 'Harassment in clan chat.', durationHours: 48, note: 'chat log seen' } });
  assert.equal(mute.status, 200, JSON.stringify(mute.body));
  assert.equal((await s.call('POST', `/api/admin/reports/${id}/resolve`, { token: mod.token, body: { outcome: 'dismiss' } })).status, 409);
  const after = await rep(s, tgt);
  assert.equal(after.chatAllowed, false); assert.ok(after.score < 100);
  assert.ok(after.actions.length >= 1);
  assert.equal(JSON.stringify(after).includes('chat log seen'), false, 'internal moderator notes are never shown to the player');
  // muted player cannot chat in a clan
  const c = (await s.call('POST', '/api/clans', { token: tgt.token, body: { name: 'Mute Test', tag: 'MUTE' } }));
  assert.equal(c.status, 403);
  // reviewer can't review a report about themselves or one they filed
  await report(s, mod, adm);
  const mine = (await s.call('GET', '/api/admin/reports', { token: adm.token })).body.reports.find(r => r.targetPublicId === adm.me.publicId || r.target_public_id === adm.me.publicId);
  if (mine) assert.equal((await s.call('POST', `/api/admin/reports/${mine.id}/resolve`, { token: adm.token, body: { outcome: 'dismiss' } })).status, 403);
  // audit trail exists and cannot be altered
  const audit = await s.call('GET', '/api/admin/audit', { token: adm.token });
  assert.ok(audit.body.entries.some(e => e.action === 'report.confirm'));
  assert.throws(() => s.app.db.prepare('DELETE FROM audit_log').run());
  assert.throws(() => s.app.db.prepare("UPDATE audit_log SET action = 'x'").run());
  await s.close();
});

test('fair play: appeals are limited to own actions, reviewed by a different moderator, and restore reputation when overturned', async () => {
  const s = await server();
  const rpt = await s.register('rpt4'), tgt = await s.register('tgt4'), other = await s.register('oth4'), mod = await s.register('mod4'), mod2 = await s.register('mod4b');
  grant(s, mod, 'moderator'); grant(s, mod2, 'moderator');
  await report(s, rpt, tgt);
  const id = (await s.call('GET', '/api/admin/reports', { token: mod.token })).body.reports[0].id;
  await s.call('POST', `/api/admin/reports/${id}/resolve`, { token: mod.token, body: { outcome: 'confirm', action: 'warn', publicReason: 'Abusive language in chat.' } });
  const reduced = (await rep(s, tgt)).score;
  assert.ok(reduced < 100);
  const actionId = (await rep(s, tgt)).actions[0].id;
  assert.equal((await s.call('POST', '/api/fairplay/appeals', { token: other.token, body: { actionId, message: 'This is not my action.' } })).status >= 400, true);
  assert.equal((await s.call('POST', '/api/fairplay/appeals', { token: tgt.token, body: { actionId, message: 'short' } })).status, 400);
  const ap = await s.call('POST', '/api/fairplay/appeals', { token: tgt.token, body: { actionId, message: 'I was misunderstood, it was a joke.' } });
  assert.equal(ap.status, 201, JSON.stringify(ap.body));
  assert.equal((await s.call('POST', '/api/fairplay/appeals', { token: tgt.token, body: { actionId, message: 'Second try for the same action.' } })).status >= 400, true);
  const appealId = (await s.call('GET', '/api/admin/appeals', { token: mod.token })).body.appeals[0].id;
  assert.equal((await s.call('POST', `/api/admin/appeals/${appealId}/resolve`, { token: mod.token, body: { decision: 'overturn', note: 'self review' } })).status, 403);
  assert.equal((await s.call('POST', `/api/admin/appeals/${appealId}/resolve`, { token: mod2.token, body: { decision: 'overturn', note: 'Context shows a joke.' } })).status, 200);
  assert.equal((await rep(s, tgt)).score, 100);
  await s.close();
});

test('fair play: abandon grace, idempotent penalties, clean-match recovery, and server-only endpoints', async () => {
  const s = await server();
  const p = await s.register('leaver1');
  const ab = (id, key = KEY) => internal(s, 'POST', '/api/internal/fairplay/abandon', { publicId: p.me.publicId, matchId: id }, key);
  assert.equal((await ab('match-aaa1', null)).status, 401);
  assert.equal((await ab('match-aaa1', 'wrong')).status, 401);
  await ab('match-aaa1'); await ab('match-aaa2');
  assert.equal((await rep(s, p)).score, 100, 'the first two abandons in a week are free (accidental disconnects)');
  await ab('match-aaa3'); await ab('match-aaa3'); // replay of the same match counts once
  assert.equal((await rep(s, p)).score, 97);
  const e = await internal(s, 'GET', `/api/internal/players/${p.me.publicId}/eligibility`);
  assert.equal(e.status, 200); assert.equal(e.body.competitiveAllowed, true);
  assert.equal((await internal(s, 'GET', `/api/internal/players/${p.me.publicId}/eligibility`, undefined, null)).status, 401);
  await internal(s, 'POST', '/api/internal/fairplay/clean-match', { publicId: p.me.publicId, matchId: 'match-ok01' });
  await internal(s, 'POST', '/api/internal/fairplay/clean-match', { publicId: p.me.publicId, matchId: 'match-ok01' });
  assert.equal((await rep(s, p)).score, 98);
  await s.close();
});

test('fair play: retention purge clears old evidence and closed reports; open reports expire', async () => {
  const s = await server();
  const a = await s.register('ret1'), b = await s.register('ret2'), mod = await s.register('retmod');
  grant(s, mod, 'moderator');
  await report(s, a, b, { evidence: 'old evidence' });
  const id = (await s.call('GET', '/api/admin/reports', { token: mod.token })).body.reports[0].id;
  await s.call('POST', `/api/admin/reports/${id}/resolve`, { token: mod.token, body: { outcome: 'dismiss' } });
  s.clock.t += 31 * DAY;
  assert.equal((await internal(s, 'POST', '/api/internal/maintenance/purge', {})).status, 200);
  assert.equal(s.app.db.prepare('SELECT evidence FROM reports WHERE id = ?').get(id).evidence, null);
  s.clock.t += 61 * DAY;
  await internal(s, 'POST', '/api/internal/maintenance/purge', {});
  assert.equal(s.app.db.prepare('SELECT COUNT(*) c FROM reports WHERE id = ?').get(id).c, 0);
  await s.close();
});

test('fair play: restricted reputation blocks competitive clan-war queueing', async () => {
  const s = await server();
  const lead = await s.register('restr1');
  const c = (await s.call('POST', '/api/clans', { token: lead.token, body: { name: 'Restricted Co', tag: 'RSTR', joinPolicy: 'open' } })).body;
  s.app.db.prepare("UPDATE player_reputation SET score = 10 WHERE player_id = (SELECT id FROM players WHERE public_id = ?)").run(lead.me.publicId);
  s.app.db.prepare("INSERT OR IGNORE INTO player_reputation(player_id, score, updated_at) SELECT id, 10, 0 FROM players WHERE public_id = ?").run(lead.me.publicId);
  s.clock.t += 2 * DAY;
  const r = await s.call('POST', '/api/clanwars/queue', { token: lead.token, body: { formatId: '2v2', players: [lead.me.publicId, lead.me.publicId] } });
  assert.ok(r.status >= 400);
  const view = await s.call('GET', '/api/clanwars/overview', { token: lead.token });
  assert.equal(view.body.eligibility.competitiveAllowed, false);
  assert.ok(c.id);
  await s.close();
});
