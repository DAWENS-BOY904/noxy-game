'use strict';
const test = require('node:test');
const assert = require('node:assert/strict');
const crypto = require('node:crypto');
const fs = require('node:fs');
const os = require('node:os');
const path = require('node:path');
const { startServer } = require('./helpers');

const SERVER_KEY = 'unit-test-server-key';
const WEBHOOK = 'unit-test-webhook-secret';
const ADS = 'unit-test-ads-secret';
const economy = (extra = {}) => ({ economy: { matchServerKey: SERVER_KEY, testPayments: true, webhookSecrets: { test: WEBHOOK, apple: WEBHOOK }, adsSecret: ADS, ...extra } });

async function server(extra) { return startServer({ config: economy(extra) }); }
const matchBody = (id, winner, others = [], extra = {}) => ({
  matchId: id, mode: 'battle_royale', playerCount: 40, durationSeconds: 900,
  players: [{ publicId: winner.me.publicId, placement: 1, kills: 3 }, ...others.map((o, i) => ({ publicId: o.me.publicId, placement: i + 2, kills: 0 }))], ...extra,
});
async function postMatch(s, body, key = SERVER_KEY) {
  const res = await fetch(s.base + '/api/internal/match-results', { method: 'POST', headers: { 'Content-Type': 'application/json', ...(key ? { 'x-noxy-server-key': key } : {}) }, body: JSON.stringify(body) });
  return { status: res.status, body: await res.json() };
}
async function signed(s, path, secret, body) {
  const raw = JSON.stringify(body);
  const res = await fetch(s.base + path, { method: 'POST', headers: { 'Content-Type': 'application/json', 'x-noxy-signature': crypto.createHmac('sha256', secret).update(raw).digest('hex') }, body: raw });
  return { status: res.status, body: await res.json() };
}
const wallet = async (s, p) => (await s.call('GET', '/api/me/wallet', { token: p.token })).body;

test('new players start with an empty wallet; wallet is not client-writable', async () => {
  const s = await server();
  try {
    const a = await s.register('wallet_a');
    assert.deepEqual((await wallet(s, a)), { coins: 0, diamonds: 0, lifetime: { diamondsEarned: 0, diamondsPurchased: 0, diamondsSpent: 0, coinsEarned: 0, coinsSpent: 0 } });
    const me = (await s.call('GET', '/api/me', { token: a.token })).body;
    assert.equal(me.credits, 0); assert.equal(me.cores, 0);
    for (const m of ['PUT', 'POST', 'PATCH']) assert.ok([404, 405].includes((await s.call(m, '/api/me/wallet', { token: a.token, body: { diamonds: 9999 } })).status));
    assert.equal((await s.call('PATCH', '/api/me', { token: a.token, body: { diamonds: 9999, credits: 9999 } })).status < 500, true);
    assert.equal((await wallet(s, a)).diamonds, 0);
    assert.equal((await s.call('GET', '/api/me/wallet')).status, 401);
  } finally { await s.close(); }
});

test('ledger: debits cannot overdraw, keys make credits idempotent, history is ordered', async () => {
  const s = await server();
  try {
    const a = await s.register('ledger_a');
    const id = s.app.db.prepare('SELECT id FROM players WHERE username = ?').get('ledger_a').id;
    const eco = s.app.economy;
    eco.apply(id, 'coins', 100, { kind: 'test', key: 'k1' });
    assert.equal(eco.apply(id, 'coins', 100, { kind: 'test', key: 'k1' }).duplicate, true);
    assert.equal((await wallet(s, a)).coins, 100);
    assert.throws(() => eco.apply(id, 'coins', -101, { kind: 'test' }), /Not enough coins/);
    assert.equal((await wallet(s, a)).coins, 100);
    eco.apply(id, 'coins', -40, { kind: 'test' });
    const w = await wallet(s, a);
    assert.equal(w.coins, 60); assert.equal(w.lifetime.coinsEarned, 100); assert.equal(w.lifetime.coinsSpent, 40);
    const h = (await s.call('GET', '/api/me/transactions', { token: a.token })).body.transactions;
    assert.deepEqual(h.map(t => t.amount), [-40, 100]);
    assert.match(h[0].txnId, /^txn_/);
    assert.equal(h[0].balanceAfter, 60);
    // the database itself refuses a negative balance
    assert.throws(() => s.app.db.prepare('UPDATE player_wallets SET coins = -1 WHERE player_id = ?').run(id));
  } finally { await s.close(); }
});

