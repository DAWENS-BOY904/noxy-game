'use strict';
const test = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const os = require('node:os');
const { startServer } = require('./helpers');
const { catalog } = require('../src/app');

test('registration allocates a permanent unique 10-digit player ID', async () => {
  const s = await startServer();
  try {
    const ids = new Set();
    for (let i = 0; i < 30; i++) {
      const u = await s.register('player_' + i);
      assert.match(u.me.publicId, /^[1-9][0-9]{9}$/);
      ids.add(u.me.publicId);
    }
    assert.equal(ids.size, 30);
  } finally { await s.close(); }
});

test('ID collisions are retried with a new ID', async () => {
  const sequence = ['1234567890', '1234567890', '1234567890', '9876543210'];
  let i = 0;
  const s = await startServer({ config: { idGenerator: () => sequence[Math.min(i++, sequence.length - 1)] } });
  try {
    const a = await s.register('first_user');
    const b = await s.register('second_user');
    assert.equal(a.me.publicId, '1234567890');
    assert.equal(b.me.publicId, '9876543210');
  } finally { await s.close(); }
});

test('database enforces 10-digit unique IDs', async () => {
  const s = await startServer();
  try {
    const now = Date.now();
    const ins = id => s.app.db.prepare("INSERT INTO players(public_id, username, password_salt, password_hash, created_at) VALUES (?, ?, 'a', 'b', ?)").run(id, 'u' + Math.random().toString(36).slice(2, 8), now);
    assert.throws(() => ins('123'));
    assert.throws(() => ins('0123456789'));
    assert.throws(() => ins('12345abcde'));
    ins('1234567890');
    assert.throws(() => ins('1234567890'));
  } finally { await s.close(); }
});

test('ID is retained across sign out / sign in and a second device', async () => {
  const s = await startServer();
  try {
    const a = await s.register('persist_me');
    assert.equal((await s.call('POST', '/api/auth/logout', { token: a.token })).status, 200);
    assert.equal((await s.call('GET', '/api/me', { token: a.token })).status, 401);
    const login = await s.call('POST', '/api/auth/login', { body: { username: 'PERSIST_ME', password: 'password123' } });
    assert.equal(login.status, 200);
    assert.equal(login.body.player.publicId, a.me.publicId);
    const login2 = await s.call('POST', '/api/auth/login', { body: { username: 'persist_me', password: 'password123' } });
    assert.equal(login2.body.player.publicId, a.me.publicId);
    assert.notEqual(login2.body.token, login.body.token);
  } finally { await s.close(); }
});

test('auth validation and generic credential errors', async () => {
  const s = await startServer();
  try {
    await s.register('real_user');
    assert.equal((await s.call('POST', '/api/auth/register', { body: { username: 'ab', password: 'password123' } })).body.error.code, 'invalid_username');
    assert.equal((await s.call('POST', '/api/auth/register', { body: { username: 'good_name', password: 'short' } })).body.error.code, 'invalid_password');
    assert.equal((await s.call('POST', '/api/auth/register', { body: { username: 'REAL_USER', password: 'password123' } })).status, 409);
    const wrongPw = await s.call('POST', '/api/auth/login', { body: { username: 'real_user', password: 'nope-nope' } });
    const noUser = await s.call('POST', '/api/auth/login', { body: { username: 'ghost', password: 'nope-nope' } });
    assert.equal(wrongPw.status, 401);
    assert.deepEqual(wrongPw.body, noUser.body);
    assert.equal((await s.call('GET', '/api/me')).status, 401);
    assert.equal((await s.call('GET', '/api/me', { token: 'x'.repeat(30) })).status, 401);
  } finally { await s.close(); }
});

test('responses never expose passwords, hashes or tokens of other players', async () => {
  const s = await startServer();
  try {
    const a = await s.register('alice_a');
    const b = await s.register('bob_b');
    const profile = await s.call('GET', `/api/players/${b.me.publicId}`, { token: a.token });
    const text = JSON.stringify(profile.body) + JSON.stringify(a.me);
    assert.ok(!/password|salt|hash|token|email/i.test(text), text);
    const stored = s.app.db.prepare('SELECT token_hash FROM sessions').all();
    assert.ok(stored.every(r => r.token_hash !== a.token));
  } finally { await s.close(); }
});

