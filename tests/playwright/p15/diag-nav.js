'use strict';

const { chromium } = require('@playwright/test');
const { seedCase, cleanupAll, readAdminCredentials } = require('./seed');

const BASE = 'http://localhost:5083';
const PASSWORD = 'P15-e2e-pass-2026';

(async () => {
  const state = seedCase([{ key: 'u1', projects: ['A', 'B'] }]);
  const admin = readAdminCredentials();
  const browser = await chromium.launch();
  const out = [];
  const log = (s) => { out.push(s); };
  try {
    const ctxU1 = await browser.newContext();
    const pageU1 = await ctxU1.newPage();
    const loads = { n: 0 };
    pageU1.on('request', (r) => { if (r.resourceType() === 'document') loads.n += 1; });

    await pageU1.goto(`${BASE}/login`);
    await pageU1.fill('input[name="login"]', state.users.u1.login);
    await pageU1.fill('input[name="password"]', PASSWORD);
    await Promise.all([
      pageU1.waitForURL(/\/dashboard/),
      pageU1.click('form[action="/v1/auth/cookie-login"] button[type="submit"]'),
    ]);
    log(`after login url=${pageU1.url()}`);
    await pageU1.goto(`${BASE}/board`);
    await pageU1.locator('.kanban-board').waitFor({ state: 'visible', timeout: 30000 });
    log(`board loaded; markerA=${await pageU1.locator('.beacon-card-title', { hasText: state.markerA }).count()}`);

    const ctxAdmin = await browser.newContext();
    const pageAdmin = await ctxAdmin.newPage();
    await pageAdmin.goto(`${BASE}/login`);
    await pageAdmin.fill('input[name="login"]', admin.login);
    await pageAdmin.fill('input[name="password"]', admin.password);
    await Promise.all([
      pageAdmin.waitForURL(/\/dashboard/),
      pageAdmin.click('form[action="/v1/auth/cookie-login"] button[type="submit"]'),
    ]);
    log(`admin url=${pageAdmin.url()}`);
    const resp = await ctxAdmin.request.delete(`${BASE}/v1/projects/${state.projectA}/members/${state.users.u1.id}`);
    log(`revoke A -> ${resp.status()}`);

    // --- the failing interaction: drawer link to /dashboard ---
    const links = await pageU1.locator('.beacon-drawer a[href="/dashboard"]').count();
    log(`drawer dashboard links: ${links}`);
    if (links > 0) {
      await pageU1.click('.beacon-drawer a[href="/dashboard"]');
      await pageU1.waitForTimeout(6000);
      log(`after drawer click: url=${pageU1.url()} docLoads=${loads.n}`);
      log(`  page-header titles: ${await pageU1.locator('.beacon-page-header .beacon-task-title').count()}`);
      const title = await pageU1.locator('.beacon-page-header .beacon-task-title').first().textContent().catch(() => null);
      log(`  first title text: ${JSON.stringify(title)}`);
      log(`  kanban-board: ${await pageU1.locator('.kanban-board').count()}`);
      const bodyText = await pageU1.evaluate(() => document.body.innerText.slice(0, 600));
      log(`  body text: ${JSON.stringify(bodyText)}`);
    }

    // --- control: full load of /dashboard ---
    await pageU1.goto(`${BASE}/dashboard`);
    await pageU1.waitForTimeout(6000);
    log(`after full goto /dashboard: url=${pageU1.url()} docLoads=${loads.n}`);
    log(`  page-header titles: ${await pageU1.locator('.beacon-page-header .beacon-task-title').count()}`);
    const title2 = await pageU1.locator('.beacon-page-header .beacon-task-title').first().textContent().catch(() => null);
    log(`  first title text: ${JSON.stringify(title2)}`);
  } finally {
    await browser.close();
    cleanupAll();
  }
  console.log(out.join('\n'));
})().catch((e) => { console.error(e); process.exit(1); });
