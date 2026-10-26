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

const app = createApp({ dbPath, config });
const server = http.createServer((req, res) => { app.handle(req, res); });
server.listen(port, host, () => console.log(`Noxy dev backend listening on http://${host}:${port} (db: ${dbPath})`));