test('search by exact ID and username; invalid queries; blocked users hidden', async () => {
  const s = await startServer();
  try {
    const a = await s.register('searcher');
    const b = await s.register('target_one');
    await s.register('target_two');
    const byId = await s.call('GET', `/api/players/search?q=${b.me.publicId}`, { token: a.token });
    assert.equal(byId.body.results.length, 1);
    assert.equal(byId.body.results[0].username, 'target_one');
    assert.deepEqual(Object.keys(byId.body.results[0]).sort(), ['level', 'publicId', 'rank', 'relationship', 'status', 'username']);
    const byPrefix = await s.call('GET', '/api/players/search?q=target', { token: a.token });
    assert.equal(byPrefix.body.results.length, 2);
    assert.equal((await s.call('GET', '/api/players/search?q=9999999999', { token: a.token })).body.results.length, 0);
    assert.equal((await s.call('GET', '/api/players/search?q=%25', { token: a.token })).status, 400);
    assert.equal((await s.call('GET', '/api/players/search?q=ab', { token: a.token })).status, 400);
    assert.equal((await s.call('GET', '/api/players/search?q=target', {})).status, 401);

    await s.call('POST', '/api/blocks', { token: a.token, body: { publicId: b.me.publicId } });
    assert.equal((await s.call('GET', `/api/players/search?q=${b.me.publicId}`, { token: a.token })).body.results.length, 0);
    assert.equal((await s.call('GET', `/api/players/search?q=${a.me.publicId}`, { token: b.token })).body.results.length, 0);
    assert.equal((await s.call('GET', `/api/players/${b.me.publicId}`, { token: a.token })).status, 404);
  } finally { await s.close(); }
});

test('search is rate limited', async () => {
  const s = await startServer({ config: { limits: { search: [3, 60000] } } });
  try {
    const a = await s.register('limited_user');
    for (let i = 0; i < 3; i++) assert.equal((await s.call('GET', '/api/players/search?q=abc', { token: a.token })).status, 200);
    const r = await s.call('GET', '/api/players/search?q=abc', { token: a.token });
    assert.equal(r.status, 429);
    assert.ok(r.headers.get('retry-after'));
    s.clock.t += 61000;
    assert.equal((await s.call('GET', '/api/players/search?q=abc', { token: a.token })).status, 200);
  } finally { await s.close(); }
});

test('public profile shows safe data and relationship', async () => {
  const s = await startServer();
  try {
    const a = await s.register('viewer_a');
    const b = await s.register('viewed_b');
    let p = await s.call('GET', `/api/players/${b.me.publicId}`, { token: a.token });
    assert.equal(p.body.relationship, 'none');
    assert.equal(p.body.loadout.outfit, 'outfit_urban');
    assert.equal(p.status, 200);
    assert.equal((await s.call('GET', '/api/players/abc', { token: a.token })).status, 404);
    assert.equal((await s.call('GET', '/api/players/1000000000', { token: a.token })).status, 404);
    await s.befriend(a, b);
    p = await s.call('GET', `/api/players/${b.me.publicId}`, { token: a.token });
    assert.equal(p.body.relationship, 'friend');
  } finally { await s.close(); }
});

