'use strict';
// Diagnoses why the in-circuit click on the drawer /dashboard link did not
// render the dashboard body (.beacon-metric-strip) in the S1 scenario.
const { chromium } = require('@playwright/test');
const { seedCase, cleanupAll } = require('./seed');

const BASE = 'http://localhost:5083';
const PASSWORD = 'P15-e2e-pass-2026';

const log = [];
const t0 = Date.now();
const ts = () => `+${((Date.now() - t0) / 1000).toFixed(1)}s`;

(async () => {
  const state = seedCase([{ key: 'u1', projects: ['A', 'B'] }]);
  const browser = await chromium.launch();
  try {
    const ctx = await browser.newContext();
    const page = await ctx.newPage();
    const t = { docs: 0, ws: [] };
    page.on('request', (r) => {
      if (r.resourceType() === 'document') {
        t.docs += 1;
        log.push(`${ts()} DOC ${r.url()}`);
      }
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
    log.push(`${ts()} on /dashboard; docs=${t.docs} ws=${t.ws.length}`);

    await page.goto(`${BASE}/board`);
    await page.locator('.kanban-board').waitFor({ state: 'visible', timeout: 30000 });
    log.push(`${ts()} board visible; docs=${t.docs} ws=${t.ws.length}`);

    // In-circuit navigation: click the drawer /dashboard link.
    const clickSel = '.beacon-drawer a[href="/dashboard"]';
    const link = page.locator(clickSel);
    log.push(`${ts()} drawer link count=${await link.count()}`);
    const before = { docs: t.docs, ws: t.ws.length };
    await link.first().click();
    await page.waitForTimeout(8000);
    log.push(
      `${ts()} after click: url=${page.url()} docs=${t.docs} (was ${before.docs}) ws=${t.ws.length} (was ${before.ws})`,
    );
    const metric = await page.locator('.beacon-metric-strip').count();
    const board = await page.locator('.kanban-board').count();
    const grid = await page.locator('.beacon-ops-grid').count();
    const alerts = await page.locator('.mud-alert').allInnerTexts();
    const prog = await page.locator('.mud-progress-linear').count();
    const btns = await page.locator('a.mud-button-root, button.mud-button-root').allInnerTexts();
    log.push(`${ts()} metric-strip=${metric} board=${board} ops-grid=${grid} progress=${prog}`);
    if (alerts.length) log.push(`${ts()} alerts: ${JSON.stringify(alerts)}`);
    log.push(`${ts()} buttons: ${JSON.stringify(btns.slice(0, 15))}`);
    const bodyText = await page.evaluate(() => document.body.innerText.slice(0, 1500));
    log.push(`${ts()} body: ${JSON.stringify(bodyText)}`);
    await page.screenshot({ path: 'diag-dashboard.png' });
    log.push(`${ts()} screenshot saved`);
  } finally {
    await browser.close();
    cleanupAll();
  }
  console.log(log.join('\n'));
})().catch((e) => { console.error(e); process.exit(1); });
