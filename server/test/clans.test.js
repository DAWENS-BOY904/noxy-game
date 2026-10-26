'use strict';
const test = require('node:test');
const assert = require('node:assert/strict');
const { startServer } = require('./helpers');

async function setup(n = 4, config) {
  const s = await startServer({ config });
  const users = [];
  for (let i = 0; i < n; i++) users.push(await s.register('clanuser' + i));
  return { s, users };
}
const mk = (s, u, name = 'Iron Wolves', tag = 'IRON', extra = {}) => s.call('POST', '/api/clans', { token: u.token, body: { name, tag, ...extra } });

test('clans: unique name/tag (case-insensitive), one clan per player, creator is leader', async () => {
  const { s, users: [a, b, c] } = await setup(3);
  const r = await mk(s, a);
  assert.equal(r.status, 201);
  assert.equal(r.body.myRole, 'leader');
  assert.equal((await mk(s, a, 'Other Name', 'OTHR')).status, 409);
  const dupName = await mk(s, b, 'iron wolves', 'ZZZ');
  assert.equal(dupName.status, 409); assert.equal(dupName.body.error.code, 'name_taken');
  const dupTag = await mk(s, b, 'Brand New', 'iron');
  assert.equal(dupTag.status, 409); assert.equal(dupTag.body.error.code, 'tag_taken');
  assert.equal((await mk(s, c, 'Third Clan', 'THRD')).status, 201);
  assert.equal((await s.call('POST', '/api/clans', { body: { name: 'x', tag: 'XX' } })).status, 401);
  await s.close();
});

test('clans: invitations accepted by the invitee only; roles gate privileged actions server-side', async () => {
  const { s, users: [lead, m1, m2, outsider] } = await setup(4);
  const clan = (await mk(s, lead)).body;
  // a plain member can't invite
  await s.call('POST', `/api/clans/${clan.id}/invitations`, { token: lead.token, body: { publicId: m1.me.publicId } });
  const inv = (await s.call('GET', '/api/clans/me', { token: m1.token })).body;
  assert.ok(inv.invitations.length === 1, JSON.stringify(inv));
  assert.equal((await s.call('POST', `/api/clans/invitations/${inv.invitations[0].id}/accept`, { token: outsider.token })).status >= 400, true);
  assert.equal((await s.call('POST', `/api/clans/invitations/${inv.invitations[0].id}/accept`, { token: m1.token })).status, 200);
  const nope = await s.call('POST', `/api/clans/${clan.id}/invitations`, { token: m1.token, body: { publicId: m2.me.publicId } });
  assert.equal(nope.status, 403);
  // member cannot promote, kick or edit
  assert.equal((await s.call('POST', `/api/clans/${clan.id}/members/${lead.me.publicId}/kick`, { token: m1.token })).status, 403);
  assert.equal((await s.call('PATCH', `/api/clans/${clan.id}`, { token: m1.token, body: { description: 'hijack' } })).status, 403);
  assert.equal((await s.call('DELETE', `/api/clans/${clan.id}`, { token: m1.token })).status, 403);
  // leader promotes; officer can now invite but cannot kick the leader
  assert.equal((await s.call('POST', `/api/clans/${clan.id}/members/${m1.me.publicId}/promote`, { token: lead.token })).status, 200);
  assert.equal((await s.call('POST', `/api/clans/${clan.id}/invitations`, { token: m1.token, body: { publicId: m2.me.publicId } })).status, 201);
  assert.equal((await s.call('POST', `/api/clans/${clan.id}/members/${lead.me.publicId}/kick`, { token: m1.token })).status, 403);
  await s.close();
});