test('friend requests: send, duplicate, self, accept, decline, cancel, remove', async () => {
  const s = await startServer();
  try {
    const a = await s.register('friend_a');
    const b = await s.register('friend_b');
    const c = await s.register('friend_c');
    assert.equal((await s.call('POST', '/api/friends/requests', { token: a.token, body: { publicId: a.me.publicId } })).body.error.code, 'self_request');
    const r1 = await s.call('POST', '/api/friends/requests', { token: a.token, body: { publicId: b.me.publicId } });
    assert.equal(r1.status, 201);
    assert.equal((await s.call('POST', '/api/friends/requests', { token: a.token, body: { publicId: b.me.publicId } })).body.error.code, 'duplicate_request');
    assert.equal((await s.call('POST', '/api/friends/requests', { token: b.token, body: { publicId: a.me.publicId } })).body.error.code, 'incoming_request_exists');

    let list = await s.call('GET', '/api/friends', { token: b.token });
    assert.equal(list.body.incoming.length, 1);
    assert.equal((await s.call('GET', '/api/friends', { token: a.token })).body.outgoing.length, 1);

    // only the recipient can accept
    assert.equal((await s.call('POST', `/api/friends/requests/${r1.body.requestId}/accept`, { token: a.token })).status, 404);
    assert.equal((await s.call('POST', `/api/friends/requests/${r1.body.requestId}/accept`, { token: c.token })).status, 404);
    assert.equal((await s.call('POST', `/api/friends/requests/${r1.body.requestId}/accept`, { token: b.token })).status, 200);
    assert.equal((await s.call('POST', '/api/friends/requests', { token: a.token, body: { publicId: b.me.publicId } })).body.error.code, 'already_friends');
    assert.equal((await s.call('GET', '/api/friends', { token: a.token })).body.friends[0].username, 'friend_b');

    const r2 = await s.call('POST', '/api/friends/requests', { token: c.token, body: { publicId: a.me.publicId } });
    assert.equal((await s.call('POST', `/api/friends/requests/${r2.body.requestId}/decline`, { token: a.token })).status, 200);
    const r3 = await s.call('POST', '/api/friends/requests', { token: c.token, body: { publicId: a.me.publicId } });
    assert.equal(r3.status, 201);
    assert.equal((await s.call('POST', `/api/friends/requests/${r3.body.requestId}/cancel`, { token: c.token })).status, 200);

    assert.equal((await s.call('DELETE', `/api/friends/${b.me.publicId}`, { token: a.token })).status, 200);
    assert.equal((await s.call('GET', '/api/friends', { token: a.token })).body.friends.length, 0);
    assert.equal((await s.call('DELETE', `/api/friends/${b.me.publicId}`, { token: a.token })).status, 404);

    const notes = await s.call('GET', '/api/notifications', { token: a.token });
    assert.ok(notes.body.notifications.some(n => n.kind === 'friend_accepted'));
  } finally { await s.close(); }
});

test('friend requests respect privacy and blocks', async () => {
  const s = await startServer();
  try {
    const a = await s.register('priv_a');
    const b = await s.register('priv_b');
    await s.call('PUT', '/api/me/settings', { token: b.token, body: { friendRequests: 'nobody' } });
    assert.equal((await s.call('POST', '/api/friends/requests', { token: a.token, body: { publicId: b.me.publicId } })).body.error.code, 'requests_disabled');
    await s.call('PUT', '/api/me/settings', { token: b.token, body: { friendRequests: 'everyone' } });
    await s.call('POST', '/api/blocks', { token: b.token, body: { publicId: a.me.publicId } });
    assert.equal((await s.call('POST', '/api/friends/requests', { token: a.token, body: { publicId: b.me.publicId } })).status, 404);
    assert.equal((await s.call('GET', '/api/blocks', { token: b.token })).body.blocked[0].username, 'priv_a');
    await s.call('DELETE', `/api/blocks/${a.me.publicId}`, { token: b.token });
    assert.equal((await s.call('POST', '/api/friends/requests', { token: a.token, body: { publicId: b.me.publicId } })).status, 201);
  } finally { await s.close(); }
});

test('blocking removes the friendship', async () => {
  const s = await startServer();
  try {
    const a = await s.register('blk_a');
    const b = await s.register('blk_b');
    await s.befriend(a, b);
    await s.call('POST', '/api/blocks', { token: a.token, body: { publicId: b.me.publicId } });
    assert.equal((await s.call('GET', '/api/friends', { token: a.token })).body.friends.length, 0);
    assert.equal((await s.call('GET', '/api/friends', { token: b.token })).body.friends.length, 0);
  } finally { await s.close(); }
});

test('presence respects privacy and heartbeat timeout', async () => {
  const s = await startServer();
  try {
    const a = await s.register('pres_a');
    const b = await s.register('pres_b');
    const c = await s.register('pres_c');
    await s.befriend(a, b);
    await s.call('POST', '/api/presence', { token: a.token, body: { state: 'lobby' } });
    const view = async (viewer, id) => (await s.call('GET', `/api/players/${id}`, { token: viewer.token })).body.status;
    assert.equal(await view(b, a.me.publicId), 'lobby');
    assert.equal(await view(c, a.me.publicId), 'hidden'); // default: friends only
    await s.call('PUT', '/api/me/settings', { token: a.token, body: { onlineStatus: 'everyone' } });
    assert.equal(await view(c, a.me.publicId), 'lobby');
    await s.call('PUT', '/api/me/settings', { token: a.token, body: { onlineStatus: 'nobody' } });
    assert.equal(await view(b, a.me.publicId), 'hidden');
    assert.equal(await view(a, a.me.publicId), 'lobby');
    await s.call('PUT', '/api/me/settings', { token: a.token, body: { onlineStatus: 'friends' } });
    s.clock.t += 31000;
    assert.equal(await view(b, a.me.publicId), 'offline');
    assert.equal((await s.call('POST', '/api/presence', { token: a.token, body: { state: 'bogus' } })).status, 400);
  } finally { await s.close(); }
});