test('Booyah: only the trusted server can award, 500 diamonds, once per player per match', async () => {
  const s = await server();
  try {
    const a = await s.register('booyah_a'); const b = await s.register('booyah_b');
    const body = matchBody('match_000001', a, [b]);
    // forged victories: no key, wrong key, and a normal player token are all rejected
    assert.equal((await postMatch(s, body, null)).status, 401);
    assert.equal((await postMatch(s, body, 'nope')).status, 401);
    const viaPlayer = await s.call('POST', '/api/internal/match-results', { token: a.token, body });
    assert.equal(viaPlayer.status, 401);
    assert.equal((await wallet(s, a)).diamonds, 0);
    for (const p of ['/api/me/matches/match_000001/rewards']) assert.equal((await s.call('GET', p, { token: a.token })).status, 404);

    const ok = await postMatch(s, body);
    assert.equal(ok.status, 200);
    let w = await wallet(s, a);
    assert.equal(w.diamonds, 500);
    assert.equal(w.lifetime.diamondsEarned, 500); assert.equal(w.lifetime.diamondsPurchased, 0);
    assert.equal((await wallet(s, b)).diamonds, 0);

    const view = (await s.call('GET', '/api/me/matches/match_000001/rewards', { token: a.token })).body;
    assert.equal(view.booyah, true); assert.equal(view.booyahDiamonds, 500); assert.equal(view.status, 'granted');
    assert.ok(view.coins > 0); assert.ok(view.breakdown.some(l => l.label === 'Victory'));
    assert.match(view.notice, /no cash value/);
    const loser = (await s.call('GET', '/api/me/matches/match_000001/rewards', { token: b.token })).body;
    assert.equal(loser.booyah, false);

    // replay of the same match changes nothing
    const again = await postMatch(s, body);
    assert.equal(again.body.duplicate, true);
    w = await wallet(s, a);
    assert.equal(w.diamonds, 500);
    // ... even if the replay is crafted to name the other player as winner
    await postMatch(s, matchBody('match_000001', b, [a]));
    assert.equal((await wallet(s, b)).diamonds, 0);
    assert.equal(s.app.db.prepare('SELECT COUNT(*) c FROM match_rewards WHERE match_id = ?').get('match_000001').c, 2);
    // unique constraint exists at the database level
    assert.throws(() => s.app.db.prepare("INSERT INTO match_rewards(match_id, player_id, placement, kills, status, breakdown, created_at) SELECT match_id, player_id, 1, 0, 'granted', '[]', 0 FROM match_rewards LIMIT 1").run());
    const stats = (await s.call('GET', '/api/me', { token: a.token })).body.stats;
    assert.equal(stats.wins, 1); assert.equal(stats.matches, 1); assert.equal(stats.kills, 3);
  } finally { await s.close(); }
});

test('Booyah: simultaneous submissions of one match credit exactly once', async () => {
  const s = await server();
  try {
    const a = await s.register('race_a'); const b = await s.register('race_b');
    const results = await Promise.all(Array.from({ length: 8 }, () => postMatch(s, matchBody('match_race01', a, [b]))));
    assert.ok(results.every(r => r.status === 200));
    assert.equal(results.filter(r => !r.body.duplicate).length, 1);
    assert.equal((await wallet(s, a)).diamonds, 500);
  } finally { await s.close(); }
});

