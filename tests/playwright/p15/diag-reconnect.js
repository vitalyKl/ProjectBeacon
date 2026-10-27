'use strict';
// Diagnoses the S4 reconnect scenario: does context.setOffline(true) drop an
// existing Blazor circuit WebSocket, and does the client open a new WS after
// the network returns?
const { chromium } = require('@playwright/test');
const { seedCase, cleanupAll } = require('./seed');

const BASE = 'http://localhost:5083';
const PASSWORD = 'P15-e2e-pass-2026';

const log = [];
const t0 = Date.now();
const ts = () => `+${((Date.now() - t0) / 1000).toFixed(1)}s`;

(async () => {
  const state = seedCase([{ key: 'u3', projects: ['A'] }]);
  const browser = await chromium.launch();
  try {
    const ctx = await browser.newContext();
    const page = await ctx.newPage();
    const sockets = [];
    page.on('websocket', (ws) => {
      const url = ws.url();
      if (!url.includes('/_blazor')) return;
      const entry = { url, frames: 0, closed: false };
      sockets.push(entry);
      log.push(`${ts()} WS-OPEN ${url}`);
      ws.on('framereceived', () => entry.frames++);
      ws.on('framesent', () => entry.frames++);
      ws.on('close', () => {
        entry.closed = true;
        log.push(`${ts()} WS-CLOSE ${url} frames=${entry.frames}`);
      });
    });

    await page.goto(`${BASE}/login`);
    await page.fill('input[name="login"]', state.users.u3.login);
    await page.fill('input[name="password"]', PASSWORD);
    await Promise.all([
      page.waitForURL(/\/dashboard/),
      page.click('form[action="/v1/auth/cookie-login"] button[type="submit"]'),
    ]);
    await page.goto(`${BASE}/board`);
    await page.locator('.kanban-board').waitFor({ state: 'visible', timeout: 30000 });
    log.push(`${ts()} board visible; sockets=${sockets.length}`);
    await page.waitForTimeout(3000);

    log.push(`${ts()} setOffline(true)`);
    await ctx.setOffline(true);
    for (let i = 0; i < 6; i++) {
      await page.waitForTimeout(2000);
      log.push(`${ts()} offline t+${(i + 1) * 2}s closed=[${sockets.map((s) => s.closed).join(',')}]`);
    }
    log.push(`${ts()} setOffline(false)`);
    await ctx.setOffline(false);
    const before = sockets.length;
    let newSocket = false;
    for (let i = 0; i < 45; i++) {
      await page.waitForTimeout(2000);
      if (sockets.length > before) {
        newSocket = true;
        break;
      }
    }
    log.push(`${ts()} new socket appeared: ${newSocket}; total=${sockets.length}`);
    if (newSocket) await page.waitForTimeout(5000);
    log.push(
      `${ts()} final: ${sockets.map((s) => `${s.closed ? 'closed' : 'open'} frames=${s.frames}`).join(' | ')}`,
    );
  } finally {
    await browser.close();
    cleanupAll();
  }
  console.log(log.join('\n'));
})().catch((e) => { console.error(e); process.exit(1); });
