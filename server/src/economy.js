'use strict';
// Server-authoritative virtual economy for the Noxy dev backend.
// Balances only change through apply() (wallet + ledger row in one transaction). Clients can never
// write a balance. Rewards come from trusted server-to-server calls; payments from verified provider
// receipts / signed notifications. Nothing here is production-ready until real providers are wired
// and tested (see docs/ECONOMY_SETUP.md).
const crypto = require('node:crypto');

const DAY_MS = 86400000;

const DEFAULT_SETTINGS = {
  booyah_enabled: '1',
  booyah_diamonds: '500',
  booyah_min_players: '10',
  booyah_min_match_seconds: '120',
  booyah_daily_cap: '3',
  booyah_eligible_modes: 'battle_royale,lone_wolf',
  match_participation_coins: '20',
  match_kill_coins: '5',
  match_win_coins: '100',
  coin_daily_cap: '1500',
  training_reward_coins: '10',
  training_daily_claims: '3',
  ad_reward_coins: '50',
  ad_daily_cap: '5',
  diamond_cosmetic_ratio: '10',
};

const PACKAGES = [
  // Reference prices only. Mobile builds must show the price the store (Apple / Google) reports.
  ['diamonds_100', 100, 0, 99, 'noxy.diamonds.100'],
  ['diamonds_310', 310, 0, 299, 'noxy.diamonds.310'],
  ['diamonds_520', 520, 0, 499, 'noxy.diamonds.520'],
  ['diamonds_1060', 1060, 0, 999, 'noxy.diamonds.1060'],
  ['diamonds_2180', 2180, 0, 1999, 'noxy.diamonds.2180'],
  ['diamonds_5600', 5600, 0, 4999, 'noxy.diamonds.5600'],
];

const MISSIONS = [
  // id, title, kind, metric, target, coins
  ['daily_play_3', 'Play 3 matches', 'daily', 'matches_played', 3, 150],
  ['daily_kills_5', 'Get 5 eliminations', 'daily', 'kills', 5, 100],
  ['daily_training', 'Finish a Training drill', 'daily', 'training_drills', 1, 50],
  ['weekly_wins_3', 'Win 3 matches', 'weekly', 'wins', 3, 600],
  ['weekly_play_20', 'Play 20 matches', 'weekly', 'matches_played', 20, 500],
  ['ach_first_win', 'Achievement: first victory', 'achievement', 'wins', 1, 300],
];

