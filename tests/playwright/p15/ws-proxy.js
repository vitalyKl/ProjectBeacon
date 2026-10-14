'use strict';
// Minimal TCP forward proxy for the P1.5 E2E suite.
//
// It forwards every connection to the app (127.0.0.1:5083) and tracks the
// Blazor circuit WebSockets (request path /_blazor + Upgrade header). A
// control endpoint can abruptly destroy the tracked WS sockets, which forces
// the Blazor client onto its reconnect path (same circuit id, new WebSocket).
//
// Playwright's context.setOffline(true) does NOT tear down already-open
// WebSockets in Chromium (verified empirically: frames keep flowing while
// offline), so it cannot be used to test the reconnect rebind.
const net = require('net');

const APP_HOST = '127.0.0.1';
const APP_PORT = 5083;
const PROXY_PORT = 5084;
const CTRL_PORT = 5085;

const proxied = new Set(); // all forwarded sockets (client + upstream)
const circuitSockets = new Set(); // sockets identified as Blazor circuits

function startProxy() {
  const server = net.createServer((client) => {
    const upstream = net.connect(APP_PORT, APP_HOST);
    proxied.add(client);
    proxied.add(upstream);
    let inspected = 0;
    client.on('data', (chunk) => {
      if (inspected < chunk.length) {
        inspected = chunk.length;
        const head = chunk.slice(0, 1024).toString('latin1');
        if (head.includes('Upgrade:') && head.includes('/_blazor')) {
          circuitSockets.add(client);
        }
      }
      upstream.write(chunk);
    });
    upstream.on('data', (chunk) => client.write(chunk));
    const cleanup = () => {
      proxied.delete(client);
      proxied.delete(upstream);
      circuitSockets.delete(client);
      client.destroy();
      upstream.destroy();
    };
    client.on('close', cleanup);
    upstream.on('close', cleanup);
    client.on('error', cleanup);
    upstream.on('error', cleanup);
  });
  server.listen(PROXY_PORT, APP_HOST);

  const ctrl = net.createServer((req) => {
    let buf = '';
    let done = false;
    req.on('data', (chunk) => {
      buf += chunk.toString('latin1');
      if (!done && buf.includes('\r\n\r\n')) {
        done = true;
        const [method, path] = buf.split('\r\n')[0].split(' ');
        let body;
        if (method === 'POST' && path === '/dropws') {
          const n = circuitSockets.size;
          circuitSockets.forEach((s) => s.destroy());
          body = `dropped=${n}`;
        } else if (method === 'GET' && path === '/count') {
          body = JSON.stringify({ circuits: circuitSockets.size, proxied: proxied.size / 2 });
        } else {
          body = 'unknown';
        }
        req.write(
          `HTTP/1.1 200 OK\r\nContent-Type: text/plain\r\nContent-Length: ${Buffer.byteLength(body)}\r\nConnection: close\r\n\r\n${body}`,
        );
        req.destroy();
      }
    });
  });
  ctrl.listen(CTRL_PORT, APP_HOST);

  return {
    proxyUrl: `http://127.0.0.1:${PROXY_PORT}`,
    drop: async () => {
      const res = await fetch(`http://127.0.0.1:${CTRL_PORT}/dropws`, { method: 'POST' });
      return (await res.text());
    },
    count: async () => {
      const res = await fetch(`http://127.0.0.1:${CTRL_PORT}/count`);
      return JSON.parse(await res.text());
    },
    close: () => {
      circuitSockets.forEach((s) => s.destroy());
      proxied.forEach((s) => s.destroy());
      server.close();
      ctrl.close();
    },
  };
}

module.exports = { startProxy, PROXY_PORT, CTRL_PORT };
