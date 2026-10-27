'use strict';

const { chromium } = require('@playwright/test');
const { seedCase, cleanupAll, readAdminCredentials } = require('./seed');
const fs = require('fs');
const path = require('path');

const BASE = 'http://localhost:5083';
const PASSWORD = 'P15-e2e-pass-2026';

async function formLogin(browser, loginName, password, label) {
  const ctx = await browser.newContext();
  const page = await ctx.newPage();
  const log = [];
  page.on('response', (res) => {
    if (res.url().includes('/v1/auth/cookie-login')) {
      log.push(`POST /v1/auth/cookie-login -> ${res.status()} location=${res.headers()['location'] ?? 'n/a'}`);
    }
  });
  page.on('request', (req) => {
    if (req.url().includes('/v1/auth/cookie-login') && req.method() === 'POST') {
      log.push(`request body: ${JSON.stringify(req.postData())}`);
    }
  });
  await page.goto(`${BASE}/login`);
  await page.fill('input[name="login"]', loginName);
  await page.fill('input[name="password"]', password);
  await page.click('form[action="/v1/auth/cookie-login"] button[type="submit"]');
  await page.waitForTimeout(4000);
  log.push(`final url: ${page.url()}`);
  console.log(`--- ${label} (${loginName})`);
  for (const line of log) console.log(line);
  await ctx.close();
}

(async () => {
  const admin = readAdminCredentials();
  const state = seedCase([{ key: 'u1', projects: ['A'] }]);
  const browser = await chromium.launch();
  try {
    // 1. Admin (known working form flow, used by the real tests)
    await formLogin(browser, admin.login, admin.password, 'admin');
    // 2. Freshly seeded user via seed.js
    await formLogin(browser, state.users.u1.login, PASSWORD, 'seeded u1');
    // 3. The manual debug user, if still present
    const { execSync } = require('child_process');
    const sql = "SELECT \"Login\" FROM \"Users\" WHERE \"Login\" = 'p15-debug-user1';";
    const out = execSync(
      'docker exec -i beacon-postgres psql -U beacon -d beacon -t -A -c "' + sql + '"',
      { encoding: 'utf8' },
    );
    if (out.trim()) {
      await formLogin(browser, 'p15-debug-user1', PASSWORD, 'manual debug user');
    } else {
      console.log('--- p15-debug-user1 no longer present');
    }
  } finally {
    await browser.close();
    cleanupAll();
  }
})().catch((e) => { console.error(e); process.exit(1); });