const SCHEMA = `
CREATE TABLE IF NOT EXISTS economy_settings (key TEXT PRIMARY KEY, value TEXT NOT NULL);

CREATE TABLE IF NOT EXISTS player_wallets (
  player_id INTEGER PRIMARY KEY REFERENCES players(id) ON DELETE CASCADE,
  coins INTEGER NOT NULL DEFAULT 0 CHECK (coins >= 0),
  diamonds INTEGER NOT NULL DEFAULT 0 CHECK (diamonds >= 0),
  lifetime_diamonds_earned INTEGER NOT NULL DEFAULT 0,
  lifetime_diamonds_purchased INTEGER NOT NULL DEFAULT 0,
  lifetime_diamonds_spent INTEGER NOT NULL DEFAULT 0,
  lifetime_coins_earned INTEGER NOT NULL DEFAULT 0,
  lifetime_coins_spent INTEGER NOT NULL DEFAULT 0,
  updated_at INTEGER NOT NULL
);

CREATE TABLE IF NOT EXISTS currency_transactions (
  id INTEGER PRIMARY KEY AUTOINCREMENT,
  txn_id TEXT NOT NULL UNIQUE,
  player_id INTEGER NOT NULL REFERENCES players(id) ON DELETE CASCADE,
  currency TEXT NOT NULL CHECK (currency IN ('coins','diamonds')),
  amount INTEGER NOT NULL CHECK (amount <> 0),
  balance_after INTEGER NOT NULL CHECK (balance_after >= 0),
  kind TEXT NOT NULL,
  ref_type TEXT,
  ref_id TEXT,
  idempotency_key TEXT UNIQUE,
  created_at INTEGER NOT NULL
);
CREATE INDEX IF NOT EXISTS idx_ctx_player ON currency_transactions(player_id, id DESC);

CREATE TABLE IF NOT EXISTS diamond_packages (
  id TEXT PRIMARY KEY,
  diamonds INTEGER NOT NULL CHECK (diamonds > 0),
  bonus_diamonds INTEGER NOT NULL DEFAULT 0 CHECK (bonus_diamonds >= 0),
  price_minor INTEGER NOT NULL CHECK (price_minor > 0),
  currency TEXT NOT NULL DEFAULT 'USD',
  apple_product_id TEXT,
  google_product_id TEXT,
  active INTEGER NOT NULL DEFAULT 1,
  sort_order INTEGER NOT NULL DEFAULT 0
);

CREATE TABLE IF NOT EXISTS payment_transactions (
  id TEXT PRIMARY KEY,
  player_id INTEGER NOT NULL REFERENCES players(id) ON DELETE CASCADE,
  package_id TEXT NOT NULL REFERENCES diamond_packages(id),
  provider TEXT NOT NULL CHECK (provider IN ('test','apple','google')),
  provider_txn_id TEXT,
  status TEXT NOT NULL CHECK (status IN ('pending','succeeded','cancelled','failed','refunded')),
  diamonds INTEGER NOT NULL,
  price_minor INTEGER NOT NULL,
  currency TEXT NOT NULL,
  error TEXT,
  created_at INTEGER NOT NULL,
  updated_at INTEGER NOT NULL,
  UNIQUE (provider, provider_txn_id)
);
CREATE INDEX IF NOT EXISTS idx_pay_player ON payment_transactions(player_id, created_at DESC);

CREATE TABLE IF NOT EXISTS matches_processed (
  match_id TEXT PRIMARY KEY,
  mode TEXT NOT NULL,
  player_count INTEGER NOT NULL,
  duration_seconds INTEGER NOT NULL,
  processed_at INTEGER NOT NULL
);

CREATE TABLE IF NOT EXISTS match_rewards (
  id INTEGER PRIMARY KEY AUTOINCREMENT,
  match_id TEXT NOT NULL,
  player_id INTEGER NOT NULL REFERENCES players(id) ON DELETE CASCADE,
  placement INTEGER NOT NULL,
  kills INTEGER NOT NULL,
  coins INTEGER NOT NULL DEFAULT 0,
  booyah_diamonds INTEGER NOT NULL DEFAULT 0,
  status TEXT NOT NULL CHECK (status IN ('granted','held','denied','none')),
  reason TEXT,
  breakdown TEXT NOT NULL,
  coin_txn_id TEXT,
  diamond_txn_id TEXT,
  created_at INTEGER NOT NULL,
  UNIQUE (match_id, player_id)
);

CREATE TABLE IF NOT EXISTS daily_missions (
  id TEXT PRIMARY KEY,
  title TEXT NOT NULL,
  kind TEXT NOT NULL CHECK (kind IN ('daily','weekly','achievement')),
  metric TEXT NOT NULL,
  target INTEGER NOT NULL CHECK (target > 0),
  reward_coins INTEGER NOT NULL CHECK (reward_coins >= 0),
  active INTEGER NOT NULL DEFAULT 1
);

CREATE TABLE IF NOT EXISTS player_mission_progress (
  player_id INTEGER NOT NULL REFERENCES players(id) ON DELETE CASCADE,
  mission_id TEXT NOT NULL REFERENCES daily_missions(id),
  period_key TEXT NOT NULL,
  progress INTEGER NOT NULL DEFAULT 0,
  PRIMARY KEY (player_id, mission_id, period_key)
);

CREATE TABLE IF NOT EXISTS reward_claims (
  id INTEGER PRIMARY KEY AUTOINCREMENT,
  player_id INTEGER NOT NULL REFERENCES players(id) ON DELETE CASCADE,
  claim_key TEXT NOT NULL,
  source TEXT NOT NULL,
  currency TEXT NOT NULL,
  amount INTEGER NOT NULL,
  txn_id TEXT,
  created_at INTEGER NOT NULL,
  UNIQUE (player_id, claim_key)
);

CREATE TABLE IF NOT EXISTS ad_reward_events (
  id INTEGER PRIMARY KEY AUTOINCREMENT,
  provider TEXT NOT NULL,
  provider_txn_id TEXT NOT NULL,
  player_id INTEGER NOT NULL REFERENCES players(id) ON DELETE CASCADE,
  coins INTEGER NOT NULL,
  created_at INTEGER NOT NULL,
  UNIQUE (provider, provider_txn_id)
);

CREATE TABLE IF NOT EXISTS cosmetic_catalog (
  item_id TEXT PRIMARY KEY,
  slot TEXT NOT NULL,
  name TEXT NOT NULL,
  rarity TEXT NOT NULL,
  coin_price INTEGER CHECK (coin_price IS NULL OR coin_price > 0),
  diamond_price INTEGER CHECK (diamond_price IS NULL OR diamond_price > 0),
  available INTEGER NOT NULL DEFAULT 1
);

CREATE VIEW IF NOT EXISTS player_cosmetics AS
  SELECT player_id, item_id, acquired_at FROM player_inventory;
`;