test('Booyah: configuration, eligibility rules, daily cap and review holds', async () => {
  const s = await server();
  try {
    const a = await s.register('rule_a'); const b = await s.register('rule_b');
    const set = (k, v) => s.app.db.prepare('UPDATE economy_settings SET value = ? WHERE key = ?').run(String(v), k);
    // ineligible: too few players, too short, wrong mode
    await postMatch(s, matchBody('m_small_01', a, [b], { playerCount: 4 }));
    await postMatch(s, matchBody('m_short_01', a, [b], { durationSeconds: 30 }));
    await postMatch(s, matchBody('m_train_01', a, [b], { mode: 'training' }));
    assert.equal((await wallet(s, a)).diamonds, 0);
    assert.equal((await s.call('GET', '/api/me/matches/m_small_01/rewards', { token: a.token })).body.status, 'denied');
    // configurable amount
    set('booyah_diamonds', 250);
    await postMatch(s, matchBody('m_cfg_0001', a, [b]));
    assert.equal((await wallet(s, a)).diamonds, 250);
    // switched off
    set('booyah_enabled', 0);
    await postMatch(s, matchBody('m_off_0001', a, [b]));
    assert.equal((await wallet(s, a)).diamonds, 250);
    set('booyah_enabled', 1); set('booyah_diamonds', 500); set('booyah_daily_cap', 2);
    await postMatch(s, matchBody('m_cap_0001', a, [b]));
    await postMatch(s, matchBody('m_cap_0002', a, [b]));
    assert.equal((await wallet(s, a)).diamonds, 250 + 500);
    assert.match((await s.call('GET', '/api/me/matches/m_cap_0002/rewards', { token: a.token })).body.reason, /limit/);
    // flagged accounts are held for review, not paid
    const c = await s.register('rule_c');
    s.app.db.prepare('INSERT INTO cheat_strikes(player_id, strikes, flagged, updated_at) SELECT id, 3, 1, 0 FROM players WHERE username = ?').run('rule_c');
    await postMatch(s, matchBody('m_flag_001', c, [b]));
    assert.equal((await wallet(s, c)).diamonds, 0);
    assert.equal((await s.call('GET', '/api/me/matches/m_flag_001/rewards', { token: c.token })).body.status, 'held');
    // malformed payloads
    assert.equal((await postMatch(s, { ...matchBody('bad id!', a), matchId: 'bad id!' })).status, 400);
    assert.equal((await postMatch(s, matchBody('m_unknown1', a, [], { players: [{ publicId: '1234567890', placement: 1, kills: 0 }] }))).status, 400);
    assert.equal((await postMatch(s, matchBody('m_kills_01', a, [], { players: [{ publicId: a.me.publicId, placement: 1, kills: 9999 }] }))).status, 400);
  } finally { await s.close(); }
});

test('match endpoint is disabled when no server key is configured', async () => {
  const s = await startServer();
  try {
    const a = await s.register('nokey_a');
    assert.equal((await postMatch(s, matchBody('m_nokey_01', a), '')).status, 503);
    assert.equal((await wallet(s, a)).diamonds, 0);
  } finally { await s.close(); }
});

test('missions: progress from trusted results, one claim per period, simultaneous claims pay once', async () => {
  const s = await server();
  try {
    const a = await s.register('mission_a'); const b = await s.register('mission_b');
    const list = async () => (await s.call('GET', '/api/me/missions', { token: a.token })).body.missions;
    const m = id => list().then(l => l.find(x => x.id === id));
    assert.equal((await s.call('POST', '/api/me/missions/daily_play_3/claim', { token: a.token })).status, 409);
    for (let i = 1; i <= 3; i++) await postMatch(s, matchBody('m_miss_00' + i, a, [b]));
    const mission = await m('daily_play_3');
    assert.equal(mission.claimable, true);
    const before = (await wallet(s, a)).coins;
    const claims = await Promise.all(Array.from({ length: 6 }, () => s.call('POST', '/api/me/missions/daily_play_3/claim', { token: a.token })));
    assert.equal(claims.filter(r => r.status === 200).length, 1);
    assert.equal(claims.filter(r => r.status === 409).length, 5);
    assert.equal((await wallet(s, a)).coins - before, 150);
    assert.equal((await m('daily_play_3')).claimed, true);
    assert.equal((await s.call('POST', '/api/me/missions/nope/claim', { token: a.token })).status, 404);
    // daily mission becomes claimable again the next UTC day, the achievement does not
    s.clock.t += 86400000;
    assert.equal((await m('daily_play_3')).claimed, false);
    assert.equal((await m('daily_play_3')).progress, 0);
    const ach = await m('ach_first_win');
    assert.equal(ach.claimable, true);
    assert.equal((await s.call('POST', '/api/me/missions/ach_first_win/claim', { token: a.token })).status, 200);
    s.clock.t += 5 * 86400000;
    assert.equal((await s.call('POST', '/api/me/missions/ach_first_win/claim', { token: a.token })).status, 409);
  } finally { await s.close(); }
});