async function trio(s) {
  const a = await s.register('lead_a');
  const b = await s.register('mate_b');
  const c = await s.register('mate_c');
  await s.befriend(a, b);
  await s.befriend(a, c);
  return { a, b, c };
}
const invite = (s, from, to, extra = {}) => s.call('POST', '/api/party/invite', { token: from.token, body: { publicId: to.me.publicId, ...extra } });

test('party invites: send, accept, decline, party state', async () => {
  const s = await startServer();
  try {
    const { a, b, c } = await trio(s);
    const inv = await invite(s, a, b);
    assert.equal(inv.status, 201);

    const bView = await s.call('GET', '/api/party', { token: b.token });
    assert.equal(bView.body.invitations.length, 1);
    assert.equal(bView.body.invitations[0].fromUsername, 'lead_a');
    assert.equal(bView.body.invitations[0].modeId, 'battle_royale');
    assert.equal(bView.body.party, null);

    // someone else cannot answer b's invitation
    assert.equal((await s.call('POST', `/api/party/invitations/${inv.body.invitationId}/accept`, { token: c.token })).status, 404);

    const acc = await s.call('POST', `/api/party/invitations/${inv.body.invitationId}/accept`, { token: b.token });
    assert.equal(acc.status, 200);
    assert.equal(acc.body.party.members.length, 2);
    assert.equal(acc.body.party.members.find(m => m.isLeader).username, 'lead_a');
    assert.equal((await s.call('POST', `/api/party/invitations/${inv.body.invitationId}/accept`, { token: b.token })).status, 409);

    const inv2 = await invite(s, a, c);
    assert.equal((await s.call('POST', `/api/party/invitations/${inv2.body.invitationId}/decline`, { token: c.token })).status, 200);
    const notes = await s.call('GET', '/api/notifications', { token: a.token });
    assert.ok(notes.body.notifications.some(n => n.kind === 'invite_accepted'));
    assert.ok(notes.body.notifications.some(n => n.kind === 'invite_declined'));
  } finally { await s.close(); }
});

test('party invites: non-friends, disabled, unsupported mode, self, full party, not leader', async () => {
  const s = await startServer();
  try {
    const { a, b, c } = await trio(s);
    const stranger = await s.register('stranger_x');
    assert.equal((await invite(s, a, stranger)).body.error.code, 'not_friends');
    assert.equal((await invite(s, a, a)).body.error.code, 'self_invite');
    assert.equal((await invite(s, a, b, { modeId: 'training' })).body.error.code, 'mode_not_supported');
    assert.equal((await invite(s, a, b, { modeId: 'lone_wolf' })).body.error.code, 'mode_not_supported');
    assert.equal((await invite(s, a, b, { modeId: 'nope' })).body.error.code, 'invalid_mode');

    await s.call('PUT', '/api/me/settings', { token: b.token, body: { partyInvites: 'nobody' } });
    assert.equal((await invite(s, a, b)).body.error.code, 'invites_disabled');
    await s.call('PUT', '/api/me/settings', { token: b.token, body: { partyInvites: 'friends' } });

    const ib = await invite(s, a, b);
    await s.call('POST', `/api/party/invitations/${ib.body.invitationId}/accept`, { token: b.token });
    await s.befriend(b, c);
    assert.equal((await invite(s, b, c)).body.error.code, 'not_leader');

    // fill party to 4
    const d = await s.register('mate_d');
    const e = await s.register('mate_e');
    await s.befriend(a, d); await s.befriend(a, e);
    for (const p of [c, d]) {
      const i = await invite(s, a, p);
      assert.equal((await s.call('POST', `/api/party/invitations/${i.body.invitationId}/accept`, { token: p.token })).status, 200);
    }
    assert.equal((await invite(s, a, e)).body.error.code, 'party_full');
  } finally { await s.close(); }
});

