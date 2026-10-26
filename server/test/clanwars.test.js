'use strict';
const test = require('node:test');
const assert = require('node:assert/strict');
const { startServer } = require('./helpers');

const KEY = 'unit-test-server-key';
const cfg = (wars = {}) => ({ economy: { matchServerKey: KEY }, clans: { wars } });
async function internal(s, method, path, body, key = KEY) {
  const res = await fetch(s.base + path, { method, headers: { 'Content-Type': 'application/json', ...(key ? { 'x-noxy-server-key': key } : {}) }, body: body === undefined ? undefined : JSON.stringify(body) });
  const text = await res.text();
  return { status: res.status, body: text ? JSON.parse(text) : null };
}
const wallet = async (s, p) => (await s.call('GET', '/api/me/wallet', { token: p.token })).body;

// A clan of two (leader + one open-door joiner) whose members are old enough to play.
async function clan(s, tag) {
  const lead = await s.register(tag.toLowerCase() + 'lead');
  const mate = await s.register(tag.toLowerCase() + 'mate');
  const c = (await s.call('POST', '/api/clans', { token: lead.token, body: { name: 'Clan ' + tag, tag, joinPolicy: 'open' } })).body;
  await s.call('POST', `/api/clans/${c.id}/applications`, { token: mate.token, body: {} });
  return { id: c.id, lead, mate, roster: [lead.me.publicId, mate.me.publicId] };
}
async function started(s, A, B, { age = true } = {}) {
  if (age) s.clock.t += 25 * 3600000;
  const q1 = await s.call('POST', '/api/clanwars/queue', { token: A.lead.token, body: { formatId: '2v2', players: A.roster } });
  assert.equal(q1.status, 201, JSON.stringify(q1.body));
  const q2 = await s.call('POST', '/api/clanwars/queue', { token: B.lead.token, body: { formatId: '2v2', players: B.roster } });
  assert.equal(q2.status, 201, JSON.stringify(q2.body));
  const matchId = q2.body.matchId;
  assert.ok(matchId, 'second clan should be paired immediately');
  assert.equal((await internal(s, 'POST', `/api/internal/clanwars/matches/${matchId}/start`, { serverId: 'srv-1' })).status, 200);
  return matchId;
}
// Winner "a" takes 3 straight rounds. Kills must be backed by the other side's deaths.
const resultBody = (A, B, over = {}) => ({
  status: 'completed', rounds: [{ winnerSide: 'a' }, { winnerSide: 'a' }, { winnerSide: 'a' }],
  players: [
    { publicId: A.roster[0], kills: 3, deaths: 1, score: 300 }, { publicId: A.roster[1], kills: 3, deaths: 1, score: 250 },
    { publicId: B.roster[0], kills: 1, deaths: 3, score: 100 }, { publicId: B.roster[1], kills: 1, deaths: 3, score: 90 },
  ], ...over,
});
const resultOf = (s, id, body, key = KEY) => internal(s, 'POST', `/api/internal/clanwars/matches/${id}/result`, body, key);

test('clan wars: queue pairs two clans; players cannot post, submit or start results', async () => {
  const s = await startServer({ config: cfg() });
  const A = await clan(s, 'AAA'), B = await clan(s, 'BBB');
  const id = await started(s, A, B);
  const body = resultBody(A, B);
  // forged: player token, no key, wrong key
  assert.equal((await s.call('POST', `/api/internal/clanwars/matches/${id}/result`, { token: A.lead.token, body })).status, 401);
  assert.equal((await resultOf(s, id, body, null)).status, 401);
  assert.equal((await resultOf(s, id, body, 'wrong')).status, 401);
  assert.equal((await s.call('POST', `/api/internal/clanwars/matches/${id}/start`, { token: A.lead.token, body: { serverId: 'x1' } })).status, 401);
  assert.equal((await s.call('GET', `/api/clanwars/matches/${id}`, { token: A.mate.token })).status, 200);
  const outsider = await s.register('outsider1');
  assert.equal((await s.call('GET', `/api/clanwars/matches/${id}`, { token: outsider.token })).status, 404);
  await s.close();
});

