'use strict';
// Validates the ws-proxy drop/reconnect mechanism end to end.
const { chromium } = require('@playwright/test');
const { seedCase, cleanupAll } = require('./seed');
const { startProxy } = require('./ws-proxy');

const PROXY = 'http://127.0.0.1:5084';
const PASSWORD = 'P15-e2e-pass-2026';

const log = [];
const t0 = Date.now();
const ts = () => `+${((Date.now() - t0) / 1000).toFixed(1)}s`;

(async () => {
  const proxy = startProxy();
  const state = seedCase([{ key: 'u3', projects: ['A'] }]);
  const browser = await chromium.launch();
  try {
    const ctx = await browser.newContext();
    const page = await ctx.newPage();
    const sockets = [];
    page.on('websocket', (ws) => {
      const url = ws.url();
      if (!url.includes('/_blazor')) return;
      const id = new URL(url).searchParams.get('id');
      const entry = { id, frames: 0, closed: false };
      sockets.push(entry);
      log.push(`${ts()} WS-OPEN id=${id}`);
      ws.on('framereceived', () => entry.frames++);
      ws.on('framesent', () => entry.frames++);
      ws.on('close', () => {
        entry.closed = true;
        log.push(`${ts()} WS-CLOSE id=${id} frames=${entry.frames}`);
      });
    });

    await page.goto(`${PROXY}/login`);
    await page.fill('input[name="login"]', state.users.u3.login);
    await page.fill('input[name="password"]', PASSWORD);
    await Promise.all([
      page.waitForURL(/\/dashboard/),
      page.click('form[action="/v1/auth/cookie-login"] button[type="submit"]'),
    ]);
    await page.goto(`${PROXY}/board`);
    await page.locator('.kanban-board').waitFor({ state: 'visible', timeout: 30000 });
    log.push(`${ts()} board visible; sockets=${sockets.length}; proxy=${JSON.stringify(await proxy.count())}`);
    await page.waitForTimeout(2000);

    const boardId = sockets[sockets.length - 1].id;
    log.push(`${ts()} dropping circuit sockets (board circuit id=${boardId})`);
    const dropped = await proxy.drop();
    log.push(`${ts()} drop response: ${dropped}`);

    const before = sockets.length;
    let reconnectedAt = -1;
    for (let i = 0; i < 30; i++) {
      await page.waitForTimeout(1000);
      if (sockets.length > before) {
        reconnectedAt = Date.now() - t0;
        break;
      }
    }
    if (reconnectedAt >= 0) {
      await page.waitForTimeout(3000);
      const fresh = sockets[sockets.length - 1];
      log.push(`${ts()} new WS: id=${fresh.id} frames=${fresh.frames} same-as-board=${fresh.id === boardId}`);
      log.push(`${ts()} page url: ${page.url()}`);
      const boardVisible = await page.locator('.kanban-board').count();
      log.push(`${ts()} kanban-board count after reconnect: ${boardVisible}`);
    } else {
      log.push(`${ts()} NO new socket within 30s; url=${page.url()}`);
    }
    log.push(`${ts()} final: ${sockets.map((s) => `${s.id} ${s.closed ? 'closed' : 'open'} frames=${s.frames}`).join(' | ')}`);
  } finally {
    await browser.close();
    cleanupAll();
    proxy.close();
  }
  console.log(log.join('\n'));
})().catch((e) => { console.error(e); process.exit(1); });
