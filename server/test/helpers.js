'use strict';
const http = require('node:http');
const { createApp } = require('../src/app');

async function startServer(options = {}) {
  const clock = { t: 1_700_000_000_000 };
  const app = createApp({ now: () => clock.t, ...options, config: { ...(options.config || {}), limits: { auth: [100000, 60000], ...((options.config || {}).limits || {}) } } });
  const server = http.createServer((req, res) => app.handle(req, res));
  await new Promise(r => server.listen(0, '127.0.0.1', r));
  const base = `http://127.0.0.1:${server.address().port}`;

  async function call(method, path, { token, body } = {}) {
    const res = await fetch(base + path, {
      method,
      headers: { ...(token ? { Authorization: 'Bearer ' + token } : {}), ...(body !== undefined ? { 'Content-Type': 'application/json' } : {}) },
      body: body !== undefined ? JSON.stringify(body) : undefined,
    });
    const text = await res.text();
    return { status: res.status, body: text ? JSON.parse(text) : null, headers: res.headers };
  }

  async function register(username, password = 'password123') {
    const r = await call('POST', '/api/auth/register', { body: { username, password } });
    if (r.status !== 201) throw new Error('register failed: ' + JSON.stringify(r.body));
    return { token: r.body.token, me: r.body.player };
  }

  async function befriend(a, b) {
    const r = await call('POST', '/api/friends/requests', { token: a.token, body: { publicId: b.me.publicId } });
    const accept = await call('POST', `/api/friends/requests/${r.body.requestId}/accept`, { token: b.token });
    if (accept.status !== 200) throw new Error('befriend failed');
  }

  return { app, clock, base, call, register, befriend, close: () => new Promise(r => server.close(() => { app.close(); r(); })) };
}

module.exports = { startServer };