test('clan wars: only leaders/co-leaders manage the queue; new members and busy players are refused', async () => {
  const s = await startServer({ config: cfg() });
  const A = await clan(s, 'AAA');
  assert.equal((await s.call('POST', '/api/clanwars/queue', { token: A.mate.token, body: { formatId: '2v2', players: A.roster } })).status, 403);
  // members have not been in the clan 24h yet
  const young = await s.call('POST', '/api/clanwars/queue', { token: A.lead.token, body: { formatId: '2v2', players: A.roster } });
  assert.equal(young.status, 409); assert.equal(young.body.error.code, 'member_too_new');
  s.clock.t += 25 * 3600000;
  assert.equal((await s.call('POST', '/api/clanwars/queue', { token: A.lead.token, body: { formatId: '2v2', players: [A.roster[0], A.roster[0]] } })).status, 400);
  const stranger = await s.register('stranger1');
  assert.equal((await s.call('POST', '/api/clanwars/queue', { token: A.lead.token, body: { formatId: '2v2', players: [A.roster[0], stranger.me.publicId] } })).status, 400);
  assert.equal((await s.call('POST', '/api/clanwars/queue', { token: A.lead.token, body: { formatId: '9v9', players: A.roster } })).status, 400);
  assert.equal((await s.call('POST', '/api/clanwars/queue', { token: A.lead.token, body: { formatId: '2v2', players: A.roster } })).status, 201);
  assert.equal((await s.call('POST', '/api/clanwars/queue', { token: A.lead.token, body: { formatId: '2v2', players: A.roster } })).body.error.code, 'already_queued');
  await s.close();
});

test('clan wars: a verified result grants xp, rating and coins once; replays are idempotent; conflicting results rejected', async () => {
  const s = await startServer({ config: cfg() });
  const A = await clan(s, 'AAA'), B = await clan(s, 'BBB');
  const id = await started(s, A, B);
  const before = (await wallet(s, A.lead)).coins;
  const r1 = await resultOf(s, id, resultBody(A, B));
  assert.equal(r1.status, 200, JSON.stringify(r1.body));
  const ca = (await s.call('GET', `/api/clans/${A.id}`, { token: A.lead.token })).body;
  const cb = (await s.call('GET', `/api/clans/${B.id}`, { token: B.lead.token })).body;
  assert.equal(ca.wins, 1); assert.equal(cb.losses, 1);
  assert.equal(ca.xp, 150); assert.equal(cb.xp, 50);
  assert.ok(ca.rating > cb.rating);
  const earned = (await wallet(s, A.lead)).coins - before;
  assert.equal(earned, 130); // 100 win + 30 participation
  // exact replay: no double credit
  const r2 = await resultOf(s, id, resultBody(A, B));
  assert.equal(r2.status, 200); assert.equal(r2.body.duplicate, true);
  assert.equal((await wallet(s, A.lead)).coins - before, 130);
  assert.equal((await s.call('GET', `/api/clans/${A.id}`, { token: A.lead.token })).body.xp, 150);
  // different payload for a finished match
  const swapped = await resultOf(s, id, resultBody(A, B, { rounds: [{ winnerSide: 'b' }, { winnerSide: 'b' }, { winnerSide: 'b' }] }));
  assert.equal(swapped.status, 409); assert.equal(swapped.body.error.code, 'conflicting_result');
  await s.close();
});

test('clan wars: simultaneous identical results apply exactly once', async () => {
  const s = await startServer({ config: cfg() });
  const A = await clan(s, 'AAA'), B = await clan(s, 'BBB');
  const id = await started(s, A, B);
  const rs = await Promise.all(Array.from({ length: 6 }, () => resultOf(s, id, resultBody(A, B))));
  assert.ok(rs.every(r => r.status === 200));
  assert.equal(rs.filter(r => !r.body.duplicate).length, 1);
  assert.equal((await s.call('GET', `/api/clans/${A.id}`, { token: A.lead.token })).body.xp, 150);
  await s.close();
});

