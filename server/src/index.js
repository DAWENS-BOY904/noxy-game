'use strict';
const http = require('node:http');
const { createApp } = require('./app');

const port = Number(process.env.PORT || 8787);
const host = process.env.HOST || '127.0.0.1'; // loopback by default; use a reverse proxy with TLS for anything public
const dbPath = process.env.DB_PATH || 'noxy-dev.sqlite';

// Only for automated tests: raise the per-minute auth limit. Never set this on a public server.
const config = {};
if (process.env.NOXY_TEST_AUTH_LIMIT) config.limits = { auth: [Number(process.env.NOXY_TEST_AUTH_LIMIT), 60000] };

const app = createApp({ dbPath, config });
const server = http.createServer((req, res) => { app.handle(req, res); });
server.listen(port, host, () => console.log(`Noxy dev backend listening on http://${host}:${port} (db: ${dbPath})`));