test('clans: join policies, applications, member limit, leader transfer and leaving', async () => {
  const { s, users: [lead, a, b] } = await setup(3);
  const clan = (await mk(s, lead, 'Open Door', 'OPEN', { joinPolicy: 'invite' })).body;
  assert.equal((await s.call('POST', `/api/clans/${clan.id}/applications`, { token: a.token, body: {} })).body.error.code, 'invite_only');
  await s.call('PATCH', `/api/clans/${clan.id}`, { token: lead.token, body: { joinPolicy: 'request' } });
  const app = await s.call('POST', `/api/clans/${clan.id}/applications`, { token: a.token, body: { message: 'hi' } });
  assert.equal(app.status, 201); assert.equal(app.body.joined, false);
  assert.equal((await s.call('POST', `/api/clans/applications/${app.body.applicationId}/accept`, { token: b.token })).status, 403);
  assert.equal((await s.call('POST', `/api/clans/applications/${app.body.applicationId}/accept`, { token: lead.token })).status, 200);
  await s.call('PATCH', `/api/clans/${clan.id}`, { token: lead.token, body: { joinPolicy: 'open' } });
  const joined = await s.call('POST', `/api/clans/${clan.id}/applications`, { token: b.token, body: {} });
  assert.equal(joined.body.joined, true);
  // leader cannot just leave; must transfer
  assert.notEqual((await s.call('POST', `/api/clans/${clan.id}/leave`, { token: lead.token })).status, 200);
  assert.equal((await s.call('POST', `/api/clans/${clan.id}/transfer`, { token: a.token, body: { publicId: a.me.publicId } })).status, 403);
  assert.equal((await s.call('POST', `/api/clans/${clan.id}/transfer`, { token: lead.token, body: { publicId: a.me.publicId } })).status, 200);
  const view = (await s.call('GET', `/api/clans/${clan.id}`, { token: a.token })).body;
  assert.equal(view.myRole, 'leader');
  assert.equal(view.members.filter(m => m.role === 'leader').length, 1);
  assert.equal((await s.call('POST', `/api/clans/${clan.id}/leave`, { token: b.token })).status, 200);
  await s.close();
});

test('clans: member limit enforced and simultaneous joins never exceed it', async () => {
  const { s, users } = await setup(8);
  const lead = users[0];
  const clan = (await mk(s, lead, 'Tiny Crew', 'TINY', { joinPolicy: 'open' })).body;
  s.app.db.prepare('UPDATE clans SET member_limit = 3 WHERE id = ?').run(clan.id);
  const results = await Promise.all(users.slice(1).map(u => s.call('POST', `/api/clans/${clan.id}/applications`, { token: u.token, body: {} })));
  const ok = results.filter(r => r.status === 201 && r.body.joined).length;
  assert.equal(ok, 2);
  assert.equal(s.app.db.prepare('SELECT COUNT(*) c FROM clan_members WHERE clan_id = ?').get(clan.id).c, 3);
  await s.close();
});

test('clans: chat for members only, reports validated, muted players cannot chat', async () => {
  const { s, users: [lead, m, out] } = await setup(3);
  const clan = (await mk(s, lead, 'Chatters', 'CHAT', { joinPolicy: 'open' })).body;
  await s.call('POST', `/api/clans/${clan.id}/applications`, { token: m.token, body: {} });
  assert.equal((await s.call('POST', `/api/clans/${clan.id}/chat`, { token: out.token, body: { text: 'hello' } })).status, 403);
  assert.equal((await s.call('GET', `/api/clans/${clan.id}/chat`, { token: out.token })).status, 403);
  const posted = await s.call('POST', `/api/clans/${clan.id}/chat`, { token: m.token, body: { text: 'hello team' } });
  assert.equal(posted.status, 201);
  const chat = (await s.call('GET', `/api/clans/${clan.id}/chat`, { token: lead.token })).body;
  assert.equal(chat.messages[0].text, 'hello team');
  assert.equal((await s.call('POST', `/api/clans/${clan.id}/report`, { token: lead.token, body: { category: 'nonsense', reason: 'bad' } })).status, 400);
  assert.equal((await s.call('POST', `/api/clans/${clan.id}/report`, { token: out.token, body: { category: 'offensive_name', reason: 'rude name' } })).status, 201);
  await s.close();
});

test('clans: disbanding only by the leader and data persists across restart', async () => {
  const path = require('node:path'), os = require('node:os'), fs = require('node:fs');
  const dir = fs.mkdtempSync(path.join(os.tmpdir(), 'noxy-clan-'));
  const file = path.join(dir, 'db.sqlite');
  let s = await startServer({ dbPath: file });
  const a = await s.register('persistlead');
  const clan = (await mk(s, a, 'Persist', 'PRST')).body;
  const token = a.token;
  await s.close();
  s = await startServer({ dbPath: file });
  const got = await s.call('GET', `/api/clans/${clan.id}`, { token });
  assert.equal(got.status, 200); assert.equal(got.body.name, 'Persist');
  assert.equal((await s.call('DELETE', `/api/clans/${clan.id}`, { token })).status, 200);
  assert.equal((await s.call('GET', `/api/clans/${clan.id}`, { token })).status, 404);
  await s.close();
  fs.rmSync(dir, { recursive: true, force: true });
});
