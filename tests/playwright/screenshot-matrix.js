const { chromium } = require('@playwright/test');
const path = require('path');
const fs = require('fs');

const BASE = 'http://localhost:5083';
const OUT = path.resolve(__dirname, 'out');
const ENV_FILE = path.resolve(__dirname, '..', '..', '.env');
const CRED_FILE = path.join(__dirname, '.dev-admin.json');

// A task that lives in the admin's default (first-joined) project,
// so /task/{id} resolves to the project pinned in the login cookie.
const TASK_ID = '01a0ac2e-02ec-7393-8799-8a5a8769be7e';

function readEnv(file) {
  const env = {};
  for (const line of fs.readFileSync(file, 'utf8').split(/\r?\n/)) {
    const m = line.match(/^([A-Za-z_][A-Za-z0-9_]*)=(.*)$/);
    if (m) env[m[1]] = m[2].trim();
  }
  return env;
}

// Anonymous pages: shown without any auth, so capture in a fresh (cookie-less) context.
const anonRoutes = [
  { path: '/', name: 'landing' },
  { path: '/login', name: 'login' },
  { path: '/register', name: 'register' },
  { path: '/forgot', name: 'forgot' },
  { path: '/reset', name: 'reset' },
  { path: '/recover', name: 'recover' },
  { path: '/invite', name: 'invite' },
  { path: '/bootstrap', name: 'bootstrap' },
];

// Authenticated module pages: need the cookie from a real login.
const moduleRoutes = [
  { path: '/dashboard', name: 'dashboard' },
  { path: '/board', name: 'board' },
  { path: '/backlog', name: 'backlog' },
  { path: '/task/' + TASK_ID, name: 'task-detail' },
  { path: '/context', name: 'context' },
  { path: '/decisions', name: 'decisions' },
  { path: '/roadmap', name: 'roadmap' },
  { path: '/reports', name: 'reports' },
  { path: '/chat', name: 'chat' },
  { path: '/settings', name: 'settings' },
  { path: '/projects/new', name: 'projects-new' },
];

const breakpoints = [
  { name: 'desktop-1440', width: 1440, height: 900 },
  { name: 'desktop-1280', width: 1280, height: 800 },
  { name: 'tablet-1024', width: 1024, height: 768 },
  { name: 'tablet-768', width: 768, height: 1024 },
  { name: 'mobile-390', width: 390, height: 844 },
  { name: 'mobile-360', width: 360, height: 780 },
];

// Reset the admin password via the bootstrap-token break-glass endpoint and
// return the fresh credentials. Non-destructive: does not touch projects/tasks.
async function recoverAdmin() {
  const token = readEnv(ENV_FILE).BOOTSTRAP_ADMIN_TOKEN;
  if (!token) throw new Error('BOOTSTRAP_ADMIN_TOKEN not found in ' + ENV_FILE);
  const res = await fetch(BASE + '/v1/auth/recover-admin', {
    method: 'POST',
    headers: { 'Authorization': 'Bearer ' + token, 'Content-Type': 'application/json' },
    body: '{}',
  });
  const data = await res.json();
  if (!res.ok || !data.password) {
    throw new Error('recover-admin failed: HTTP ' + res.status + ' ' + JSON.stringify(data));
  }
  return { login: data.login, password: data.password };
}

// Real UI login: fill the form and submit. Stores the BeaconAuth cookie in the
// context so later navigations are authenticated.
async function login(page, login, password) {
  await page.goto(BASE + '/login', { waitUntil: 'domcontentloaded', timeout: 20000 });
  await page.waitForSelector('input[name="password"]', { timeout: 15000 });
  await page.fill('input[name="login"]', login);
  await page.fill('input[name="password"]', password);

  const nav = page.waitForURL(/\/(dashboard|login\?error)/, { timeout: 25000 });
  const submit = page.locator('button[type="submit"]').first();
  if (await submit.count()) {
    await submit.click();
  } else {
    await page.keyboard.press('Enter');
  }
  await nav;
  if (page.url().includes('error')) throw new Error('login failed: ' + page.url());
}

async function shot(page, bp, name, urlPath, results) {
  const file = path.join(OUT, bp, name + '.png');
  fs.mkdirSync(path.dirname(file), { recursive: true });
  try {
    try {
      await page.goto(BASE + urlPath, { waitUntil: 'networkidle', timeout: 12000 });
    } catch {
      // networkidle may not settle with a persistent SignalR socket; continue.
    }
    await page.waitForTimeout(1200);
    const cur = new URL(page.url());
    const requested = new URL(urlPath, BASE).pathname;
    if (cur.pathname === '/login' && requested !== '/login') {
      results.push('REDIRECT ' + bp + ' ' + urlPath + ' -> /login (unauthed)');
      return;
    }
    await page.screenshot({ path: file, fullPage: true });
    results.push('OK   ' + bp + ' ' + urlPath + ' -> ' + cur.pathname);
  } catch (e) {
    results.push('ERR  ' + bp + ' ' + urlPath + ' - ' + e.message);
  }
}

(async () => {
  console.log('Recovering admin password via /v1/auth/recover-admin ...');
  const creds = await recoverAdmin();
  fs.writeFileSync(CRED_FILE, JSON.stringify(creds, null, 2));
  console.log('  login=' + creds.login + ' (password saved to ' + CRED_FILE + ')');

  const browser = await chromium.launch();
  const results = [];

  for (const bp of breakpoints) {
    // Anonymous pass (no cookie).
    {
      const ctx = await browser.newContext({ viewport: { width: bp.width, height: bp.height } });
      const page = await ctx.newPage();
      for (const r of anonRoutes) await shot(page, bp.name, r.name, r.path, results);
      await ctx.close();
    }
    // Authenticated pass (log in once, then walk all modules).
    {
      const ctx = await browser.newContext({ viewport: { width: bp.width, height: bp.height } });
      const page = await ctx.newPage();
      await login(page, creds.login, creds.password);
      for (const r of moduleRoutes) await shot(page, bp.name, r.name, r.path, results);
      await ctx.close();
    }
  }

  await browser.close();
  const ok = results.filter((l) => l.startsWith('OK')).length;
  const warn = results.filter((l) => l.startsWith('REDIRECT')).length;
  const err = results.filter((l) => l.startsWith('ERR')).length;
  console.log(results.join('\n'));
  console.log('\nTotal: ' + results.length + ' | OK: ' + ok + ' | REDIRECT: ' + warn + ' | ERR: ' + err);
})().catch((e) => {
  console.error(e);
  process.exit(1);
});