test('free coins: match coin daily cap and capped training rewards', async () => {
  const s = await server();
  try {
    const a = await s.register('cap_a'); const b = await s.register('cap_b');
    s.app.db.prepare("UPDATE economy_settings SET value = '100' WHERE key = 'coin_daily_cap'").run();
    await postMatch(s, matchBody('m_coin_001', a, [b]));
    assert.equal((await wallet(s, a)).coins, 100); // 20 + 15 + 100 wanted, capped
    const view = (await s.call('GET', '/api/me/matches/m_coin_001/rewards', { token: a.token })).body;
    assert.equal(view.breakdown.reduce((n, l) => n + l.coins, 0), view.coins);
    await postMatch(s, matchBody('m_coin_002', a, [b]));
    assert.equal((await wallet(s, a)).coins, 100);
    s.app.db.prepare("UPDATE economy_settings SET value = '100000' WHERE key = 'coin_daily_cap'").run();
    const t = [];
    for (let i = 0; i < 5; i++) t.push((await s.call('POST', '/api/me/training-result', { token: a.token, body: { score: 500 } })).body);
    assert.deepEqual(t.map(x => x.coins), [10, 10, 10, 0, 0]);
    assert.equal(t[4].limitReached, true);
    assert.equal((await s.call('POST', '/api/me/training-result', { token: a.token, body: { score: -5 } })).status, 400);
    assert.equal(((await s.call('GET', '/api/me/missions', { token: a.token })).body.missions.find(x => x.id === 'daily_training')).claimable, true);
  } finally { await s.close(); }
});

test('store: packages are configurable server data and the client price is never used', async () => {
  const s = await server();
  try {
    const a = await s.register('pkg_a');
    const pk = (await s.call('GET', '/api/store/packages', { token: a.token })).body;
    assert.deepEqual(pk.packages.map(p => p.diamonds), [100, 310, 520, 1060, 2180, 5600]);
    assert.equal(pk.testPayments, true);
    s.app.db.prepare("UPDATE diamond_packages SET diamonds = 120 WHERE id = 'diamonds_100'").run();
    assert.equal((await s.call('GET', '/api/store/packages', { token: a.token })).body.packages[0].diamonds, 120);
    const p = await s.call('POST', '/api/store/purchases', { token: a.token, body: { packageId: 'diamonds_100', provider: 'test', diamonds: 99999, priceMinor: 1 } });
    assert.equal(p.status, 201);
    assert.equal(p.body.payment.diamonds, 120);
    assert.equal((await s.call('POST', '/api/store/purchases', { token: a.token, body: { packageId: 'nope', provider: 'test' } })).status, 404);
  } finally { await s.close(); }
});

test('payments (test provider): pending -> succeeded credits once, cancelled/failed never credit', async () => {
  const s = await server();
  try {
    const a = await s.register('pay_a');
    const create = async id => (await s.call('POST', '/api/store/purchases', { token: a.token, body: { packageId: id, provider: 'test' } })).body.payment;
    const p1 = await create('diamonds_310');
    assert.equal(p1.status, 'pending');
    assert.equal((await wallet(s, a)).diamonds, 0); // creating a purchase credits nothing
    const done = await Promise.all([1, 2, 3, 4].map(() => s.call('POST', `/api/store/purchases/${p1.id}/test-confirm`, { token: a.token, body: { outcome: 'success' } })));
    assert.ok(done.every(r => r.status === 200 && r.body.payment.status === 'succeeded'));
    let w = await wallet(s, a);
    assert.equal(w.diamonds, 310); assert.equal(w.lifetime.diamondsPurchased, 310); assert.equal(w.lifetime.diamondsEarned, 0);
    assert.equal((await s.call('POST', `/api/store/purchases/${p1.id}/test-confirm`, { token: a.token, body: { outcome: 'cancel' } })).status, 409);
    const p2 = await create('diamonds_100');
    assert.equal((await s.call('POST', `/api/store/purchases/${p2.id}/test-confirm`, { token: a.token, body: { outcome: 'cancel' } })).body.payment.status, 'cancelled');
    const p3 = await create('diamonds_100');
    const f = (await s.call('POST', `/api/store/purchases/${p3.id}/test-confirm`, { token: a.token, body: { outcome: 'fail' } })).body.payment;
    assert.equal(f.status, 'failed'); assert.ok(f.error);
    assert.equal((await wallet(s, a)).diamonds, 310);
    const hist = (await s.call('GET', '/api/store/purchases', { token: a.token })).body.purchases;
    assert.deepEqual(hist.map(h => h.status).sort(), ['cancelled', 'failed', 'succeeded']);
    // another player cannot confirm my payment
    const b = await s.register('pay_b');
    const p4 = await create('diamonds_100');
    assert.equal((await s.call('POST', `/api/store/purchases/${p4.id}/test-confirm`, { token: b.token, body: { outcome: 'success' } })).status, 404);
    assert.equal((await s.call('POST', '/api/store/restore', { token: a.token })).body.pending.length, 1);
  } finally { await s.close(); }
});