function create(ctx) {
  const { db, q, tx, fail, now, config, catalog, requireString, rateLimit, playerByPublicId, notify } = ctx;
  const eco = config.economy;

  db.exec(SCHEMA);
  for (const [k, v] of Object.entries(DEFAULT_SETTINGS)) q.run('INSERT OR IGNORE INTO economy_settings(key, value) VALUES (?,?)', k, v);
  PACKAGES.forEach(([id, d, b, p, prod], i) => q.run(
    'INSERT OR IGNORE INTO diamond_packages(id, diamonds, bonus_diamonds, price_minor, currency, apple_product_id, google_product_id, sort_order) VALUES (?,?,?,?,?,?,?,?)',
    id, d, b, p, 'USD', prod, prod, i));
  for (const [id, title, kind, metric, target, coins] of MISSIONS) {
    q.run('INSERT OR IGNORE INTO daily_missions(id, title, kind, metric, target, reward_coins) VALUES (?,?,?,?,?,?)', id, title, kind, metric, target, coins);
  }
  const ratio = () => Math.max(1, num('diamond_cosmetic_ratio'));
  for (const it of catalog.items) {
    if (!(it.price > 0)) continue;
    q.run('INSERT OR IGNORE INTO cosmetic_catalog(item_id, slot, name, rarity, coin_price, diamond_price) VALUES (?,?,?,?,?,?)',
      it.id, it.slot, it.name, it.rarity, it.price, Math.max(10, Math.round(it.price / ratio())));
  }

  const inTx = fn => (db.isTransaction ? fn() : tx(fn));
  function setting(key) { const r = q.one('SELECT value FROM economy_settings WHERE key = ?', key); return r ? r.value : DEFAULT_SETTINGS[key]; }
  function num(key) { const n = Number(setting(key)); return Number.isFinite(n) ? n : Number(DEFAULT_SETTINGS[key]); }
  const dayStart = () => Math.floor(now() / DAY_MS) * DAY_MS;
  const periodKey = kind => {
    const d = Math.floor(now() / DAY_MS);
    if (kind === 'daily') return 'd' + d;
    if (kind === 'weekly') return 'w' + Math.floor((d + 3) / 7); // weeks start on Monday UTC (epoch day 0 was a Thursday)
    return 'once';
  };
  const newId = prefix => prefix + crypto.randomBytes(9).toString('hex');
  const safeEqual = (a, b) => {
    const x = Buffer.from(String(a)); const y = Buffer.from(String(b));
    return x.length === y.length && crypto.timingSafeEqual(x, y);
  };
  // Shared by every trusted game-server endpoint (match results, clan wars, map events, fair play).
  function requireServerKey(req, ip) {
    rateLimit('internal', ip);
    if (!eco.matchServerKey) fail(503, 'not_configured', 'Game-server endpoints are disabled (no server key configured).');
    if (!safeEqual(req.headers['x-noxy-server-key'] || '', eco.matchServerKey)) fail(401, 'unauthorized', 'Invalid server key.');
  }
  const hmac = (secret, raw) => crypto.createHmac('sha256', secret).update(raw).digest('hex');

  // ---------- wallet ----------
  function ensureWallet(playerId) {
    q.run('INSERT OR IGNORE INTO player_wallets(player_id, updated_at) VALUES (?,?)', playerId, now());
    return q.one('SELECT * FROM player_wallets WHERE player_id = ?', playerId);
  }
  function walletView(playerId) {
    const w = ensureWallet(playerId);
    return {
      coins: w.coins, diamonds: w.diamonds,
      lifetime: {
        diamondsEarned: w.lifetime_diamonds_earned, diamondsPurchased: w.lifetime_diamonds_purchased, diamondsSpent: w.lifetime_diamonds_spent,
        coinsEarned: w.lifetime_coins_earned, coinsSpent: w.lifetime_coins_spent,
      },
    };
  }

  // The only function allowed to change a balance. Safe to call inside or outside a transaction.
  // With a key, repeating the call returns the original ledger row instead of applying it again.
  function apply(playerId, currency, amount, { kind, refType = null, refId = null, key = null, purchased = false }) {
    if (currency !== 'coins' && currency !== 'diamonds') throw new Error('bad currency');
    if (!Number.isInteger(amount) || amount === 0) throw new Error('bad amount');
    return inTx(() => {
      if (key) {
        const prev = q.one('SELECT * FROM currency_transactions WHERE idempotency_key = ?', key);
        if (prev) return { duplicate: true, txnId: prev.txn_id, balance: prev.balance_after, amount: prev.amount };
      }
      const w = ensureWallet(playerId);
      const balance = w[currency] + amount;
      if (balance < 0) fail(402, 'insufficient_funds', `Not enough ${currency}.`);
      const earned = amount > 0 ? amount : 0;
      const spent = amount < 0 ? -amount : 0;
      if (currency === 'coins') {
        q.run('UPDATE player_wallets SET coins = ?, lifetime_coins_earned = lifetime_coins_earned + ?, lifetime_coins_spent = lifetime_coins_spent + ?, updated_at = ? WHERE player_id = ?',
          balance, earned, spent, now(), playerId);
      } else {
        q.run('UPDATE player_wallets SET diamonds = ?, lifetime_diamonds_earned = lifetime_diamonds_earned + ?, lifetime_diamonds_purchased = lifetime_diamonds_purchased + ?, lifetime_diamonds_spent = lifetime_diamonds_spent + ?, updated_at = ? WHERE player_id = ?',
          balance, purchased ? 0 : earned, purchased ? earned : 0, spent, now(), playerId);
      }
      const txnId = newId('txn_');
      q.run('INSERT INTO currency_transactions(txn_id, player_id, currency, amount, balance_after, kind, ref_type, ref_id, idempotency_key, created_at) VALUES (?,?,?,?,?,?,?,?,?,?)',
        txnId, playerId, currency, amount, balance, kind, refType, refId, key, now());
      return { duplicate: false, txnId, balance, amount };
    });
  }

  // ---------- missions ----------
  function addProgress(playerId, metric, delta) {
    if (!(delta > 0)) return;
    for (const m of q.all('SELECT * FROM daily_missions WHERE active = 1 AND metric = ?', metric)) {
      q.run(`INSERT INTO player_mission_progress(player_id, mission_id, period_key, progress) VALUES (?,?,?,?)
             ON CONFLICT(player_id, mission_id, period_key) DO UPDATE SET progress = progress + excluded.progress`,
        playerId, m.id, periodKey(m.kind), delta);
    }
  }
  function missionList(playerId) {
    return q.all('SELECT * FROM daily_missions WHERE active = 1 ORDER BY kind, id').map(m => {
      const pk = periodKey(m.kind);
      const p = q.one('SELECT progress FROM player_mission_progress WHERE player_id=? AND mission_id=? AND period_key=?', playerId, m.id, pk);
      const claimed = !!q.one('SELECT 1 x FROM reward_claims WHERE player_id=? AND claim_key=?', playerId, `mission:${m.id}:${pk}`);
      const progress = Math.min(p ? p.progress : 0, m.target);
      return { id: m.id, title: m.title, kind: m.kind, target: m.target, progress, rewardCoins: m.reward_coins, claimed, claimable: !claimed && progress >= m.target };
    });
  }
  function coinsEarnedToday(playerId) {
    return q.one("SELECT COALESCE(SUM(amount),0) s FROM currency_transactions WHERE player_id=? AND currency='coins' AND amount>0 AND kind IN ('match','training','ad') AND created_at >= ?",
      playerId, dayStart()).s;
  }
  const coinCapRemaining = playerId => Math.max(0, num('coin_daily_cap') - coinsEarnedToday(playerId));

  // ---------- match results (trusted server only) ----------
  const isFlagged = id => !!q.one('SELECT 1 x FROM cheat_strikes WHERE player_id = ? AND flagged = 1', id);

  function processMatch(body) {
    const matchId = requireString(body.matchId, 'matchId', 6, 64);
    if (!/^[A-Za-z0-9_-]+$/.test(matchId)) fail(400, 'invalid_matchId', 'matchId has invalid characters.');
    const mode = requireString(body.mode, 'mode', 2, 32);
    const playerCount = body.playerCount, duration = body.durationSeconds;
    if (!Number.isInteger(playerCount) || playerCount < 1 || playerCount > 200) fail(400, 'invalid_playerCount', 'playerCount must be 1-200.');
    if (!Number.isInteger(duration) || duration < 0 || duration > 7200) fail(400, 'invalid_duration', 'durationSeconds must be 0-7200.');
    if (!Array.isArray(body.players) || body.players.length < 1 || body.players.length > 200) fail(400, 'invalid_players', 'players must be a list of 1-200 entries.');
    const seen = new Set();
    const roster = body.players.map(e => {
      const p = e && playerByPublicId(e.publicId);
      if (!p) fail(400, 'unknown_player', 'Unknown player in results.');
      if (seen.has(p.id)) fail(400, 'duplicate_player', 'Player listed twice.');
      seen.add(p.id);
      if (!Number.isInteger(e.placement) || e.placement < 1 || e.placement > 200) fail(400, 'invalid_placement', 'placement must be 1-200.');
      if (!Number.isInteger(e.kills) || e.kills < 0 || e.kills > 100) fail(400, 'invalid_kills', 'kills must be 0-100.');
      return { p, placement: e.placement, kills: e.kills };
    });
    if (roster.length > playerCount) fail(400, 'invalid_players', 'More players listed than playerCount.');

    return inTx(() => {
      if (q.one('SELECT 1 x FROM matches_processed WHERE match_id = ?', matchId)) return { duplicate: true, matchId };
      q.run('INSERT INTO matches_processed(match_id, mode, player_count, duration_seconds, processed_at) VALUES (?,?,?,?,?)', matchId, mode, playerCount, duration, now());
      const eligibleModes = setting('booyah_eligible_modes').split(',').map(s => s.trim());
      const booyahOn = setting('booyah_enabled') === '1' && eligibleModes.includes(mode)
        && playerCount >= num('booyah_min_players') && duration >= num('booyah_min_match_seconds');
      const out = [];
      for (const { p, placement, kills } of roster) {
        const lines = [];
        let coins = 0;
        const part = num('match_participation_coins'); if (part > 0) lines.push({ label: 'Match played', coins: part });
        if (kills > 0 && num('match_kill_coins') > 0) lines.push({ label: `${kills} elimination${kills === 1 ? '' : 's'}`, coins: kills * num('match_kill_coins') });
        const won = placement === 1;
        if (won && num('match_win_coins') > 0) lines.push({ label: 'Victory', coins: num('match_win_coins') });
        const wanted = lines.reduce((s, l) => s + l.coins, 0);
        coins = Math.min(wanted, coinCapRemaining(p.id));
        if (coins < wanted) lines.push({ label: 'Daily free-coin limit reached', coins: coins - wanted });

        let coinTxn = null; let diamondTxn = null; let diamonds = 0; let status = 'none'; let reason = null;
        if (coins > 0) coinTxn = apply(p.id, 'coins', coins, { kind: 'match', refType: 'match', refId: matchId, key: `match:${matchId}:${p.id}:coins` }).txnId;
        if (won) {
          const amount = num('booyah_diamonds');
          if (!booyahOn || amount <= 0) { status = 'denied'; reason = 'Booyah reward is not active for this match.'; }
          else if (isFlagged(p.id)) { status = 'held'; reason = 'Held for review.'; }
          else if (q.one("SELECT COUNT(*) c FROM match_rewards WHERE player_id = ? AND status = 'granted' AND booyah_diamonds > 0 AND created_at >= ?", p.id, dayStart()).c >= num('booyah_daily_cap')) {
            status = 'denied'; reason = 'Daily Booyah reward limit reached.';
          } else {
            diamondTxn = apply(p.id, 'diamonds', amount, { kind: 'booyah', refType: 'match', refId: matchId, key: `booyah:${matchId}:${p.id}` }).txnId;
            diamonds = amount; status = 'granted';
          }
        }
        q.run('INSERT INTO match_rewards(match_id, player_id, placement, kills, coins, booyah_diamonds, status, reason, breakdown, coin_txn_id, diamond_txn_id, created_at) VALUES (?,?,?,?,?,?,?,?,?,?,?,?)',
          matchId, p.id, placement, kills, coins, diamonds, status, reason, JSON.stringify(lines), coinTxn, diamondTxn, now());

        q.run('UPDATE player_statistics SET matches = matches + 1, wins = wins + ?, top3 = top3 + ?, kills = kills + ? WHERE player_id = ?', won ? 1 : 0, placement <= 3 ? 1 : 0, kills, p.id);
        addProgress(p.id, 'matches_played', 1); addProgress(p.id, 'kills', kills); if (won) addProgress(p.id, 'wins', 1);
        if (diamonds > 0) notify(p.id, 'reward', 'Booyah!', `You received ${diamonds} diamonds (in-game promotional reward).`);
        out.push({ publicId: p.public_id, coins, booyahDiamonds: diamonds, status });
      }
      return { duplicate: false, matchId, results: out };
    });
  }

  function rewardView(r) {
    return {
      matchId: r.match_id, placement: r.placement, kills: r.kills, coins: r.coins, booyahDiamonds: r.booyah_diamonds,
      booyah: r.booyah_diamonds > 0, status: r.status, reason: r.reason, breakdown: JSON.parse(r.breakdown), createdAt: r.created_at,
      notice: 'The Booyah bonus is an in-game promotional reward subject to published eligibility rules. It has no cash value and cannot be withdrawn.',
    };
  }

  // ---------- payments ----------
  const packageView = p => ({
    id: p.id, diamonds: p.diamonds, bonusDiamonds: p.bonus_diamonds, referencePriceMinor: p.price_minor, referenceCurrency: p.currency,
    appleProductId: p.apple_product_id, googleProductId: p.google_product_id,
    priceNote: 'Reference price only. Mobile stores show the real localized price.',
  });
  const paymentView = r => ({ id: r.id, packageId: r.package_id, provider: r.provider, status: r.status, diamonds: r.diamonds, error: r.error, createdAt: r.created_at, updatedAt: r.updated_at });

  // Single place where a payment can become 'succeeded'. Idempotent on payment id and provider transaction id.
  function settle(paymentId, providerTxnId, status, error) {
    return inTx(() => {
      const pay = q.one('SELECT * FROM payment_transactions WHERE id = ?', paymentId);
      if (!pay) fail(404, 'payment_not_found', 'Unknown payment.');
      if (pay.status === 'succeeded' && status === 'succeeded') return { payment: pay, duplicate: true };
      if (pay.status === 'refunded') return { payment: pay, duplicate: true };
      if (pay.status !== 'pending' && !(pay.status === 'succeeded' && status === 'refunded')) {
        if (pay.status === status) return { payment: pay, duplicate: true };
        fail(409, 'payment_final', `Payment is already ${pay.status}.`);
      }
      if (status === 'succeeded') {
        if (!providerTxnId) fail(400, 'invalid_transaction', 'Provider transaction id required.');
        if (q.one('SELECT 1 x FROM payment_transactions WHERE provider = ? AND provider_txn_id = ? AND id <> ?', pay.provider, providerTxnId, pay.id)) {
          fail(409, 'receipt_already_used', 'This purchase was already redeemed on another payment.');
        }
        q.run('UPDATE payment_transactions SET status = ?, provider_txn_id = ?, updated_at = ? WHERE id = ?', 'succeeded', providerTxnId, now(), pay.id);
        apply(pay.player_id, 'diamonds', pay.diamonds, { kind: 'purchase', refType: 'payment', refId: pay.id, key: `payment:${pay.id}`, purchased: true });
      } else if (status === 'refunded') {
        q.run('UPDATE payment_transactions SET status = ?, updated_at = ? WHERE id = ?', 'refunded', now(), pay.id);
        const w = ensureWallet(pay.player_id);
        const claw = Math.min(w.diamonds, pay.diamonds);
        if (claw > 0) apply(pay.player_id, 'diamonds', -claw, { kind: 'refund', refType: 'payment', refId: pay.id, key: `refund:${pay.id}` });
      } else {
        q.run('UPDATE payment_transactions SET status = ?, error = ?, updated_at = ? WHERE id = ?', status, error || null, now(), pay.id);
      }
      return { payment: q.one('SELECT * FROM payment_transactions WHERE id = ?', pay.id), duplicate: false };
    });
  }

  function ownPayment(me, id) {
    const pay = q.one('SELECT * FROM payment_transactions WHERE id = ? AND player_id = ?', id, me.id);
    if (!pay) fail(404, 'payment_not_found', 'Unknown payment.');
    return pay;
  }

  // ---------- routes ----------
  function install(route) {
    route('GET', '/api/me/wallet', ({ me }) => ({ body: walletView(me.id) }));

    route('GET', '/api/me/transactions', ({ me, query }) => {
      const limit = Math.min(100, Math.max(1, Number.parseInt(query.get('limit') || '30', 10) || 30));
      const before = Number.parseInt(query.get('before') || '0', 10) || 0;
      const rows = q.all(`SELECT * FROM currency_transactions WHERE player_id = ? ${before > 0 ? 'AND id < ?' : ''} ORDER BY id DESC LIMIT ?`,
        ...(before > 0 ? [me.id, before, limit] : [me.id, limit]));
      return { body: { transactions: rows.map(r => ({ id: r.id, txnId: r.txn_id, currency: r.currency, amount: r.amount, balanceAfter: r.balance_after, kind: r.kind, refId: r.ref_id, createdAt: r.created_at })) } };
    });

    // --- missions / free coins ---
    route('GET', '/api/me/missions', ({ me }) => ({ body: { missions: missionList(me.id), coinsEarnedToday: coinsEarnedToday(me.id), coinDailyCap: num('coin_daily_cap') } }));

    route('POST', '/api/me/missions/:id/claim', ({ me, params }) => {
      rateLimit('claim', me.id);
      return inTx(() => {
        const m = q.one('SELECT * FROM daily_missions WHERE id = ? AND active = 1', params.id);
        if (!m) fail(404, 'mission_not_found', 'Unknown mission.');
        const pk = periodKey(m.kind);
        const p = q.one('SELECT progress FROM player_mission_progress WHERE player_id=? AND mission_id=? AND period_key=?', me.id, m.id, pk);
        if (!p || p.progress < m.target) fail(409, 'mission_incomplete', 'Mission is not complete yet.');
        const claimKey = `mission:${m.id}:${pk}`;
        if (q.one('SELECT 1 x FROM reward_claims WHERE player_id=? AND claim_key=?', me.id, claimKey)) fail(409, 'already_claimed', 'Reward already claimed.');
        const t = m.reward_coins > 0 ? apply(me.id, 'coins', m.reward_coins, { kind: 'mission', refType: 'mission', refId: claimKey, key: `claim:${me.id}:${claimKey}` }) : null;
        q.run('INSERT INTO reward_claims(player_id, claim_key, source, currency, amount, txn_id, created_at) VALUES (?,?,?,?,?,?,?)',
          me.id, claimKey, 'mission', 'coins', m.reward_coins, t ? t.txnId : null, now());
        return { body: { missionId: m.id, coins: m.reward_coins, wallet: walletView(me.id) } };
      });
    });

    // Training runs offline on the client, so the result cannot be verified: reward is tiny and capped.
    route('POST', '/api/me/training-result', ({ me, body }) => {
      rateLimit('claim', me.id);
      if (!Number.isInteger(body.score) || body.score < 1 || body.score > 1000000) fail(400, 'invalid_score', 'score must be a positive integer.');
      return inTx(() => {
        const today = q.one("SELECT COUNT(*) c FROM reward_claims WHERE player_id = ? AND source = 'training' AND created_at >= ?", me.id, dayStart()).c;
        const progressed = today < num('training_daily_claims');
        let coins = 0;
        if (progressed) {
          const amount = Math.min(num('training_reward_coins'), coinCapRemaining(me.id));
          const claimKey = `training:${me.id}:${today}:${dayStart()}`;
          if (amount > 0) coins = amount;
          if (coins > 0) apply(me.id, 'coins', coins, { kind: 'training', refType: 'training', refId: claimKey, key: `claim:${me.id}:${claimKey}` });
          q.run('INSERT INTO reward_claims(player_id, claim_key, source, currency, amount, created_at) VALUES (?,?,?,?,?,?)', me.id, claimKey, 'training', 'coins', coins, now());
          addProgress(me.id, 'training_drills', 1);
        }
        return { body: { coins, limitReached: !progressed, wallet: walletView(me.id) } };
      });
    });

    // --- trusted server: match results ---
    route('POST', '/api/internal/match-results', ({ req, body, ip }) => {
      rateLimit('internal', ip);
      if (!eco.matchServerKey) fail(503, 'not_configured', 'Match-result endpoint is disabled (no server key configured).');
      if (!safeEqual(req.headers['x-noxy-server-key'] || '', eco.matchServerKey)) fail(401, 'unauthorized', 'Invalid server key.');
      return { status: 200, body: processMatch(body) };
    }, { auth: false });

    route('GET', '/api/me/matches', ({ me }) => ({
      body: { matches: q.all('SELECT * FROM match_rewards WHERE player_id = ? ORDER BY id DESC LIMIT 20', me.id).map(rewardView) },
    }));
    route('GET', '/api/me/matches/:matchId/rewards', ({ me, params }) => {
      const r = q.one('SELECT * FROM match_rewards WHERE player_id = ? AND match_id = ?', me.id, params.matchId);
      if (!r) fail(404, 'match_not_found', 'No rewards recorded for that match.');
      return { body: { ...rewardView(r), wallet: walletView(me.id) } };
    });

    // --- store: diamond packages & payments ---
    route('GET', '/api/store/packages', () => ({
      body: { packages: q.all('SELECT * FROM diamond_packages WHERE active = 1 ORDER BY sort_order').map(packageView), testPayments: !!eco.testPayments, providers: Object.keys(eco.verifiers || {}) },
    }));

    route('POST', '/api/store/purchases', ({ me, body }) => {
      rateLimit('purchase', me.id);
      const pkg = q.one('SELECT * FROM diamond_packages WHERE id = ? AND active = 1', String(body.packageId || ''));
      if (!pkg) fail(404, 'package_not_found', 'Unknown diamond package.');
      const provider = String(body.provider || '');
      if (!['test', 'apple', 'google'].includes(provider)) fail(400, 'invalid_provider', 'Unknown payment provider.');
      if (provider === 'test' && !eco.testPayments) fail(403, 'test_payments_disabled', 'Test payments are disabled on this server.');
      if (provider !== 'test' && !(eco.verifiers && eco.verifiers[provider])) fail(501, 'provider_not_configured', `${provider} payments are not configured on this server.`);
      const pending = q.one("SELECT COUNT(*) c FROM payment_transactions WHERE player_id = ? AND status = 'pending'", me.id).c;
      if (pending >= 5) fail(429, 'too_many_pending', 'Too many unfinished purchases. Complete or cancel them first.');
      const id = newId('pay_');
      q.run('INSERT INTO payment_transactions(id, player_id, package_id, provider, status, diamonds, price_minor, currency, created_at, updated_at) VALUES (?,?,?,?,?,?,?,?,?,?)',
        id, me.id, pkg.id, provider, 'pending', pkg.diamonds + pkg.bonus_diamonds, pkg.price_minor, pkg.currency, now(), now());
      return { status: 201, body: { payment: paymentView(q.one('SELECT * FROM payment_transactions WHERE id = ?', id)) } };
    });

    // Explicit sandbox simulation. Only reachable when the operator enabled test payments; never in production.
    route('POST', '/api/store/purchases/:id/test-confirm', ({ me, params, body }) => {
      if (!eco.testPayments) fail(403, 'test_payments_disabled', 'Test payments are disabled on this server.');
      const pay = ownPayment(me, params.id);
      if (pay.provider !== 'test') fail(400, 'invalid_provider', 'Only test payments can be confirmed this way.');
      const outcome = String(body.outcome || 'success');
      if (!['success', 'cancel', 'fail'].includes(outcome)) fail(400, 'invalid_outcome', 'outcome must be success, cancel or fail.');
      const status = outcome === 'success' ? 'succeeded' : outcome === 'cancel' ? 'cancelled' : 'failed';
      const r = settle(pay.id, status === 'succeeded' ? 'test_' + pay.id : null, status, outcome === 'fail' ? 'Simulated payment failure.' : null);
      return { body: { payment: paymentView(r.payment), wallet: walletView(me.id) } };
    });

    // Apple / Google receipt verification through an injected verifier (needs real credentials to be useful).
    route('POST', '/api/store/purchases/:id/verify', async ({ me, params, body }) => {
      rateLimit('purchase', me.id);
      const pay = ownPayment(me, params.id);
      const verifier = eco.verifiers && eco.verifiers[pay.provider];
      if (!verifier) fail(501, 'provider_not_configured', `${pay.provider} verification is not configured on this server.`);
      if (typeof body.receipt !== 'string' || body.receipt.length < 8 || body.receipt.length > 16384) fail(400, 'invalid_receipt', 'receipt is required.');
      let verdict;
      try { verdict = await verifier({ receipt: body.receipt, payment: paymentView(pay) }); } catch { fail(502, 'provider_unavailable', 'Could not reach the payment provider. Try again later.'); }
      if (!verdict || !verdict.valid) { settle(pay.id, null, 'failed', 'Receipt rejected by provider.'); fail(402, 'receipt_invalid', 'The payment could not be verified.'); }
      const pkg = q.one('SELECT * FROM diamond_packages WHERE id = ?', pay.package_id);
      const expected = pay.provider === 'apple' ? pkg.apple_product_id : pkg.google_product_id;
      if (verdict.productId !== expected) { settle(pay.id, null, 'failed', 'Receipt is for a different product.'); fail(402, 'receipt_mismatch', 'Receipt does not match the selected package.'); }
      const r = settle(pay.id, String(verdict.transactionId), 'succeeded');
      return { body: { payment: paymentView(r.payment), duplicate: r.duplicate, wallet: walletView(me.id) } };
    });

    // Signed provider notifications (HMAC over the raw body). The only server-to-server settlement path.
    route('POST', '/api/payments/webhook/:provider', ({ req, params, body, ip }) => {
      rateLimit('internal', ip);
      const secret = (eco.webhookSecrets || {})[params.provider];
      if (!secret) fail(503, 'not_configured', 'Webhook is not configured for this provider.');
      const sig = String(req.headers['x-noxy-signature'] || '');
      if (!safeEqual(sig, hmac(secret, req.rawBody || ''))) fail(401, 'bad_signature', 'Invalid signature.');
      const pay = q.one('SELECT * FROM payment_transactions WHERE id = ? AND provider = ?', String(body.paymentId || ''), params.provider);
      if (!pay) fail(404, 'payment_not_found', 'Unknown payment.');
      if (!['succeeded', 'failed', 'cancelled', 'refunded'].includes(body.status)) fail(400, 'invalid_status', 'Unknown status.');
      const r = settle(pay.id, body.transactionId ? String(body.transactionId) : null, body.status, body.error ? String(body.error).slice(0, 200) : null);
      return { body: { ok: true, duplicate: r.duplicate, status: r.payment.status } };
    }, { auth: false });

    route('GET', '/api/store/purchases', ({ me }) => ({
      body: { purchases: q.all('SELECT * FROM payment_transactions WHERE player_id = ? ORDER BY created_at DESC, rowid DESC LIMIT 50', me.id).map(paymentView) },
    }));

    // Platform stores do not "restore" consumables, so this only lists unfinished purchases the client should re-verify.
    route('POST', '/api/store/restore', ({ me }) => ({
      body: { pending: q.all("SELECT * FROM payment_transactions WHERE player_id = ? AND status = 'pending' ORDER BY created_at", me.id).map(paymentView) },
    }));

    // --- rewarded ads (disabled until a provider secret is configured) ---
    route('GET', '/api/ads/status', ({ me }) => {
      const watched = q.one('SELECT COUNT(*) c FROM ad_reward_events WHERE player_id = ? AND created_at >= ?', me.id, dayStart()).c;
      return { body: { enabled: !!eco.adsSecret, rewardCoins: num('ad_reward_coins'), dailyCap: num('ad_daily_cap'), watchedToday: watched } };
    });
    route('POST', '/api/ads/reward-callback', ({ req, body, ip }) => {
      rateLimit('internal', ip);
      if (!eco.adsSecret) fail(503, 'ads_disabled', 'Rewarded ads are not configured.');
      if (!safeEqual(String(req.headers['x-noxy-signature'] || ''), hmac(eco.adsSecret, req.rawBody || ''))) fail(401, 'bad_signature', 'Invalid signature.');
      const p = playerByPublicId(body.publicId);
      if (!p) fail(404, 'player_not_found', 'Player not found.');
      const provider = requireString(body.provider, 'provider', 2, 32);
      const txn = requireString(body.transactionId, 'transactionId', 4, 128);
      return inTx(() => {
        if (q.one('SELECT 1 x FROM ad_reward_events WHERE provider = ? AND provider_txn_id = ?', provider, txn)) return { body: { ok: true, duplicate: true } };
        const watched = q.one('SELECT COUNT(*) c FROM ad_reward_events WHERE player_id = ? AND created_at >= ?', p.id, dayStart()).c;
        if (watched >= num('ad_daily_cap')) fail(429, 'ad_daily_cap', 'Daily rewarded-ad limit reached.');
        const coins = Math.min(num('ad_reward_coins'), coinCapRemaining(p.id));
        q.run('INSERT INTO ad_reward_events(provider, provider_txn_id, player_id, coins, created_at) VALUES (?,?,?,?,?)', provider, txn, p.id, coins, now());
        if (coins > 0) apply(p.id, 'coins', coins, { kind: 'ad', refType: 'ad', refId: txn, key: `ad:${provider}:${txn}` });
        return { body: { ok: true, duplicate: false, coins } };
      });
    }, { auth: false });

    // --- cosmetics ---
    route('GET', '/api/store/cosmetics', ({ me }) => {
      const owned = new Set(q.all('SELECT item_id FROM player_inventory WHERE player_id = ?', me.id).map(r => r.item_id));
      return {
        body: {
          items: q.all('SELECT * FROM cosmetic_catalog WHERE available = 1 ORDER BY slot, coin_price').map(c => ({
            itemId: c.item_id, slot: c.slot, name: c.name, rarity: c.rarity, coinPrice: c.coin_price, diamondPrice: c.diamond_price, owned: owned.has(c.item_id),
          })),
        },
      };
    });

    route('POST', '/api/shop/purchase', ({ me, body }) => {
      rateLimit('purchase', me.id);
      const itemId = String(body.itemId || '');
      const currency = String(body.currency || '');
      if (currency !== 'coins' && currency !== 'diamonds') fail(400, 'invalid_currency', 'currency must be coins or diamonds.');
      return inTx(() => {
        const item = q.one('SELECT * FROM cosmetic_catalog WHERE item_id = ? AND available = 1', itemId);
        if (!item) fail(404, 'item_not_for_sale', 'That item is not for sale.');
        if (q.one('SELECT 1 x FROM player_inventory WHERE player_id = ? AND item_id = ?', me.id, itemId)) fail(409, 'already_owned', 'You already own this item.');
        const price = currency === 'coins' ? item.coin_price : item.diamond_price; // price always comes from the server
        if (!price) fail(400, 'not_sold_for_currency', `This item cannot be bought with ${currency}.`);
        apply(me.id, currency, -price, { kind: 'cosmetic', refType: 'item', refId: itemId, key: `cosmetic:${me.id}:${itemId}` });
        q.run('INSERT INTO player_inventory(player_id, item_id, acquired_at) VALUES (?,?,?)', me.id, itemId, now());
        return { body: { itemId, currency, price, wallet: walletView(me.id) } };
      });
    });
  }

  return { install, requireServerKey, apply, walletView, ensureWallet, settle, processMatch, setting };
}

module.exports = { create, DEFAULT_SETTINGS };
