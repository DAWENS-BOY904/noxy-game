'use strict';
// Cosmetic companions. Pure cosmetics: ownership and the equipped selection are stored here, prices are decided
// server-side and charged through the economy ledger. Variants and progression (xp) are first-class columns so
// new variants / levels never need a schema redesign. Gameplay servers only ever receive the equipped ids.

const CATALOG = [
  // id, name, kind, rarity, unlock, coins, diamonds, minLevel, description
  ['bolt', 'Bolt', 'drone', 'common', 'default', 0, 0, 1, 'A tiny scout drone that hums along beside you.'],
  ['pip', 'Pip', 'robot', 'common', 'coins', 800, 0, 1, 'A round little robot with a wobbling antenna.'],
  ['orbit', 'Orbit', 'drone', 'rare', 'coins', 2500, 0, 5, 'A ring-shaped drone circling a glowing core.'],
  ['glimmer', 'Glimmer', 'creature', 'rare', 'coins', 3000, 0, 8, 'A luminous moth that leaves a faint trail of sparks.'],
  ['cinder', 'Cinder', 'creature', 'epic', 'diamonds', 0, 300, 1, 'An ember pup with a tail of drifting coals.'],
  ['tide', 'Tide', 'creature', 'epic', 'level', 0, 0, 15, 'A floating manta that glides on a cushion of mist. Unlocks at level 15.'],
  ['nova', 'Nova', 'creature', 'legendary', 'diamonds', 0, 800, 1, 'A wisp of living starlight.'],
];
const VARIANTS = [
  // companion, variant, name, coins, diamonds
  ['bolt', 'default', 'Standard', 0, 0], ['bolt', 'ember', 'Ember', 600, 0],
  ['pip', 'default', 'Standard', 0, 0], ['pip', 'neon', 'Neon', 1500, 0],
  ['orbit', 'default', 'Standard', 0, 0], ['glimmer', 'default', 'Standard', 0, 0],
  ['cinder', 'default', 'Standard', 0, 0], ['cinder', 'frost', 'Frost', 0, 150],
  ['tide', 'default', 'Standard', 0, 0], ['nova', 'default', 'Standard', 0, 0],
];
const XP_PER_LEVEL = 100;

const SCHEMA = `
CREATE TABLE IF NOT EXISTS companion_catalog (
  id          TEXT PRIMARY KEY,
  name        TEXT NOT NULL,
  kind        TEXT NOT NULL,
  rarity      TEXT NOT NULL,
  unlock_kind TEXT NOT NULL CHECK (unlock_kind IN ('default','coins','diamonds','level','reward')),
  price_coins INTEGER NOT NULL DEFAULT 0 CHECK (price_coins >= 0),
  price_diamonds INTEGER NOT NULL DEFAULT 0 CHECK (price_diamonds >= 0),
  min_level   INTEGER NOT NULL DEFAULT 1,
  description TEXT NOT NULL DEFAULT '',
  active      INTEGER NOT NULL DEFAULT 1
);
CREATE TABLE IF NOT EXISTS companion_variants (
  companion_id TEXT NOT NULL REFERENCES companion_catalog(id) ON DELETE CASCADE,
  variant_id   TEXT NOT NULL,
  name         TEXT NOT NULL,
  price_coins  INTEGER NOT NULL DEFAULT 0 CHECK (price_coins >= 0),
  price_diamonds INTEGER NOT NULL DEFAULT 0 CHECK (price_diamonds >= 0),
  PRIMARY KEY (companion_id, variant_id)
);
CREATE TABLE IF NOT EXISTS player_companions (
  player_id    INTEGER NOT NULL REFERENCES players(id) ON DELETE CASCADE,
  companion_id TEXT NOT NULL,
  variant_id   TEXT NOT NULL DEFAULT 'default',
  xp           INTEGER NOT NULL DEFAULT 0 CHECK (xp >= 0),
  acquired_at  INTEGER NOT NULL,
  PRIMARY KEY (player_id, companion_id, variant_id),
  FOREIGN KEY (companion_id, variant_id) REFERENCES companion_variants(companion_id, variant_id)
);
CREATE TABLE IF NOT EXISTS player_companion_equipped (
  player_id    INTEGER PRIMARY KEY REFERENCES players(id) ON DELETE CASCADE,
  companion_id TEXT NOT NULL,
  variant_id   TEXT NOT NULL,
  FOREIGN KEY (player_id, companion_id, variant_id) REFERENCES player_companions(player_id, companion_id, variant_id) ON DELETE CASCADE
);
CREATE TABLE IF NOT EXISTS companion_xp_events (
  key        TEXT PRIMARY KEY,
  player_id  INTEGER NOT NULL REFERENCES players(id) ON DELETE CASCADE,
  amount     INTEGER NOT NULL,
  created_at INTEGER NOT NULL
);
`;