test('party invites: cooldown, expiry, pending cap', async () => {
  const s = await startServer({ config: { maxPendingSentInvites: 2 } });
  try {
    const { a, b, c } = await trio(s);
    const first = await invite(s, a, b);
    assert.equal(first.status, 201);
    const again = await invite(s, a, b);
    assert.equal(again.status, 429);
    assert.equal(again.body.error.code, 'invite_cooldown');

    s.clock.t += 61000; // past TTL
    const expired = await s.call('POST', `/api/party/invitations/${first.body.invitationId}/accept`, { token: b.token });
    assert.equal(expired.status, 410);
    assert.equal(expired.body.error.code, 'invitation_expired');
    const notes = await s.call('GET', '/api/notifications', { token: a.token });
    assert.ok(notes.body.notifications.some(n => n.kind === 'invite_expired'));
    assert.equal((await s.call('GET', '/api/party', { token: b.token })).body.invitations.length, 0);

    assert.equal((await invite(s, a, b)).status, 201);
    assert.equal((await invite(s, a, c)).status, 201);
    const d = await s.register('mate_d2');
    await s.befriend(a, d);
    assert.equal((await invite(s, a, d)).body.error.code, 'too_many_invites');
  } finally { await s.close(); }
});

test('party: leave promotes a new leader, ready and mode rules', async () => {
  const s = await startServer();
  try {
    const { a, b } = await trio(s);
    const i = await invite(s, a, b);
    await s.call('POST', `/api/party/invitations/${i.body.invitationId}/accept`, { token: b.token });

    const ready = await s.call('POST', '/api/party/ready', { token: b.token, body: { ready: true } });
    assert.equal(ready.body.party.members.find(m => m.username === 'mate_b').ready, true);
    assert.equal((await s.call('POST', '/api/party/mode', { token: b.token, body: { modeId: 'battle_royale' } })).body.error.code, 'not_leader');
    assert.equal((await s.call('POST', '/api/party/mode', { token: a.token, body: { modeId: 'training' } })).body.error.code, 'party_too_large');

    await s.call('POST', '/api/party/leave', { token: a.token });
    const view = await s.call('GET', '/api/party', { token: b.token });
    assert.equal(view.body.party.members.length, 1);
    assert.equal(view.body.party.members[0].isLeader, true);
    assert.equal((await s.call('GET', '/api/party', { token: a.token })).body.party, null);
    await s.call('POST', '/api/party/leave', { token: b.token });
    assert.equal((await s.call('GET', '/api/party', { token: b.token })).body.party, null);
    assert.equal((await s.call('POST', '/api/party/ready', { token: b.token, body: { ready: true } })).status, 404);
  } finally { await s.close(); }
});

test('private party cannot be joined without an invitation', async () => {
  const s = await startServer();
  try {
    const { a, b } = await trio(s);
    const outsider = await s.register('outsider_o');
    const i = await invite(s, a, b);
    assert.equal((await s.call('POST', `/api/party/invitations/${i.body.invitationId}/accept`, { token: outsider.token })).status, 404);
    assert.equal((await s.call('POST', '/api/party/invitations/999/accept', { token: outsider.token })).status, 404);
    assert.equal((await s.call('GET', '/api/party', { token: outsider.token })).body.party, null);
  } finally { await s.close(); }
});

test('accepting an invite leaves the previous party and blocks respect', async () => {
  const s = await startServer();
  try {
    const { a, b, c } = await trio(s);
    await s.befriend(b, c);
    const ia = await invite(s, a, b);
    await s.call('POST', `/api/party/invitations/${ia.body.invitationId}/accept`, { token: b.token });
    const ic = await invite(s, c, b);
    assert.equal(ic.status, 201);
    await s.call('POST', `/api/party/invitations/${ic.body.invitationId}/accept`, { token: b.token });
    const party = (await s.call('GET', '/api/party', { token: b.token })).body.party;
    assert.deepEqual(party.members.map(m => m.username).sort(), ['mate_b', 'mate_c']);
    assert.equal((await s.call('GET', '/api/party', { token: a.token })).body.party.members.length, 1);

    // invite then block -> invitation cancelled
    const i2 = await invite(s, a, c);
    await s.call('POST', '/api/blocks', { token: c.token, body: { publicId: a.me.publicId } });
    assert.equal((await s.call('POST', `/api/party/invitations/${i2.body.invitationId}/accept`, { token: c.token })).status, 409);
  } finally { await s.close(); }
});