test('payments: test mode is off by default and unconfigured providers are refused', async () => {
  const s = await startServer();
  try {
    const a = await s.register('pay_off');
    assert.equal((await s.call('POST', '/api/store/purchases', { token: a.token, body: { packageId: 'diamonds_100', provider: 'test' } })).status, 403);
    assert.equal((await s.call('POST', '/api/store/purchases', { token: a.token, body: { packageId: 'diamonds_100', provider: 'apple' } })).status, 501);
    assert.equal((await s.call('POST', '/api/store/purchases', { token: a.token, body: { packageId: 'diamonds_100', provider: 'google' } })).status, 501);
    assert.equal((await wallet(s, a)).diamonds, 0);
  } finally { await s.close(); }
});

test('payments: signed webhooks credit once; forged, replayed and cross-account receipts do not', async () => {
  const s = await server();
  try {
    const a = await s.register('hook_a'); const b = await s.register('hook_b');
    const create = async (p, id) => (await s.call('POST', '/api/store/purchases', { token: p.token, body: { packageId: id, provider: 'test' } })).body.payment;
    const p1 = await create(a, 'diamonds_520');
    const evt = { paymentId: p1.id, transactionId: 'prov-100', status: 'succeeded' };
    assert.equal((await signed(s, '/api/payments/webhook/test', 'wrong-secret', evt)).status, 401);
    const raw = await fetch(s.base + '/api/payments/webhook/test', { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify(evt) });
    assert.equal(raw.status, 401);
    assert.equal((await wallet(s, a)).diamonds, 0);
    assert.equal((await signed(s, '/api/payments/webhook/test', WEBHOOK, evt)).body.duplicate, false);
    assert.equal((await signed(s, '/api/payments/webhook/test', WEBHOOK, evt)).body.duplicate, true);
    const rep = await Promise.all([1, 2, 3].map(() => signed(s, '/api/payments/webhook/test', WEBHOOK, evt)));
    assert.ok(rep.every(r => r.status === 200));
    assert.equal((await wallet(s, a)).diamonds, 520);
    // the same provider transaction cannot be redeemed on a second payment
    const p2 = await create(b, 'diamonds_520');
    const reuse = await signed(s, '/api/payments/webhook/test', WEBHOOK, { paymentId: p2.id, transactionId: 'prov-100', status: 'succeeded' });
    assert.equal(reuse.status, 409);
    assert.equal((await wallet(s, b)).diamonds, 0);
    // refund claws diamonds back (never below zero) and is idempotent
    s.app.economy.apply(s.app.db.prepare('SELECT id FROM players WHERE username=?').get('hook_a').id, 'diamonds', -500, { kind: 'test' });
    await signed(s, '/api/payments/webhook/test', WEBHOOK, { paymentId: p1.id, status: 'refunded' });
    await signed(s, '/api/payments/webhook/test', WEBHOOK, { paymentId: p1.id, status: 'refunded' });
    assert.equal((await wallet(s, a)).diamonds, 0);
    assert.equal(s.app.db.prepare("SELECT status FROM payment_transactions WHERE id = ?").get(p1.id).status, 'refunded');
    // webhook for a provider with no secret is disabled
    assert.equal((await signed(s, '/api/payments/webhook/google', WEBHOOK, evt)).status, 503);
  } finally { await s.close(); }
});

