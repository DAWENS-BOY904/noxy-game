// DEVELOPMENT STAND-IN, not a game server.
//
// The Battle Royale queue only accepts players while the backend lists a verified online server for the mode. There is no
// dedicated Unity/Linux server yet: today the Node backend itself runs the match rules (server/src/br.js). This script
// registers a LABEL for it in the server registry and keeps it alive with heartbeats, so the game's PLAY button is allowed
// to queue while you develop. It proves nothing about a real dedicated server.
//
//   $env:NOXY_MATCH_SERVER_KEY = "<the same key the backend was started with>"
//   node tools/dev-br-server/register.mjs            (optional: $env:NOXY_API = "http://127.0.0.1:8787")
const key = process.env.NOXY_MATCH_SERVER_KEY;
const api = (process.env.NOXY_API || 'http://127.0.0.1:8787').replace(/\/$/, '');
if (!key) { console.error('Set NOXY_MATCH_SERVER_KEY (the same value the backend was started with). It is never stored in the game client.'); process.exit(1); }

const headers = { 'Content-Type': 'application/json', 'x-noxy-server-key': key };
const post = async (path, body) => {
  const r = await fetch(api + path, { method: 'POST', headers, body: JSON.stringify(body) });
  const j = await r.json().catch(() => ({}));
  if (!r.ok) throw new Error(path + ' -> ' + r.status + ' ' + JSON.stringify(j));
  return j;
};

const reg = await post('/api/internal/servers/register', { serverId: 'dev-br-1', name: 'Dev stand-in (backend)', region: 'local', mode: 'battle_royale', host: '127.0.0.1', port: 7777, capacity: 20, version: 'dev' });
console.log('Registered dev-br-1 as a stand-in. The backend process is the match authority; there is no dedicated server.');
const every = Math.max(1000, reg.heartbeatSeconds * 1000);
const beat = async () => {
  try { const h = await post('/api/internal/servers/heartbeat', { serverId: 'dev-br-1', instanceToken: reg.instanceToken, players: 0 }); console.log(new Date().toISOString(), 'heartbeat ->', h.status); }
  catch (e) { console.error('heartbeat failed:', e.message); }
};
await beat();
setInterval(beat, every);
