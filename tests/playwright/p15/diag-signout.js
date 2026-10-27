'use strict';
// Diagnoses the S2+S3b behavior: tracks every document load and every Blazor
// WebSocket event with timestamps while the admin revokes the user's only
// membership, to explain why the previous run observed an extra document load
// on sign-out.
const { chromium } = require('@playwright/test');
const { seedCase, cleanupAll, readAdminCredentials } = require('./seed');

const BASE = 'http://localhost:5083';
const PASSWORD = 'P15-e2e-pass-2026';

const log = [];
const t0 = Date.now();
const ts = () => `+${((Date.now() - t0) / 1000).toFixed(1)}s`;

(async () => {
  const state = seedCase([{ key: 'u2', projects: ['A'] }]);
  const browser = await chromium.launch();
  try {
    const ctx = await browser.newContext();
    const page = await ctx.newPage();
    page.on('request', (r) => {
      if (r.resourceType() === 'document') log.push(`${ts()} DOC ${r.url()}`);
    });
    page.on('websocket', (ws) => {
      log.push(`${ts()} WS-OPEN ${ws.url()}`);
      let frames = 0;
      ws.on('framereceived', () => frames++);
      ws.on('framesent', () => frames++);
      ws.on('close', () => log.push(`${ts()} WS-CLOSE ${ws.url()} frames=${frames}`));
    });
    page.on('console', (m) => {
      if (m.type() === 'error') log.push(`${ts()} CONSOLE-ERR ${m.text().slice(0, 300)}`);
    });
    page.on('crash', () => log.push(`${ts()} PAGE-CRASH`));

    await page.goto(`${BASE}/login`);
    await page.fill('input[name="login"]', state.users.u2.login);
    await page.fill('input[name="password"]', PASSWORD);
    await Promise.all([
      page.waitForURL(/\/dashboard/),
      page.click('form[action="/v1/auth/cookie-login"] button[type="submit"]'),
    ]);
    await page.goto(`${BASE}/board`);
    await page.locator('.kanban-board').waitFor({ state: 'visible', timeout: 30000 });
    log.push(`${ts()} board visible`);

    const admin = readAdminCredentials();
    const ctxAdmin = await browser.newContext();
    const pageAdmin = await ctxAdmin.newPage();
    await pageAdmin.goto(`${BASE}/login`);
    await pageAdmin.fill('input[name="login"]', admin.login);
    await pageAdmin.fill('input[name="password"]', admin.password);
    await Promise.all([
      pageAdmin.waitForURL(/\/dashboard/),
      pageAdmin.click('form[action="/v1/auth/cookie-login"] button[type="submit"]'),
    ]);
    const del = await ctxAdmin.request.delete(
      `${BASE}/v1/projects/${state.projectA}/members/${state.users.u2.id}`,
    );
    log.push(`${ts()} revoke -> ${del.status()}`);

    try {
      await page.waitForURL(/\/login/, { timeout: 100000 });
      log.push(`${ts()} URL is now /login`);
      await page.waitForTimeout(3000);
    } catch {
      log.push(`${ts()} TIMEOUT: still at ${page.url()}`);
    }
  } finally {
    await browser.close();
    cleanupAll();
  }
  console.log(log.join('\n'));
})().catch((e) => { console.error(e); process.exit(1); });
