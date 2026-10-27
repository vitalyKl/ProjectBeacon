'use strict';
// Replicates the S1 sequence exactly (login -> board -> admin revoke -> first
// in-circuit dashboard click) and dumps observable state at each step, to
// explain why the S1 run timed out waiting for .beacon-metric-strip.
const { chromium } = require('@playwright/test');
const { seedCase, cleanupAll, readAdminCredentials } = require('./seed');

const BASE = 'http://localhost:5083';
const PASSWORD = 'P15-e2e-pass-2026';

const log = [];
const t0 = Date.now();
const ts = () => `+${((Date.now() - t0) / 1000).toFixed(1)}s`;

(async () => {
  const state = seedCase([{ key: 'u1', projects: ['A', 'B'] }]);
  const admin = readAdminCredentials();
  const browser = await chromium.launch();
  try {
    const ctxU1 = await browser.newContext();
    const page = await ctxU1.newPage();
    const t = { docs: 0, ws: [] };
    page.on('request', (r) => {
      if (r.resourceType() === 'document') t.docs += 1;
    });
    page.on('websocket', (ws) => {
      if (!ws.url().includes('/_blazor')) return;
      const id = new URL(ws.url()).searchParams.get('id');
      const entry = { id, frames: 0, closed: false };
      t.ws.push(entry);
      log.push(`${ts()} WS-OPEN id=${id}`);
      ws.on('framereceived', () => entry.frames++);
      ws.on('framesent', () => entry.frames++);
      ws.on('close', () => {
        entry.closed = true;
        log.push(`${ts()} WS-CLOSE id=${id} frames=${entry.frames}`);
      });
    });
    page.on('console', (m) => {
      if (m.type() === 'error') log.push(`${ts()} CONSOLE-ERR ${m.text().slice(0, 300)}`);
    });

    await page.goto(`${BASE}/login`);
    await page.fill('input[name="login"]', state.users.u1.login);
    await page.fill('input[name="password"]', PASSWORD);
    await Promise.all([
      page.waitForURL(/\/dashboard/),
      page.click('form[action="/v1/auth/cookie-login"] button[type="submit"]'),
    ]);
    await page.goto(`${BASE}/board`);
    await page.locator('.kanban-board').waitFor({ state: 'visible', timeout: 30000 });
    log.push(`${ts()} board visible; markerA=${await page.locator('.beacon-card-title', { hasText: state.markerA }).count()}`);

    const ctxAdmin = await browser.newContext();
    const pageAdmin = await ctxAdmin.newPage();
    await pageAdmin.goto(`${BASE}/login`);
    await pageAdmin.fill('input[name="login"]', admin.login);
    await pageAdmin.fill('input[name="password"]', admin.password);
    await Promise.all([
      pageAdmin.waitForURL(/\/dashboard/),
      pageAdmin.click('form[action="/v1/auth/cookie-login"] button[type="submit"]'),
    ]);
    log.push(`${ts()} admin logged in`);

    const del = await ctxAdmin.request.delete(`${BASE}/v1/projects/${state.projectA}/members/${state.users.u1.id}`);
    log.push(`${ts()} revoke -> ${del.status()}`);

    const clickSel = '.beacon-drawer a[href="/dashboard"]';
    await page.locator(clickSel).first().click();
    for (let i = 1; i <= 6; i++) {
      await page.waitForTimeout(5000);
      const metric = await page.locator('.beacon-metric-strip').count();
      const board = await page.locator('.kanban-board').count();
      const url = page.url();
      log.push(`${ts()} t+${i * 5}s after click: url=${url} docs=${t.docs} ws=${t.ws.length} metric=${metric} board=${board}`);
      if (metric > 0 || i === 6) {
        if (metric === 0) {
          const bodyText = await page.evaluate(() => document.body.innerText.slice(0, 1200));
          log.push(`${ts()} body: ${JSON.stringify(bodyText)}`);
          await page.screenshot({ path: 'diag-s1.png' });
          log.push(`${ts()} screenshot saved`);
        }
        break;
      }
    }
  } finally {
    await browser.close();
    cleanupAll();
  }
  console.log(log.join('\n'));
})().catch((e) => { console.error(e); process.exit(1); });