test('cosmetics: only owned items can be equipped, slots validated, unequip rules', async () => {
  const s = await startServer();
  try {
    const a = await s.register('vault_user');
    const put = loadout => s.call('PUT', '/api/me/loadout', { token: a.token, body: { loadout } });
    assert.equal((await put({ outfit: 'outfit_aurora' })).body.error.code, 'not_owned');
    assert.equal((await put({ outfit: 'does_not_exist' })).body.error.code, 'invalid_item');
    assert.equal((await put({ shoes: 'outfit_urban' })).body.error.code, 'invalid_item');
    assert.equal((await put({ weird: 'x' })).body.error.code, 'invalid_slot');
    assert.equal((await put({ outfit: '' })).body.error.code, 'slot_required');
    assert.equal((await put({ outfit: 'outfit_nightops', mask: 'mask_visor' })).status, 200);
    let inv = await s.call('GET', '/api/me/inventory', { token: a.token });
    assert.equal(inv.body.loadout.outfit, 'outfit_nightops');
    assert.equal(inv.body.loadout.mask, 'mask_visor');
    assert.ok(!inv.body.owned.includes('outfit_aurora'));
    assert.equal((await put({ mask: '' })).status, 200);
    inv = await s.call('GET', '/api/me/inventory', { token: a.token });
    assert.equal(inv.body.loadout.mask, undefined);
    // a failed request must not partially apply
    assert.equal((await put({ outfit: 'outfit_recruit', shoes: 'shoes_neon' })).status, 403);
    assert.equal((await s.call('GET', '/api/me', { token: a.token })).body.loadout.outfit, 'outfit_nightops');
    assert.equal((await s.call('POST', '/api/shop/purchase', { token: a.token, body: { itemId: 'outfit_aurora' } })).status, 501);
  } finally { await s.close(); }
});

