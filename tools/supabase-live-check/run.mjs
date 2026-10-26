// Live verification against YOUR real Supabase project (and, optionally, your running game backend).
//
//   set SUPABASE_URL=https://<ref>.supabase.co
//   set SUPABASE_ANON_KEY=<anon / publishable key>          (never the service-role key)
//   node run.mjs
//
// Optional extras:
//   set NOXY_SERVER_URL=https://your-game-backend            -> Test F: the game backend accepts the same Supabase session
//   set NOXY_MATCH_SERVER_KEY=<server key>  and run with --registry -> Test G: server registry + heartbeat expiry (waits ~TTL)
//   set LIVECHECK_EMAIL_DOMAIN=yourdomain.test                -> domain for the disposable test accounts
//
// It creates two DISPOSABLE users (email/password) and never touches existing users. It cannot delete them (that needs the
// service-role key, which this script refuses to use): remove them in Dashboard > Authentication > Users afterwards.
// Tokens, passwords and full e-mail addresses are never printed or written to the evidence file.
import crypto from 'node:crypto';
import { writeFileSync } from 'node:fs';

const URL_ = (process.env.SUPABASE_URL || '').replace(/\/+$/, '');
const ANON = process.env.SUPABASE_ANON_KEY || '';
const NODE_URL = (process.env.NOXY_SERVER_URL || '').replace(/\/+$/, '');
const SERVER_KEY = process.env.NOXY_MATCH_SERVER_KEY || '';
const DOMAIN = process.env.LIVECHECK_EMAIL_DOMAIN || 'example.com';
const WANT_REGISTRY = process.argv.includes('--registry');