test('payments: injected Apple verifier — valid, forged, wrong product, replay', async () => {
  const seen = [];
  const verifiers = {
    apple: async ({ receipt }) => {
      seen.push(receipt);
      if (receipt === 'network-down') throw new Error('down');
      if (receipt.startsWith('valid:')) { const [, txn, product] = receipt.split(':'); return { valid: true, transactionId: txn, productId: product }; }
      return { valid: false };
    },
  };
  const s = await server({ verifiers });
  try {
    const a = await s.register('apple_a'); const b = await s.register('apple_b');
    const create = async (p, id = 'diamonds_100') => (await s.call('POST', '/api/store/purchases', { token: p.token, body: { packageId: id, provider: 'apple' } })).body.payment;
    const verify = (p, pay, receipt) => s.call('POST', `/api/store/purchases/${pay.id}/verify`, { token: p.token, body: { receipt } });
    const p1 = await create(a);
    assert.equal((await verify(a, p1, 'forged-receipt')).status, 402);
    assert.equal((await wallet(s, a)).diamonds, 0);
    const p2 = await create(a);
    assert.equal((await verify(a, p2, 'valid:T1:noxy.diamonds.310')).status, 402); // wrong product
    const p3 = await create(a);
    assert.equal((await verify(a, p3, 'network-down')).status, 502);
    assert.equal(s.app.db.prepare('SELECT status FROM payment_transactions WHERE id = ?').get(p3.id).status, 'pending'); // network failure keeps it retryable
    const ok = await verify(a, p3, 'valid:T2:noxy.diamonds.100');
    assert.equal(ok.status, 200); assert.equal(ok.body.wallet.diamonds, 100);
    assert.equal((await verify(a, p3, 'valid:T2:noxy.diamonds.100')).body.duplicate, true);
    assert.equal((await wallet(s, a)).diamonds, 100);
    // someone else replaying the same receipt
    const pb = await create(b);
    assert.equal((await verify(b, pb, 'valid:T2:noxy.diamonds.100')).status, 409);
    assert.equal((await wallet(s, b)).diamonds, 0);
    // test confirm cannot be used on a real-provider payment
    assert.equal((await s.call('POST', `/api/store/purchases/${pb.id}/test-confirm`, { token: b.token, body: { outcome: 'success' } })).status, 400);
  } finally { await s.close(); }
});

test('rewarded ads: disabled without a secret; verified, deduplicated and capped when enabled', async () => {
  const off = await startServer();
  try {
    const a = await off.register('ads_off');
    assert.equal((await off.call('GET', '/api/ads/status', { token: a.token })).body.enabled, false);
    assert.equal((await signed(off, '/api/ads/reward-callback', ADS, { publicId: a.me.publicId, provider: 'x', transactionId: 'abcd' })).status, 503);
  } finally { await off.close(); }
  const s = await server();
  try {
    const a = await s.register('ads_on');
    assert.equal((await s.call('GET', '/api/ads/status', { token: a.token })).body.enabled, true);
    const cb = (txn, secret = ADS) => signed(s, '/api/ads/reward-callback', secret, { publicId: a.me.publicId, provider: 'testnet', transactionId: txn });
    assert.equal((await cb('txn-0001', 'bad')).status, 401);
    assert.equal((await wallet(s, a)).coins, 0);
    assert.equal((await cb('txn-0001')).body.coins, 50);
    assert.equal((await cb('txn-0001')).body.duplicate, true);
    assert.equal((await wallet(s, a)).coins, 50);
    for (let i = 2; i <= 5; i++) assert.equal((await cb('txn-000' + i)).status, 200);
    assert.equal((await cb('txn-0006')).status, 429);
    assert.equal((await wallet(s, a)).coins, 250);
  } finally { await s.close(); }
});