function create(ctx) {
  const { db, q, tx, fail, now, requireString, rateLimit, playerByPublicId, economy } = ctx;
  db.exec(SCHEMA);
  for (const [id, name, kind, rarity, unlock, coins, dia, lvl, desc] of CATALOG) {
    q.run('INSERT OR IGNORE INTO companion_catalog(id, name, kind, rarity, unlock_kind, price_coins, price_diamonds, min_level, description) VALUES (?,?,?,?,?,?,?,?,?)', id, name, kind, rarity, unlock, coins, dia, lvl, desc);
  }
  for (const [c, v, name, coins, dia] of VARIANTS) q.run('INSERT OR IGNORE INTO companion_variants(companion_id, variant_id, name, price_coins, price_diamonds) VALUES (?,?,?,?,?)', c, v, name, coins, dia);
  const inTx = fn => (db.isTransaction ? fn() : tx(fn));

  // Default companions are granted lazily so existing accounts need no backfill.
  function grantDefaults(playerId) {
    for (const c of q.all("SELECT id FROM companion_catalog WHERE unlock_kind = 'default' AND active = 1")) {
      q.run("INSERT OR IGNORE INTO player_companions(player_id, companion_id, variant_id, acquired_at) VALUES (?,?,'default',?)", playerId, c.id, now());
    }
  }
  const levelOf = xp => 1 + Math.floor(xp / XP_PER_LEVEL);
  const owns = (pid, cid, vid) => !!q.one('SELECT 1 x FROM player_companions WHERE player_id = ? AND companion_id = ? AND variant_id = ?', pid, cid, vid);

  function view(me) {
    grantDefaults(me.id);
    const owned = q.all('SELECT * FROM player_companions WHERE player_id = ?', me.id);
    const eq = q.one('SELECT * FROM player_companion_equipped WHERE player_id = ?', me.id);
    const variants = q.all('SELECT * FROM companion_variants ORDER BY companion_id, variant_id');
    return {
      companions: q.all('SELECT * FROM companion_catalog WHERE active = 1 ORDER BY rowid').map(c => {
        const mine = owned.filter(o => o.companion_id === c.id);
        const base = mine.find(o => o.variant_id === 'default');
        const xp = mine.reduce((n, o) => Math.max(n, o.xp), 0);
        return {
          id: c.id, name: c.name, kind: c.kind, rarity: c.rarity, description: c.description, unlock: c.unlock_kind,
          priceCoins: c.price_coins, priceDiamonds: c.price_diamonds, minLevel: c.min_level,
          owned: !!base, level: levelOf(xp), xp, xpPerLevel: XP_PER_LEVEL,
          canUnlock: !base && c.unlock_kind !== 'reward' && me.level >= c.min_level,
          variants: variants.filter(v => v.companion_id === c.id).map(v => ({ id: v.variant_id, name: v.name, priceCoins: v.price_coins, priceDiamonds: v.price_diamonds, owned: mine.some(o => o.variant_id === v.variant_id) })),
        };
      }),
      equipped: eq ? { companionId: eq.companion_id, variantId: eq.variant_id } : null,
    };
  }

  function install(route) {
    route('GET', '/api/companions', ({ me }) => ({ body: view(me) }));

    route('POST', '/api/companions/:id/unlock', ({ me, params, body }) => {
      rateLimit('purchase', me.id);
      return tx(() => {
        grantDefaults(me.id);
        const c = q.one('SELECT * FROM companion_catalog WHERE id = ? AND active = 1', params.id);
        if (!c) fail(404, 'companion_not_found', 'Unknown companion.');
        const variantId = body.variantId === undefined ? 'default' : String(body.variantId);
        const v = q.one('SELECT * FROM companion_variants WHERE companion_id = ? AND variant_id = ?', c.id, variantId);
        if (!v) fail(404, 'variant_not_found', 'Unknown variant.');
        if (owns(me.id, c.id, variantId)) fail(409, 'already_owned', 'You already own that companion.');
        if (variantId !== 'default' && !owns(me.id, c.id, 'default')) fail(409, 'unlock_base_first', 'Unlock the companion before its variants.');
        if (me.level < c.min_level) fail(403, 'level_too_low', `Reach level ${c.min_level} to unlock ${c.name}.`);
        // The price comes from the catalog row, never from the client.
        let coins = variantId === 'default' ? c.price_coins : v.price_coins;
        let diamonds = variantId === 'default' ? c.price_diamonds : v.price_diamonds;
        if (variantId === 'default' && c.unlock_kind === 'reward') fail(403, 'not_purchasable', 'This companion is a reward and cannot be bought.');
        if (variantId === 'default' && c.unlock_kind === 'level') { coins = 0; diamonds = 0; }
        let currency = null, price = 0;
        if (coins > 0 && diamonds > 0) {
          currency = body.currency === 'diamonds' ? 'diamonds' : body.currency === 'coins' ? 'coins' : null;
          if (!currency) fail(400, 'currency_required', "Choose 'coins' or 'diamonds'.");
          price = currency === 'diamonds' ? diamonds : coins;
        } else if (coins > 0) { currency = 'coins'; price = coins; } else if (diamonds > 0) { currency = 'diamonds'; price = diamonds; }
        if (body.currency !== undefined && currency && body.currency !== currency && !(coins > 0 && diamonds > 0)) fail(400, 'wrong_currency', `This item is paid in ${currency}.`);
        if (price > 0) economy.apply(me.id, currency, -price, { kind: 'companion', refType: 'companion', refId: `${c.id}:${variantId}`, key: `companion:${me.id}:${c.id}:${variantId}` });
        q.run('INSERT INTO player_companions(player_id, companion_id, variant_id, acquired_at) VALUES (?,?,?,?)', me.id, c.id, variantId, now());
        return { status: 201, body: { ...view(me), wallet: economy.walletView(me.id) } };
      });
    });

    route('PUT', '/api/me/companion', ({ me, body }) => {
      rateLimit('claim', me.id);
      grantDefaults(me.id);
      if (body.companionId === null) {
        q.run('DELETE FROM player_companion_equipped WHERE player_id = ?', me.id);
        return { body: view(me) };
      }
      const cid = String(body.companionId || '');
      const vid = body.variantId === undefined ? 'default' : String(body.variantId);
      if (!owns(me.id, cid, vid)) fail(403, 'not_owned', 'You do not own that companion.');
      q.run('INSERT INTO player_companion_equipped(player_id, companion_id, variant_id) VALUES (?,?,?) ON CONFLICT(player_id) DO UPDATE SET companion_id = excluded.companion_id, variant_id = excluded.variant_id', me.id, cid, vid);
      return { body: view(me) };
    });

    // Trusted game server: what to spawn next to a player (ids only; no gameplay data) plus progression.
    route('GET', '/api/internal/players/:publicId/match-profile', ({ req, ip, params }) => {
      ctx.requireServerKey(req, ip);
      const p = playerByPublicId(params.publicId);
      if (!p) fail(404, 'player_not_found', 'Unknown player.');
      const eq = q.one('SELECT * FROM player_companion_equipped WHERE player_id = ?', p.id);
      return { body: { publicId: p.public_id, companion: eq ? { companionId: eq.companion_id, variantId: eq.variant_id } : null, eligibility: ctx.fairplay.eligibility(p.id) } };
    }, { auth: false });

    route('POST', '/api/internal/companions/xp', ({ req, ip, body }) => {
      ctx.requireServerKey(req, ip);
      const p = playerByPublicId(body.publicId);
      if (!p) fail(404, 'player_not_found', 'Unknown player.');
      const matchId = requireString(body.matchId, 'matchId', 4, 64);
      const amount = Number.isInteger(body.amount) && body.amount > 0 && body.amount <= 200 ? body.amount : fail(400, 'invalid_amount', 'amount must be 1-200.');
      return inTx(() => {
        const eq = q.one('SELECT * FROM player_companion_equipped WHERE player_id = ?', p.id);
        if (!eq) return { body: { applied: false, reason: 'no_companion_equipped' } };
        const r = q.run('INSERT OR IGNORE INTO companion_xp_events(key, player_id, amount, created_at) VALUES (?,?,?,?)', `cxp:${matchId}:${p.id}`, p.id, amount, now());
        if (!r.changes) return { body: { applied: false, duplicate: true } };
        q.run('UPDATE player_companions SET xp = xp + ? WHERE player_id = ? AND companion_id = ? AND variant_id = ?', amount, p.id, eq.companion_id, eq.variant_id);
        return { body: { applied: true } };
      });
    }, { auth: false });
  }

  return { install, view };
}

module.exports = { create, CATALOG, VARIANTS };