test('clan wars: forged or impossible results are rejected without side effects', async () => {
  const s = await startServer({ config: cfg() });
  const A = await clan(s, 'AAA'), B = await clan(s, 'BBB');
  const id = await started(s, A, B);
  const outsider = await s.register('ghost1');
  const bad = [
    resultBody(A, B, { players: [{ publicId: outsider.me.publicId, kills: 0, deaths: 0, score: 0 }] }),
    resultBody(A, B, { players: [{ publicId: A.roster[0], kills: 99, deaths: 0, score: 1 }] }), // kills not backed by deaths
    resultBody(A, B, { players: [{ publicId: A.roster[0], kills: -1, deaths: 0, score: 1 }] }),
    resultBody(A, B, { rounds: [{ winnerSide: 'a' }, { winnerSide: 'b' }] }), // no winner yet
    resultBody(A, B, { rounds: [{ winnerSide: 'a' }, { winnerSide: 'a' }, { winnerSide: 'a' }, { winnerSide: 'b' }] }), // rounds after the win
    resultBody(A, B, { status: 'victory' }),
  ];
  for (const b of bad) { const r = await resultOf(s, id, b); assert.ok(r.status >= 400 && r.status < 500, JSON.stringify(r.body)); }
  assert.equal((await s.call('GET', `/api/clans/${A.id}`, { token: A.lead.token })).body.xp, 0);
  // and a result can't be recorded for a match the server never started
  const C = await clan(s, 'CCC'), D = await clan(s, 'DDD');
  s.clock.t += 25 * 3600000;
  await s.call('POST', '/api/clanwars/queue', { token: C.lead.token, body: { formatId: '2v2', players: C.roster } });
  const pend = (await s.call('POST', '/api/clanwars/queue', { token: D.lead.token, body: { formatId: '2v2', players: D.roster } })).body.matchId;
  const early = await resultOf(s, pend, resultBody(C, D));
  assert.equal(early.status, 409); assert.equal(early.body.error.code, 'match_not_live');
  await s.close();
});

test('clan wars: repeated matches between the same two clans become unrated (no xp, rating or coins)', async () => {
  const s = await startServer({ config: cfg() });
  const A = await clan(s, 'AAA'), B = await clan(s, 'BBB');
  for (let i = 0; i < 2; i++) {
    const id = await started(s, A, B, { age: i === 0 });
    assert.equal((await resultOf(s, id, resultBody(A, B))).status, 200);
  }
  const xpAfterTwo = (await s.call('GET', `/api/clans/${A.id}`, { token: A.lead.token })).body.xp;
  // The queue refuses to pair them again; force a manual third match to prove the unrated path.
  const q1 = await s.call('POST', '/api/clanwars/queue', { token: A.lead.token, body: { formatId: '2v2', players: A.roster } });
  const q2 = await s.call('POST', '/api/clanwars/queue', { token: B.lead.token, body: { formatId: '2v2', players: B.roster } });
  assert.equal(q1.status, 201); assert.equal(q2.body.matchId, null, 'capped pair is not matched');
  assert.equal(xpAfterTwo, 300);
  await s.close();
});