test('client cosmetic catalog ids match the server catalog', () => {
  const cs = fs.readFileSync(path.join(__dirname, '..', '..', 'Assets', '_Project', 'Scripts', 'Core', 'Cosmetics.cs'), 'utf8');
  const clientIds = new Set([...cs.matchAll(/Add\("([a-z_]+)"/g)].map(m => m[1]));
  const serverIds = new Set(catalog.items.map(i => i.id));
  assert.deepEqual([...clientIds].sort(), [...serverIds].sort());
  for (const id of catalog.startingOwned) assert.ok(serverIds.has(id));
});

test('settings validation and persistence', async () => {
  const s = await startServer();
  try {
    const a = await s.register('settings_user');
    const put = body => s.call('PUT', '/api/me/settings', { token: a.token, body });
    assert.equal((await put({ onlineStatus: 'everybody' })).status, 400);
    assert.equal((await put({ data: [1] })).status, 400);
    assert.equal((await put({ data: { 'bad key!': 1 } })).status, 400);
    assert.equal((await put({ data: { x: { nested: 1 } } })).status, 400);
    assert.equal((await put({ data: { x: 1e12 } })).status, 400);
    assert.equal((await put({ partyInvites: 'nobody', data: { sensGeneral: 55, radarNorthUp: true, lang: 'en' } })).status, 200);
    const g = await s.call('GET', '/api/me/settings', { token: a.token });
    assert.equal(g.body.partyInvites, 'nobody');
    assert.equal(g.body.data.sensGeneral, 55);
    assert.equal(g.body.data.radarNorthUp, true);
  } finally { await s.close(); }
});

test('HUD layouts save, load, and reject invalid data', async () => {
  const s = await startServer();
  try {
    const a = await s.register('hud_user');
    const good = { elements: { fire: { x: 0.85, y: 0.7, scale: 1.2, opacity: 0.8 }, slot1: { x: 0.5, y: 0.9, scale: 1, opacity: 1 } } };
    assert.equal((await s.call('GET', '/api/me/hud/landscape', { token: a.token })).status, 404);
    assert.equal((await s.call('PUT', '/api/me/hud/landscape', { token: a.token, body: good })).status, 200);
    assert.deepEqual((await s.call('GET', '/api/me/hud/landscape', { token: a.token })).body.elements, good.elements);
    const bad = elements => s.call('PUT', '/api/me/hud/landscape', { token: a.token, body: { elements } });
    assert.equal((await bad({ fire: { x: 1.5, y: 0.5, scale: 1, opacity: 1 } })).status, 400);
    assert.equal((await bad({ fire: { x: 0.5, y: 0.5, scale: 9, opacity: 1 } })).status, 400);
    assert.equal((await bad({ hacks: { x: 0.5, y: 0.5, scale: 1, opacity: 1 } })).status, 400);
    assert.equal((await s.call('PUT', '/api/me/hud/BAD KEY', { token: a.token, body: good })).status, 400);
  } finally { await s.close(); }
});

test('username change rules, delete account requires password and cascades', async () => {
  const s = await startServer();
  try {
    const a = await s.register('old_name');
    await s.register('taken_name');
    assert.equal((await s.call('PATCH', '/api/me', { token: a.token, body: { username: 'taken_name' } })).body.error.code, 'username_taken');
    assert.equal((await s.call('PATCH', '/api/me', { token: a.token, body: { username: 'x' } })).status, 400);
    const ok = await s.call('PATCH', '/api/me', { token: a.token, body: { username: 'new_name' } });
    assert.equal(ok.body.username, 'new_name');
    assert.equal(ok.body.publicId, a.me.publicId);
    assert.equal((await s.call('PATCH', '/api/me', { token: a.token, body: { username: 'newer_name' } })).body.error.code, 'username_cooldown');

    assert.equal((await s.call('DELETE', '/api/me', { token: a.token, body: { password: 'wrong-password' } })).status, 403);
    assert.equal((await s.call('DELETE', '/api/me', { token: a.token, body: { password: 'password123' } })).status, 200);
    assert.equal((await s.call('GET', '/api/me', { token: a.token })).status, 401);
    assert.equal(s.app.db.prepare('SELECT COUNT(*) c FROM player_inventory WHERE player_id = 1').get().c, 0);
    assert.equal((await s.call('POST', '/api/auth/login', { body: { username: 'new_name', password: 'password123' } })).status, 401);
  } finally { await s.close(); }
});

test('report, bad JSON, oversized body, unknown routes', async () => {
  const s = await startServer();
  try {
    const a = await s.register('reporter_a');
    const b = await s.register('reported_b');
    assert.equal((await s.call('POST', `/api/players/${b.me.publicId}/report`, { token: a.token, body: { reason: 'cheating' } })).status, 201);
    assert.equal((await s.call('POST', `/api/players/${b.me.publicId}/report`, { token: a.token, body: { reason: '' } })).status, 400);
    assert.equal((await s.call('POST', `/api/players/${a.me.publicId}/report`, { token: a.token, body: { reason: 'self' } })).status, 404);

    const raw = await fetch(s.base + '/api/auth/login', { method: 'POST', body: '{not json' });
    assert.equal(raw.status, 400);
    const big = await fetch(s.base + '/api/auth/login', { method: 'POST', body: JSON.stringify({ username: 'a'.repeat(100000) }) }).catch(() => ({ status: 413 }));
    assert.equal(big.status, 413);
    assert.equal((await s.call('GET', '/api/nothing')).status, 404);
    assert.equal((await s.call('PUT', '/api/me', { token: a.token, body: {} })).status, 405);
  } finally { await s.close(); }
});

test('auth endpoints are rate limited per client', async () => {
  const s = await startServer({ config: { limits: { auth: [3, 60000] } } });
  try {
    for (let i = 0; i < 3; i++) await s.call('POST', '/api/auth/login', { body: { username: 'ghost', password: 'whatever1' } });
    assert.equal((await s.call('POST', '/api/auth/login', { body: { username: 'ghost', password: 'whatever1' } })).status, 429);
  } finally { await s.close(); }
});

test('data persists across restarts (file database)', async () => {
  const file = path.join(os.tmpdir(), `noxy-test-${process.pid}-${Date.now()}.sqlite`);
  let id;
  let s = await startServer({ dbPath: file });
  try { id = (await s.register('restart_user')).me.publicId; } finally { await s.close(); }
  s = await startServer({ dbPath: file });
  try {
    const login = await s.call('POST', '/api/auth/login', { body: { username: 'restart_user', password: 'password123' } });
    assert.equal(login.body.player.publicId, id);
  } finally { await s.close(); for (const ext of ['', '-wal', '-shm']) fs.rmSync(file + ext, { force: true }); }
});