if (!URL_ || !ANON) {
  console.error('Set SUPABASE_URL and SUPABASE_ANON_KEY (anon/publishable key only).');
  process.exit(2);
}
if (!/^https:\/\//.test(URL_) && !/^http:\/\/(127\.0\.0\.1|localhost)/.test(URL_)) { console.error('SUPABASE_URL must be https (or a local loopback address).'); process.exit(2); }
try {
  const payload = JSON.parse(Buffer.from(ANON.split('.')[1] || '', 'base64url').toString('utf8'));
  if (payload.role === 'service_role') { console.error('REFUSING TO RUN: that is a service_role key. Use the anon/publishable key.'); process.exit(2); }
} catch { /* publishable keys (sb_publishable_...) are not JWTs; that is fine */ }
if (/^sb_secret_/.test(ANON)) { console.error('REFUSING TO RUN: that is a secret key. Use the publishable key.'); process.exit(2); }

const results = [];
class Blocked extends Error {}
const mask = e => e.replace(/^(.).*(@.*)$/, '$1***$2');
async function step(id, name, fn) {
  const t0 = Date.now();
  try {
    const detail = await fn();
    results.push({ id, name, status: 'PASS', detail: detail || '', ms: Date.now() - t0 });
  } catch (e) {
    results.push({ id, name, status: e instanceof Blocked ? 'BLOCKED' : 'FAIL', detail: String(e.message || e).slice(0, 400), ms: Date.now() - t0 });
  }
  const r = results[results.length - 1];
  console.log(`${r.status.padEnd(7)} ${id.padEnd(4)} ${name}${r.detail ? '  -> ' + r.detail : ''}`);
}
const ok = (cond, msg) => { if (!cond) throw new Error(msg); };

async function http(method, url, { token, body, headers = {} } = {}) {
  const res = await fetch(url, {
    method, headers: { apikey: ANON, Accept: 'application/json', ...(token ? { Authorization: 'Bearer ' + token } : {}), ...(body !== undefined ? { 'Content-Type': 'application/json' } : {}), ...headers },
    body: body !== undefined ? JSON.stringify(body) : undefined,
  });
  const text = await res.text();
  let json = null; try { json = text ? JSON.parse(text) : null; } catch { /* not json */ }
  return { status: res.status, json };
}
const auth = (path, body, token, method = 'POST') => http(method, `${URL_}/auth/v1${path}`, { token, body });
const rest = (method, path, token, body, headers) => http(method, `${URL_}/rest/v1${path}`, { token, body, headers });
const rpc = (fn, token, body = {}) => rest('POST', `/rpc/${fn}`, token, body);
const denied = r => r.status === 401 || r.status === 403 || (r.json && (r.json.code === '42501' || r.json.code === 'PGRST301'));
const rand = () => crypto.randomBytes(4).toString('hex');
const profileOf = async (token, uid) => (await rest('GET', `/players?select=*&user_id=eq.${uid}`, token)).json;

async function signUp(label) {
  const email = `noxy-live-${label}-${rand()}@${DOMAIN}`;
  const password = 'Lc!' + crypto.randomBytes(9).toString('base64url');
  const r = await auth('/signup', { email, password });
  if (r.status !== 200) throw new Error(`signup HTTP ${r.status}: ${r.json && (r.json.msg || r.json.message || r.json.error_description)}`);
  if (!r.json.access_token) throw new Blocked('sign-up returned no session: e-mail confirmation is enabled. Use a test project with "Confirm email" off, or the local Supabase CLI stack.');
  return { email, password, token: r.json.access_token, refresh: r.json.refresh_token, id: r.json.user.id };
}
const login = async u => {
  const r = await auth('/token?grant_type=password', { email: u.email, password: u.password });
  ok(r.status === 200 && r.json.access_token, `login failed HTTP ${r.status}`);
  return r.json;
};

console.log(`Supabase live check -> ${URL_}${NODE_URL ? '   game backend -> ' + NODE_URL : ''}\n`);

// ---------- Preflight: what is configured (no secrets involved) ----------
let settings = null;
await step('P1', 'Project reachable and auth settings readable', async () => {
  const r = await auth('/settings', undefined, undefined, 'GET');
  ok(r.status === 200 && r.json, `HTTP ${r.status}`);
  settings = r.json;
  const ext = settings.external || {};
  return `email=${!!ext.email} google=${!!ext.google} apple=${!!ext.apple} signup=${!settings.disable_signup} autoconfirm=${!!settings.mailer_autoconfirm}`;
});
await step('P2', 'Google provider enabled in Supabase', async () => {
  if (!(settings && settings.external && settings.external.google)) throw new Blocked('NOT CONFIGURED: Google is not enabled. Dashboard > Authentication > Providers > Google (client ID + secret).');
  return 'enabled (credential correctness can only be proven by an interactive sign-in)';
});
await step('P3', 'Apple provider enabled in Supabase', async () => {
  if (!(settings && settings.external && settings.external.apple)) throw new Blocked('NOT CONFIGURED: Apple is not enabled. Dashboard > Authentication > Providers > Apple (Services ID, Team ID, Key ID, secret JWT).');
  return 'enabled (credential correctness can only be proven by an interactive sign-in)';
});

// ---------- Test A: registration ----------
let u1 = null, u2 = null, playerId = null;
await step('A1', 'Register a new account through real Supabase Auth', async () => { u1 = await signUp('one'); return `user ${u1.id.slice(0, 8)}... (${mask(u1.email)})`; });
await step('A2', 'Profile row was created by the backend with a 10-digit Player ID', async () => {
  ok(u1, 'no account');
  const rows = await profileOf(u1.token, u1.id);
  ok(Array.isArray(rows) && rows.length === 1, `expected exactly 1 profile row, got ${JSON.stringify(rows).slice(0, 120)}`);
  playerId = rows[0].player_id;
  ok(/^[1-9][0-9]{9}$/.test(playerId), 'player_id is not 10 digits');
  ok(rows[0].level === 1 && Number(rows[0].xp) === 0, 'starting level/xp wrong');
  return `player_id=${playerId}`;
});
await step('A3', 'ensure_player() x3 keeps the same ID and creates no duplicate', async () => {
  ok(u1 && playerId, 'no account');
  for (let i = 0; i < 3; i++) {
    const r = await rpc('ensure_player', u1.token);
    ok(r.status === 200, `HTTP ${r.status}`);
    const row = Array.isArray(r.json) ? r.json[0] : r.json;
    ok(row.player_id === playerId, `ID changed to ${row.player_id}`);
  }
  const rows = await profileOf(u1.token, u1.id);
  ok(rows.length === 1, 'duplicate profile rows');
  return 'same ID, one row';
});

// ---------- Test B: login and session ----------
await step('B1', 'Password login returns a session for the same user and Player ID', async () => {
  ok(u1, 'no account');
  const s = await login(u1);
  ok(s.user.id === u1.id, 'different user id');
  const rows = await profileOf(s.access_token, u1.id);
  ok(rows[0].player_id === playerId, 'Player ID differs after login');
  return 'same user, same Player ID';
});
await step('B2', 'Wrong password is refused', async () => {
  ok(u1, 'no account');
  const r = await auth('/token?grant_type=password', { email: u1.email, password: 'definitely-wrong-123' });
  ok(r.status === 400 || r.status === 401, `HTTP ${r.status}`);
  ok(!(r.json && r.json.access_token), 'a session was issued');
  return `HTTP ${r.status} ${r.json && (r.json.error_code || r.json.error)}`;
});
await step('B3', 'Garbage / forged tokens are refused by Auth and by the data API', async () => {
  const forged = 'eyJhbGciOiJIUzI1NiJ9.eyJzdWIiOiIwMDAwMDAwMC0wMDAwLTAwMDAtMDAwMC0wMDAwMDAwMDAwMDAiLCJleHAiOjk5OTk5OTk5OTl9.' + crypto.randomBytes(24).toString('base64url');
  const a = await auth('/user', undefined, forged, 'GET');
  const d = await rest('GET', '/players?select=*', forged);
  ok(a.status === 401 || a.status === 403, `/auth/v1/user accepted a forged token (HTTP ${a.status})`);
  ok(denied(d), `data API accepted a forged token (HTTP ${d.status})`);
  return `auth ${a.status}, data ${d.status}`;
});
await step('B4', 'Refresh token issues a new session for the same user', async () => {
  ok(u1, 'no account');
  const r = await auth('/token?grant_type=refresh_token', { refresh_token: u1.refresh });
  ok(r.status === 200 && r.json.access_token && r.json.user.id === u1.id, `HTTP ${r.status}`);
  return 'refreshed (expiry handling in the game is covered by the C# tests)';
});
await step('B5', 'Network failure handling', async () => { throw new Blocked('cannot be induced from a script against a live project; covered by the automated C# tests (SupabaseOutageMeansNoSession...).'); });

// ---------- Test C: persistence ----------
const newName = 'Lc_' + rand();
await step('C1', 'Rename is saved and a later authorized read returns it', async () => {
  const s = await login(u1);
  const r = await rest('PATCH', `/players?user_id=eq.${u1.id}`, s.access_token, { username: newName }, { Prefer: 'return=representation' });
  ok(r.status === 200 && Array.isArray(r.json) && r.json[0] && r.json[0].username === newName, `HTTP ${r.status} ${JSON.stringify(r.json).slice(0, 150)}`);
  const again = await profileOf(s.access_token, u1.id);
  ok(again[0].username === newName, 'read-after-write mismatch');
  return `username=${newName}`;
});
await step('C2', 'After sign-out and sign-in the saved values are unchanged', async () => {
  const s = await login(u1);
  const out = await auth('/logout?scope=local', undefined, s.access_token);
  ok(out.status === 204 || out.status === 200, `logout HTTP ${out.status}`);
  const s2 = await login(u1);
  const rows = await profileOf(s2.access_token, u1.id);
  ok(rows[0].username === newName && rows[0].player_id === playerId, 'values changed');
  return 'username and Player ID persisted';
});
await step('C3', 'A signed-out token no longer works', async () => {
  const s = await login(u1);
  await auth('/logout?scope=local', undefined, s.access_token);
  const a = await auth('/user', undefined, s.access_token, 'GET');
  ok(a.status === 401 || a.status === 403, `old token still accepted by Auth (HTTP ${a.status}); Supabase may keep JWTs valid until expiry, so the game backend re-verifies with /auth/v1/user`);
  return `Auth rejects it (HTTP ${a.status})`;
});
await step('C4', 'Invalid username values are rejected', async () => {
  const s = await login(u1);
  for (const bad of ['a b', 'x', 'name-with-dash', 'a'.repeat(21)]) {
    const r = await rest('PATCH', `/players?user_id=eq.${u1.id}`, s.access_token, { username: bad }, { Prefer: 'return=representation' });
    ok(r.status >= 400, `"${bad}" was accepted (HTTP ${r.status})`);
  }
  return 'format constraint enforced';
});
await step('C5', 'avatar_url accepts https only (needs migration 20250401000000)', async () => {
  const s = await login(u1);
  const good = await rest('PATCH', `/players?user_id=eq.${u1.id}`, s.access_token, { avatar_url: 'https://cdn.example.com/a.png' }, { Prefer: 'return=representation' });
  ok(good.status === 200, `https avatar refused (HTTP ${good.status})`);
  const bad = await rest('PATCH', `/players?user_id=eq.${u1.id}`, s.access_token, { avatar_url: 'javascript:alert(1)' }, { Prefer: 'return=representation' });
  ok(bad.status >= 400, 'javascript: avatar accepted. Apply supabase/migrations/20250401000000_identity_hardening.sql.');
  await rest('PATCH', `/players?user_id=eq.${u1.id}`, s.access_token, { avatar_url: null });
  return 'only https accepted';
});

// ---------- Test D: cross-device ----------
await step('D1', 'Two independent sessions load the same profile; a change on one shows on the other', async () => {
  const a = await login(u1), b = await login(u1);
  ok(a.access_token !== b.access_token, 'sessions are not independent');
  const n2 = 'Lc_' + rand();
  const r = await rest('PATCH', `/players?user_id=eq.${u1.id}`, a.access_token, { username: n2 }, { Prefer: 'return=representation' });
  ok(r.status === 200, `HTTP ${r.status}`);
  const seen = await profileOf(b.access_token, u1.id);
  ok(seen[0].username === n2 && seen[0].player_id === playerId, 'second session did not see the update');
  return 'both sessions agree';
});

// ---------- Test E: authorization ----------
await step('E0', 'Register a second disposable account', async () => { u2 = await signUp('two'); return `user ${u2.id.slice(0, 8)}...`; });
await step('E1', "Account 2 cannot read account 1's profile", async () => {
  ok(u1 && u2, 'missing accounts');
  const rows = await profileOf(u2.token, u1.id);
  ok(Array.isArray(rows) && rows.length === 0, 'RLS leak: ' + JSON.stringify(rows).slice(0, 100));
  const all = await rest('GET', '/players?select=user_id', u2.token);
  ok(all.json.every(r => r.user_id === u2.id), 'listing players returned someone else');
  return 'own row only';
});
await step('E2', "Account 2 cannot modify account 1's profile", async () => {
  const before = (await profileOf((await login(u1)).access_token, u1.id))[0];
  const r = await rest('PATCH', `/players?user_id=eq.${u1.id}`, u2.token, { username: 'Hijacked_' + rand() }, { Prefer: 'return=representation' });
  ok(!(r.status === 200 && Array.isArray(r.json) && r.json.length > 0), 'update succeeded on another player');
  const after = (await profileOf((await login(u1)).access_token, u1.id))[0];
  ok(after.username === before.username, 'username changed');
  return `HTTP ${r.status}, row unchanged`;
});
await step('E3', 'Clients cannot change xp, level, coins, gems, player_id or user_id on their own row', async () => {
  const before = (await profileOf(u2.token, u2.id))[0];
  const attempts = { xp: 999999, level: 99, coins: 999999, gems: 999999, player_id: '1234567890', user_id: u1.id };
  for (const [col, val] of Object.entries(attempts)) {
    const r = await rest('PATCH', `/players?user_id=eq.${u2.id}`, u2.token, { [col]: val }, { Prefer: 'return=representation' });
    ok(denied(r) || r.status >= 400, `${col} update accepted (HTTP ${r.status})`);
  }
  const after = (await profileOf(u2.token, u2.id))[0];
  for (const col of ['xp', 'level', 'coins', 'gems', 'player_id']) ok(String(after[col]) === String(before[col]), `${col} changed`);
  return 'all protected columns refused and unchanged';
});
await step('E4', 'Clients cannot insert or delete profile rows', async () => {
  const ins = await rest('POST', '/players', u2.token, { user_id: crypto.randomUUID(), player_id: '1111111111', username: 'Fake_' + rand() });
  ok(ins.status >= 400, `insert accepted (HTTP ${ins.status})`);
  const del = await rest('DELETE', `/players?user_id=eq.${u2.id}`, u2.token, undefined, { Prefer: 'return=representation' });
  ok(!(del.status < 300 && Array.isArray(del.json) && del.json.length), 'delete accepted');
  ok((await profileOf(u2.token, u2.id)).length === 1, 'profile vanished');
  return 'insert/delete refused';
});
await step('E5', 'Anonymous (no user token) cannot read profiles', async () => {
  const r = await rest('GET', '/players?select=*', undefined);
  ok(denied(r) || (Array.isArray(r.json) && r.json.length === 0), `anon read returned rows (HTTP ${r.status})`);
  return `HTTP ${r.status}`;
});
await step('E6', 'find_player exposes only public fields', async () => {
  const r = await rpc('find_player', u2.token, { p_player_id: playerId });
  ok(r.status === 200 && Array.isArray(r.json), `HTTP ${r.status}`);
  ok(r.json.length === 1, 'player not found by public ID');
  const keys = Object.keys(r.json[0]).sort().join(',');
  ok(keys === 'avatar_url,level,player_id,username', 'unexpected fields: ' + keys);
  return `fields: ${keys}`;
});
await step('E7', 'Privileged functions (grant_progress) cannot be called by a client', async () => {
  const r = await rpc('grant_progress', u2.token, { p_user: u2.id, p_xp: 1000, p_coins: 1000, p_gems: 1000 });
  ok(r.status >= 400, `grant_progress executed (HTTP ${r.status})`);
  const p = (await profileOf(u2.token, u2.id))[0];
  ok(Number(p.xp || 0) === 0 && Number(p.coins || 0) === 0 && Number(p.gems || 0) === 0, 'balances changed');
  return `HTTP ${r.status}`;
});

// ---------- Test F: game backend accepts the same Supabase session ----------
if (!NODE_URL) {
  await step('F0', 'Game backend bridge', async () => { throw new Blocked('set NOXY_SERVER_URL (a backend started with SUPABASE_URL + SUPABASE_ANON_KEY) to run Test F.'); });
} else {
  const me = async (token, method = 'GET', body) => http(method, `${NODE_URL}/api/me`, { token, body });
  await step('F1', 'Backend reports Supabase auth mode', async () => {
    const r = await http('GET', `${NODE_URL}/api/health`);
    ok(r.status === 200, `HTTP ${r.status}`);
    ok(r.json.authMode === 'supabase', `authMode=${r.json.authMode}: set SUPABASE_URL and SUPABASE_ANON_KEY on the backend`);
    return 'authMode=supabase';
  });
  await step('F2', 'Backend accepts the Supabase token and uses the SAME Player ID', async () => {
    const s = await login(u1);
    const r = await me(s.access_token);
    ok(r.status === 200, `HTTP ${r.status} ${JSON.stringify(r.json).slice(0, 150)}`);
    ok(r.json.publicId === playerId, `backend Player ID ${r.json.publicId} != Supabase ${playerId}`);
    return `publicId=${r.json.publicId}`;
  });
  await step('F3', 'No token, forged token and local password accounts are refused', async () => {
    ok((await me(undefined)).status === 401, 'no token accepted');
    const forged = 'eyJhbGciOiJFUzI1NiJ9.' + Buffer.from(JSON.stringify({ sub: crypto.randomUUID(), exp: 9999999999 })).toString('base64url') + '.' + crypto.randomBytes(24).toString('base64url');
    ok((await me(forged)).status === 401, 'forged token accepted');
    const reg = await http('POST', `${NODE_URL}/api/auth/register`, { body: { username: 'livecheck_x', password: 'password123' } });
    ok(reg.status === 403, `password registration still enabled (HTTP ${reg.status})`);
    return 'all refused';
  });
  await step('F4', 'Rename through the backend is stored in Supabase and read back', async () => {
    const s = await login(u1);
    const name = 'Lc_' + rand();
    const r = await me(s.access_token, 'PATCH', { username: name });
    if (r.status === 429) throw new Blocked('rename cooldown (1/day) is active for this account.');
    ok(r.status === 200, `HTTP ${r.status} ${JSON.stringify(r.json).slice(0, 150)}`);
    const rows = await profileOf(s.access_token, u1.id);
    ok(rows[0].username === name, 'Supabase does not hold the new name');
    return `username=${name}`;
  });
  await step('F5', 'Wallet and inventory load for the Supabase account', async () => {
    const s = await login(u1);
    const w = await http('GET', `${NODE_URL}/api/me/wallet`, { token: s.access_token });
    const inv = await http('GET', `${NODE_URL}/api/me/inventory`, { token: s.access_token });
    ok(w.status === 200 && inv.status === 200, `wallet ${w.status}, inventory ${inv.status}`);
    return `coins=${w.json.coins} diamonds=${w.json.diamonds} owned=${inv.json.owned.length}`;
  });
}

// ---------- Test G: dedicated server registry ----------
if (!WANT_REGISTRY || !NODE_URL || !SERVER_KEY) {
  await step('G0', 'Server registry', async () => { throw new Blocked('run with --registry and set NOXY_SERVER_URL + NOXY_MATCH_SERVER_KEY. No real dedicated server exists in this repo yet, so this registers a synthetic test record.'); });
} else {
  const key = { 'x-noxy-server-key': SERVER_KEY };
  const sid = 'livecheck-' + rand();
  let inst = null, ttl = 30;
  const listed = async () => {
    const s = await login(u1);
    const r = await http('GET', `${NODE_URL}/api/servers`, { token: s.access_token });
    ok(r.status === 200, `list HTTP ${r.status}`);
    return r.json.servers.some(x => x.id === sid);
  };
  await step('G1', 'Register through the trusted endpoint; not listed until a heartbeat proves health', async () => {
    const r = await http('POST', `${NODE_URL}/api/internal/servers/register`, { headers: key, body: { serverId: sid, name: 'Live check', region: 'test', mode: 'battle_royale', host: 'livecheck.invalid', port: 7777, capacity: 10 } });
    ok(r.status === 201, `HTTP ${r.status} ${JSON.stringify(r.json).slice(0, 150)}`);
    inst = r.json.instanceToken; ttl = r.json.ttlSeconds;
    ok(!(await listed()), 'listed before any heartbeat');
    return `ttl=${ttl}s`;
  });
  await step('G2', 'Heartbeat makes it listed; wrong key / token are refused', async () => {
    const bad = await http('POST', `${NODE_URL}/api/internal/servers/heartbeat`, { headers: { 'x-noxy-server-key': 'wrong' }, body: { serverId: sid, instanceToken: inst } });
    ok(bad.status === 401, 'wrong key accepted');
    const bad2 = await http('POST', `${NODE_URL}/api/internal/servers/heartbeat`, { headers: key, body: { serverId: sid, instanceToken: 'x'.repeat(43) } });
    ok(bad2.status === 401, 'wrong instance token accepted');
    const hb = await http('POST', `${NODE_URL}/api/internal/servers/heartbeat`, { headers: key, body: { serverId: sid, instanceToken: inst, players: 3 } });
    ok(hb.status === 200, `HTTP ${hb.status}`);
    ok(hb.json.status === 'online', `status=${hb.json.status}: if a probe is enabled (NOXY_SERVER_PROBE=tcp) the address must be reachable`);
    ok(await listed(), 'not listed after heartbeat');
    return 'listed after heartbeat';
  });
  await step('G3', `Stops heartbeating -> becomes inactive within the TTL (waits ${ttl + 2}s)`, async () => {
    await new Promise(r => setTimeout(r, (ttl + 2) * 1000));
    ok(!(await listed()), 'still listed after the heartbeat timeout');
    await http('POST', `${NODE_URL}/api/internal/servers/deregister`, { headers: key, body: { serverId: sid, instanceToken: inst } });
    return 'no longer listed';
  });
}

// ---------- report ----------
const counts = { PASS: 0, FAIL: 0, BLOCKED: 0 };
for (const r of results) counts[r.status]++;
console.log(`\nSummary: ${counts.PASS} passed, ${counts.FAIL} failed, ${counts.BLOCKED} blocked (not run).`);
console.log('Test accounts are disposable but remain in Auth; delete them in Dashboard > Authentication > Users.');
const out = `evidence-${new Date().toISOString().replace(/[:.]/g, '-')}.json`;
writeFileSync(new URL(out, import.meta.url), JSON.stringify({ project: URL_.replace(/^https?:\/\/([^.]+)\..*/, '$1'), finishedAt: new Date().toISOString(), counts, results }, null, 2));
console.log('Evidence written to tools/supabase-live-check/' + out + ' (no tokens, passwords or full e-mails).');
process.exit(counts.FAIL ? 1 : 0);
