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
  const log = (s) => out.push(s);
  try {
    const ctxU1 = await browser.newContext();
    const pageU1 = await ctxU1.newPage();
    const loads = { n: 0 };
    const wss = [];
    pageU1.on('request', (r) => {
      if (r.resourceType() === 'document') {
        loads.n += 1;
        log(`  [doc-load] ${r.url()}`);
      }
      if (r.url().includes('/_blazor?')) {
        wss.push({ url: r.url(), at: Date.now() });
        log(`  [ws-open] ${r.url()}`);
      }
    });
    pageU1.on('response', (res) => {
      if (res.request().resourceType() === 'document') {
        log(`  [doc-resp] ${res.status()} ${res.url()}`);
      }
    });
    pageU1.on('pageerror', (e) => log(`  [pageerror] ${e.message.split('\n')[0]}`));
    pageU1.on('console', (m) => {
      if (m.type() === 'error') log(`  [console-error] ${m.text().slice(0, 300)}`);
    });

    await pageU1.goto(`${BASE}/login`);
    await pageU1.fill('input[name="login"]', state.users.u1.login);
    await pageU1.fill('input[name="password"]', PASSWORD);
    await Promise.all([
      pageU1.waitForURL(/\/dashboard/),
      pageU1.click('form[action="/v1/auth/cookie-login"] button[type="submit"]'),
    ]);
    await pageU1.goto(`${BASE}/board`);
    await pageU1.locator('.kanban-board').waitFor({ state: 'visible', timeout: 30000 });
    log(`board ok; ws count=${wss.length} docLoads=${loads.n}`);

    const ctxAdmin = await browser.newContext();
    const pageAdmin = await ctxAdmin.newPage();
    await pageAdmin.goto(`${BASE}/login`);
    await pageAdmin.fill('input[name="login"]', admin.login);
    await pageAdmin.fill('input[name="password"]', admin.password);
    await Promise.all([
      pageAdmin.waitForURL(/\/dashboard/),
      pageAdmin.click('form[action="/v1/auth/cookie-login"] button[type="submit"]'),
    ]);
    const resp = await ctxAdmin.request.delete(`${BASE}/v1/projects/${state.projectA}/members/${state.users.u1.id}`);
    log(`revoke A -> ${resp.status()}`);

    // Full load of /dashboard after revocation (same as the failing suite step).
    await pageU1.goto(`${BASE}/dashboard`);
    await pageU1.waitForLoadState('load');
    const probe = async (label) => {
      const info = await pageU1.evaluate(() => {
        const q = (sel) => document.querySelectorAll(sel).length;
        const main = document.querySelector('main');
        const header = document.querySelector('.beacon-page-header');
        return {
          url: location.href,
          pageHeader: q('.beacon-page-header'),
          pageTitle: q('.beacon-page-header .beacon-task-title'),
          progressLinear: q('.mud-progress-linear'),
          alerts: q('.mud-alert'),
          alertText: document.querySelector('.mud-alert')?.innerText?.slice(0, 200) ?? null,
          mainExists: !!main,
          mainLen: main?.innerHTML.length ?? -1,
          mainHead: main?.innerHTML.slice(0, 900) ?? null,
          headerText: header?.innerText?.slice(0, 120) ?? null,
        };
      });
      log(`--- ${label}: url=${info.url} pageHeader=${info.pageHeader} pageTitle=${info.pageTitle} progress=${info.progressLinear} alerts=${info.alerts}`);
      if (info.alertText) log(`  alert: ${info.alertText}`);
      if (info.headerText) log(`  header text: ${JSON.stringify(info.headerText)}`);
      log(`  main len=${info.mainLen}`);
      if (info.mainHead) log(`  main head: ${info.mainHead}`);
    };
    await pageU1.waitForTimeout(4000);
    await probe('after 4s');
    await pageU1.waitForTimeout(12000);
    await probe('after 16s');
    log(`ws count=${wss.length} docLoads=${loads.n}`);
  } finally {
    await browser.close();
    cleanupAll();
  }
  console.log(out.join('\n'));
})().catch((e) => { console.error(e); process.exit(1); });
