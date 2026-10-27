'use strict';

const { chromium } = require('@playwright/test');
const { seedCase, cleanupAll } = require('./seed');

const BASE = 'http://localhost:5083';
const PASSWORD = 'P15-e2e-pass-2026';

(async () => {
  const state = seedCase([{ key: 'u1', projects: ['A'] }]);
  const browser = await chromium.launch();
  const out = [];
  try {
    const ctx = await browser.newContext();
    const page = await ctx.newPage();
    page.on('request', (r) => out.push(`REQ ${r.resourceType()} ${r.url()}`));
    page.on('websocket', (ws) => out.push(`WS-OPEN ${ws.url()}`));
    await page.goto(`${BASE}/login`);
    await page.fill('input[name="login"]', state.users.u1.login);
    await page.fill('input[name="password"]', PASSWORD);
    await Promise.all([
      page.waitForURL(/\/dashboard/),
      page.click('form[action="/v1/auth/cookie-login"] button[type="submit"]'),
    ]);
    await page.goto(`${BASE}/board`);
    await page.waitForTimeout(8000);
  } finally {
    await browser.close();
    cleanupAll();
  }
  const interesting = out.filter((l) => /blazor|negotiate|websocket|WS-OPEN|document/i.test(l));
  console.log(interesting.join('\n'));
  console.log(`total requests logged: ${out.length}`);
})().catch((e) => { console.error(e); process.exit(1); });
