// Runs the economy migration on PGlite with a stub of Supabase's auth schema and roles.
// Validates SQL logic only; it does not replace testing on a real Supabase project.
import { PGlite } from '@electric-sql/pglite';
import { readFileSync } from 'node:fs';
import { test, before } from 'node:test';
import assert from 'node:assert/strict';

const migration = readFileSync(new URL('../../supabase/migrations/20250201000000_economy.sql', import.meta.url), 'utf8');
const A = '11111111-1111-1111-1111-111111111111';
const B = '22222222-2222-2222-2222-222222222222';
let db;

async function asUser(uid, fn) {
  await db.exec(`set role authenticated; select set_config('request.jwt.claim.sub', '${uid}', false);`);
  try { return await fn(); } finally { await db.exec('reset role;'); }
}
const bal = async (u, c = 'diamonds') => Number((await db.query(`select coalesce((select ${c} from player_wallets where user_id=$1),0) b`, [u])).rows[0].b);

before(async () => {
  db = new PGlite();
  await db.exec(`
    create role anon nologin; create role authenticated nologin; create role service_role nologin;
    create schema auth;
    create table auth.users (id uuid primary key);
    create function auth.uid() returns uuid language sql stable
      as $$ select nullif(current_setting('request.jwt.claim.sub', true), '')::uuid $$;
    grant usage on schema auth to anon, authenticated, service_role;
    grant execute on function auth.uid() to anon, authenticated, service_role;
    grant usage on schema public to anon, authenticated, service_role;
  `);
  await db.exec(migration);
  await db.query('insert into auth.users (id) values ($1), ($2)', [A, B]);
});

test('wallet_apply is idempotent by key', async () => {
  await db.query(`select wallet_apply($1,'diamonds',100,'grant','k1')`, [A]);
  await db.query(`select wallet_apply($1,'diamonds',100,'grant','k1')`, [A]);
  assert.equal(await bal(A), 100);
  assert.equal((await db.query('select count(*)::int c from currency_transactions where user_id=$1', [A])).rows[0].c, 1);
});

test('balance can never go negative', async () => {
  await assert.rejects(db.query(`select wallet_apply($1,'diamonds',-1000,'spend','k2')`, [A]));
  assert.equal(await bal(A), 100);
});

test('clients cannot write wallets, ledger or call mutators', async () => {
  await asUser(A, async () => {
    await assert.rejects(db.query(`update player_wallets set diamonds = 999999 where user_id = $1`, [A]));
    await assert.rejects(db.query(`insert into currency_transactions(user_id,currency,amount,balance_after,kind,idempotency_key) values ($1,'coins',5,5,'x','z')`, [A]));
    await assert.rejects(db.query(`select wallet_apply($1,'diamonds',5000,'grant','hack')`, [A]));
    await assert.rejects(db.query(`select grant_booyah('m-forged',$1,50,600)`, [A]));
  });
  assert.equal(await bal(A), 100);
});

test("users read only their own wallet and transactions", async () => {
  await db.query(`select wallet_apply($1,'coins',7,'grant','kb')`, [B]);
  await asUser(A, async () => {
    assert.equal((await db.query('select * from player_wallets')).rows.length, 1);
    assert.ok((await db.query('select * from currency_transactions')).rows.every((r) => r.user_id === A));
  });
});

test('Booyah pays 500 once per match, replay and concurrent calls are no-ops', async () => {
  const before = await bal(A);
  await Promise.all([1, 2, 3].map(() => db.query(`select grant_booyah('m1',$1,20,300)`, [A])));
  assert.equal(await bal(A), before + 500);
  assert.equal((await db.query(`select count(*)::int c from match_rewards where match_id='m1' and user_id=$1`, [A])).rows[0].c, 1);
});

test('Booyah respects eligibility, disable switch and daily cap', async () => {
  const b = await bal(B);
  await db.query(`select grant_booyah('small',$1,3,300)`, [B]);
  await db.query(`select grant_booyah('short',$1,20,10)`, [B]);
  assert.equal(await bal(B), b);
  await db.query(`update economy_settings set value = jsonb_set(value,'{enabled}','false') where key='booyah'`);
  await db.query(`select grant_booyah('off',$1,20,300)`, [B]);
  assert.equal(await bal(B), b);
  await db.query(`update economy_settings set value = jsonb_set(value,'{enabled}','true') where key='booyah'`);
  for (const m of ['c1', 'c2', 'c3', 'c4']) await db.query(`select grant_booyah($2,$1,20,300)`, [B, m]);
  assert.equal(await bal(B), b + 1500);
});

test('settle_payment credits once and tracks purchased lifetime', async () => {
  const p = (await db.query(`insert into payment_transactions(user_id,package_id,provider) values ($1,'d310','test') returning id`, [B])).rows[0].id;
  const b = await bal(B);
  await db.query(`select settle_payment($1,'tx-1')`, [p]);
  await db.query(`select settle_payment($1,'tx-1')`, [p]);
  assert.equal(await bal(B), b + 310);
  assert.equal(Number((await db.query('select lifetime_diamonds_purchased l from player_wallets where user_id=$1', [B])).rows[0].l), 310);
});

test('provider transaction ids are unique across payments', async () => {
  const p = (await db.query(`insert into payment_transactions(user_id,package_id,provider,provider_transaction_id) values ($1,'d100','apple','dup') returning id`, [A])).rows[0].id;
  assert.ok(p);
  await assert.rejects(db.query(`insert into payment_transactions(user_id,package_id,provider,provider_transaction_id) values ($1,'d100','apple','dup')`, [B]));
});
