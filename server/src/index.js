'use strict';
const http = require('node:http');
const { createApp } = require('./app');

const port = Number(process.env.PORT || 8787);
const host = process.env.HOST || '127.0.0.1'; // loopback by default; use a reverse proxy with TLS for anything public
const dbPath = process.env.DB_PATH || 'noxy-dev.sqlite';

// Only for automated tests: raise the per-minute auth limit. Never set this on a public server.
const config = {};
if (process.env.NOXY_TEST_AUTH_LIMIT) config.limits = { auth: [Number(process.env.NOXY_TEST_AUTH_LIMIT), 60000] };

// Economy secrets live only in the server environment, never in the Unity client. Unset = feature disabled.
const env = process.env;
config.economy = {
  matchServerKey: env.NOXY_MATCH_SERVER_KEY || '',
  testPayments: env.NOXY_TEST_PAYMENTS === '1', // sandbox purchases with no real money; keep off on public servers
  webhookSecrets: { apple: env.NOXY_WEBHOOK_SECRET_APPLE || '', google: env.NOXY_WEBHOOK_SECRET_GOOGLE || '', test: env.NOXY_WEBHOOK_SECRET_TEST || '' },
  adsSecret: env.NOXY_ADS_SECRET || '',
  verifiers: {}, // register real Apple / Google receipt verifiers here once credentials exist
};
for (const k of Object.keys(config.economy.webhookSecrets)) if (!config.economy.webhookSecrets[k]) delete config.economy.webhookSecrets[k];

// Supabase identity (public values only: project URL + anon/publishable key). Never put the service-role key here.
config.supabase = { url: env.SUPABASE_URL || '', anonKey: env.SUPABASE_ANON_KEY || '', cacheMs: Number(env.NOXY_SUPABASE_CACHE_SECONDS || 60) * 1000 };
config.authMode = env.NOXY_AUTH_MODE || ''; // 'supabase' | 'local'; empty = supabase when configured, otherwise local dev accounts
if (env.SUPABASE_SERVICE_ROLE_KEY) console.warn('SUPABASE_SERVICE_ROLE_KEY is set but this server does not use it. Remove it from this environment.');

// Optional active health probe for registered game servers (TCP connect). Heartbeats stay the primary liveness signal.
const { tcpProbe } = require('./servers');
config.servers = { ttlMs: Number(env.NOXY_SERVER_TTL_SECONDS || 30) * 1000, probe: env.NOXY_SERVER_PROBE === 'tcp' ? tcpProbe() : null };

const app = createApp({ dbPath, config });
const server = http.createServer((req, res) => { app.handle(req, res); });
server.listen(port, host, () => console.log(`Noxy dev backend listening on http://${host}:${port} (db: ${dbPath}, auth: ${app.authMode})`));