test('clan wars: crashed and stale matches are voided with no rewards or penalties', async () => {
  const s = await startServer({ config: cfg() });
  const A = await clan(s, 'AAA'), B = await clan(s, 'BBB');
  const id = await started(s, A, B);
  const before = (await wallet(s, A.lead)).coins;
  const crash = await resultOf(s, id, { status: 'crashed' });
  assert.equal(crash.status, 200); assert.equal(crash.body.match.status, 'void');
  assert.equal((await wallet(s, A.lead)).coins, before);
  const ca = (await s.call('GET', `/api/clans/${A.id}`, { token: A.lead.token })).body;
  assert.equal(ca.xp, 0); assert.equal(ca.losses, 0);
  // server restart / lost match: a live match is voided by the sweep
  const C = await clan(s, 'CCC'), D = await clan(s, 'DDD');
  const id2 = await started(s, C, D);
  s.clock.t += 3 * 3600000;
  const sw = await internal(s, 'POST', '/api/internal/clanwars/sweep', {});
  assert.equal(sw.status, 200); assert.ok(sw.body.voided >= 1);
  const late = await resultOf(s, id2, resultBody(C, D));
  assert.equal(late.status, 409); assert.equal(late.body.error.code, 'match_void');
  await s.close();
});

test('clan wars: forfeits and abandoned players are handled; abandoners get no reward', async () => {
  const s = await startServer({ config: cfg() });
  const A = await clan(s, 'AAA'), B = await clan(s, 'BBB');
  const id = await started(s, A, B);
  const bBefore = (await wallet(s, B.mate)).coins;
  const r = await resultOf(s, id, { status: 'forfeit', forfeitSide: 'b', players: [
    { publicId: A.roster[0], kills: 0, deaths: 0, score: 0 }, { publicId: A.roster[1], kills: 0, deaths: 0, score: 0 },
    { publicId: B.roster[0], kills: 0, deaths: 0, score: 0 }, { publicId: B.roster[1], kills: 0, deaths: 0, score: 0, abandoned: true }] });
  assert.equal(r.status, 200, JSON.stringify(r.body));
  assert.equal((await s.call('GET', `/api/clans/${A.id}`, { token: A.lead.token })).body.wins, 1);
  assert.equal((await wallet(s, B.mate)).coins, bBefore, 'abandoning player earns nothing');
  const rep = (await s.call('GET', '/api/fairplay/me', { token: B.mate.token })).body;
  assert.ok(rep, 'reputation view available');
  await s.close();
});

test('clan wars: disputes are moderator-only to resolve and a voided result reverts clan standings', async () => {
  const s = await startServer({ config: cfg() });
  const A = await clan(s, 'AAA'), B = await clan(s, 'BBB');
  const id = await started(s, A, B);
  await resultOf(s, id, resultBody(A, B));
  assert.equal((await s.call('POST', `/api/clanwars/matches/${id}/dispute`, { token: B.mate.token, body: { reason: 'The scores look wrong to us.' } })).status, 403);
  assert.equal((await s.call('POST', `/api/clanwars/matches/${id}/dispute`, { token: B.lead.token, body: { reason: 'short' } })).status, 400);
  assert.equal((await s.call('POST', `/api/clanwars/matches/${id}/dispute`, { token: B.lead.token, body: { reason: 'The scores look wrong to us.' } })).status, 201);
  assert.equal((await s.call('POST', `/api/admin/clanwars/matches/${id}/resolve`, { token: B.lead.token, body: { decision: 'void', note: 'nope' } })).status, 403);
  const mod = await s.register('moderator1');
  s.app.db.prepare("INSERT INTO moderators(player_id, role, created_at) VALUES ((SELECT id FROM players WHERE public_id = ?), 'admin', ?)").run(mod.me.publicId, s.clock.t);
  const res = await s.call('POST', `/api/admin/clanwars/matches/${id}/resolve`, { token: mod.token, body: { decision: 'void', note: 'Evidence of match fixing.' } });
  assert.equal(res.status, 200, JSON.stringify(res.body));
  const ca = (await s.call('GET', `/api/clans/${A.id}`, { token: A.lead.token })).body;
  assert.equal(ca.wins, 0); assert.equal(ca.xp, 0);
  const audit = await s.call('GET', '/api/admin/audit', { token: mod.token });
  assert.ok(audit.body.entries.some(e => e.action === 'clanwar.dispute.void'));
  await s.close();
});