test('cosmetics: server-priced purchase with coins or diamonds, atomic, no double charge', async () => {
  const s = await server();
  try {
    const a = await s.register('shop_a');
    const id = s.app.db.prepare('SELECT id FROM players WHERE username=?').get('shop_a').id;
    const buy = (item, currency, who = a) => s.call('POST', '/api/shop/purchase', { token: who.token, body: { itemId: item, currency, price: 1 } });
    const list = (await s.call('GET', '/api/store/cosmetics', { token: a.token })).body.items;
    const desert = list.find(i => i.itemId === 'outfit_desert');
    assert.equal(desert.coinPrice, 800); assert.ok(desert.diamondPrice > 0); assert.equal(desert.owned, false);
    assert.equal((await buy('outfit_desert', 'coins')).status, 402);
    assert.equal((await buy('outfit_desert', 'gold')).status, 400);
    assert.equal((await buy('outfit_nightops', 'coins')).status, 404); // free starter, not for sale
    assert.equal((await buy('does_not_exist', 'coins')).status, 404);
    s.app.economy.apply(id, 'coins', 1000, { kind: 'test' });
    const results = await Promise.all([1, 2, 3, 4, 5].map(() => buy('outfit_desert', 'coins')));
    assert.equal(results.filter(r => r.status === 200).length, 1);
    assert.equal(results.filter(r => r.status === 409).length, 4);
    assert.equal((await wallet(s, a)).coins, 200);
    assert.ok((await s.call('GET', '/api/me/inventory', { token: a.token })).body.owned.includes('outfit_desert'));
    // premium currency needs diamonds that came from confirmed payments/rewards
    assert.equal((await buy('outfit_crimson', 'diamonds')).status, 402);
    s.app.economy.apply(id, 'diamonds', 1000, { kind: 'test' });
    const crimson = list.find(i => i.itemId === 'outfit_crimson');
    const r = await buy('outfit_crimson', 'diamonds');
    assert.equal(r.status, 200); assert.equal(r.body.price, crimson.diamondPrice);
    const w = await wallet(s, a);
    assert.equal(w.diamonds, 1000 - crimson.diamondPrice); assert.equal(w.lifetime.diamondsSpent, crimson.diamondPrice);
    // equip works after buying, and the failed buys left no ledger rows
    assert.equal((await s.call('PUT', '/api/me/loadout', { token: a.token, body: { loadout: { outfit: 'outfit_crimson' } } })).status, 200);
    const kinds = (await s.call('GET', '/api/me/transactions', { token: a.token })).body.transactions.filter(t => t.kind === 'cosmetic');
    assert.equal(kinds.length, 2);
  } finally { await s.close(); }
});

test('server restart: wallet, ledger, rewards and payments persist and stay idempotent', async () => {
  const file = path.join(os.tmpdir(), `noxy-eco-${process.pid}-${Date.now()}.sqlite`);
  let a;
  let s = await server({}); // in-memory first to build a baseline (no-op), then file-backed below
  await s.close();
  s = await startServer({ dbPath: file, config: economy() });
  try {
    a = await s.register('restart_a'); const b = await s.register('restart_b');
    await postMatch(s, matchBody('m_restart_1', a, [b]));
    const pay = (await s.call('POST', '/api/store/purchases', { token: a.token, body: { packageId: 'diamonds_100', provider: 'test' } })).body.payment;
    await s.call('POST', `/api/store/purchases/${pay.id}/test-confirm`, { token: a.token, body: { outcome: 'success' } });
    assert.equal((await wallet(s, a)).diamonds, 600);
  } finally { await s.close(); }
  const s2 = await startServer({ dbPath: file, config: economy() });
  try {
    const login = await s2.call('POST', '/api/auth/login', { body: { username: 'restart_a', password: 'password123' } });
    const tok = { token: login.body.token };
    assert.equal((await wallet(s2, tok)).diamonds, 600);
    assert.equal((await postMatch(s2, matchBody('m_restart_1', a, []))).body.duplicate, true);
    assert.equal((await wallet(s2, tok)).diamonds, 600);
    assert.equal((await s2.call('GET', '/api/store/purchases', tok)).body.purchases[0].status, 'succeeded');
    assert.equal((await s2.call('GET', '/api/me/transactions', tok)).body.transactions.length >= 3, true);
  } finally { await s2.close(); for (const ext of ['', '-wal', '-shm']) fs.rmSync(file + ext, { force: true }); }
});
