const { chromium } = require('@playwright/test');
const fs = require('fs');
const path = require('path');

const BASE = 'http://localhost:5083';
const OUT = path.resolve(__dirname, 'out', 'sprint6');
const creds = JSON.parse(fs.readFileSync(path.resolve(__dirname, '.dev-admin.json'), 'utf8'));

function style(page, selector) {
  return page.locator(selector).evaluateAll((els) =>
    els.map((el) => ({
      className: el.className,
      display: getComputedStyle(el).display,
      text: (el.innerText || '').replace(/\s+/g, ' ').trim().slice(0, 80),
    })));
}

async function login(page) {
  await page.goto(BASE + '/login', { waitUntil: 'domcontentloaded', timeout: 20000 });
  await page.waitForSelector('input[name="password"]', { timeout: 15000 });
  await page.fill('input[name="login"]', creds.login);
  await page.fill('input[name="password"]', creds.password);
  const nav = page.waitForURL(/\/(dashboard|login\?error)/, { timeout: 25000 });
  await page.locator('button[type="submit"]').first().click();
  await nav;
  if (page.url().includes('error')) throw new Error('login failed');
}

async function openBoard(page) {
  await page.goto(BASE + '/board', { waitUntil: 'domcontentloaded', timeout: 20000 });
  await page.waitForSelector('.kanban-board, .mud-alert, a[href="/projects/new"]', { timeout: 15000 });
  await page.waitForTimeout(800);
}

(async () => {
  fs.mkdirSync(OUT, { recursive: true });
  const browser = await chromium.launch();
  const report = { checks: [] };
  const fail = (name, detail) => report.checks.push({ name, ok: false, detail });
  const pass = (name, detail) => report.checks.push({ name, ok: true, detail });

  const desktop = await browser.newContext({ viewport: { width: 1440, height: 900 } });
  const page = await desktop.newPage();
  await login(page);
  await openBoard(page);
  await page.screenshot({ path: path.join(OUT, 'board-desktop.png'), fullPage: true });

  const url = page.url();
  if (!url.includes('/board')) fail('board-url', url);
  else pass('board-url', url);

  const filterDisplay = await page.locator('.beacon-board-filter').evaluate((el) => getComputedStyle(el).display).catch(() => 'missing');
  if (filterDisplay === 'none') pass('desktop-filter-hidden', filterDisplay);
  else fail('desktop-filter-hidden', filterDisplay);

  const columns = await style(page, '.kanban-column');
  const visibleCols = columns.filter((c) => c.display !== 'none').length;
  if (visibleCols === 3) pass('desktop-three-columns', JSON.stringify(columns));
  else fail('desktop-three-columns', JSON.stringify(columns));

  const moveCount = await page.locator('.beacon-move-task').count();
  if (moveCount > 0) pass('move-menu-present', String(moveCount));
  else fail('move-menu-present', '0');

  if (moveCount > 0) {
    await page.locator('.beacon-move-task').first().click();
    await page.waitForTimeout(400);
    const items = await page.locator('.mud-list-item, .mud-menu-item').allInnerTexts();
    const labels = items.map((t) => t.replace(/\s+/g, ' ').trim()).filter(Boolean);
    if (labels.length >= 1) pass('move-menu-opens', labels.join(' | '));
    else fail('move-menu-opens', 'no items');
    await page.keyboard.press('Escape');
    await page.screenshot({ path: path.join(OUT, 'board-move-menu.png'), fullPage: false });
  }

  const card = page.locator('.beacon-board-card .beacon-card-title').first();
  if (await card.count()) {
    await card.click();
    await page.waitForURL(/\/task\//, { timeout: 15000 });
    await page.waitForSelector('.beacon-task-meta', { timeout: 15000 });
    await page.waitForTimeout(600);
    await page.screenshot({ path: path.join(OUT, 'task-desktop.png'), fullPage: true });
    const meta = await page.locator('.beacon-task-meta').innerText();
    const bar = await page.locator('.beacon-action-bar').innerText();
    const primary = await page.locator('.beacon-action-bar .mud-button-filled').count();
    if (meta.trim()) pass('task-meta', meta.replace(/\s+/g, ' ').trim().slice(0, 180));
    else fail('task-meta', 'empty');
    if (primary >= 1) pass('task-primary-action', bar.replace(/\s+/g, ' ').trim().slice(0, 180));
    else fail('task-primary-action', bar.replace(/\s+/g, ' ').trim().slice(0, 180));
    const tabs = await page.locator('.mud-tab').allInnerTexts();
    pass('task-tabs', tabs.map((t) => t.trim()).filter(Boolean).join(' | '));
  } else {
    fail('open-task', 'no cards');
  }
  await desktop.close();

  const mobile = await browser.newContext({ viewport: { width: 390, height: 844 } });
  const mpage = await mobile.newPage();
  await login(mpage);
  await openBoard(mpage);
  await mpage.screenshot({ path: path.join(OUT, 'board-mobile.png'), fullPage: true });
  const mFilter = await mpage.locator('.beacon-board-filter').evaluate((el) => getComputedStyle(el).display).catch(() => 'missing');
  if (mFilter !== 'none' && mFilter !== 'missing') pass('mobile-filter-visible', mFilter);
  else fail('mobile-filter-visible', mFilter);
  const mCols = await style(mpage, '.kanban-column');
  const mVisible = mCols.filter((c) => c.display !== 'none');
  if (mVisible.length === 1 && /kanban-column-todo/.test(mVisible[0].className)) pass('mobile-one-column', JSON.stringify(mCols));
  else fail('mobile-one-column', JSON.stringify(mCols));

  const inProgress = mpage.locator('.beacon-board-filter button', { hasText: /In progress|In Progress|В работе/i });
  if (await inProgress.count()) {
    await inProgress.first().click();
    await mpage.waitForTimeout(400);
    const after = await style(mpage, '.kanban-column');
    const shown = after.filter((c) => c.display !== 'none');
    if (shown.length === 1 && /kanban-column-inprogress/.test(shown[0].className)) pass('mobile-switch-column', JSON.stringify(after));
    else fail('mobile-switch-column', JSON.stringify(after));
    await mpage.screenshot({ path: path.join(OUT, 'board-mobile-inprogress.png'), fullPage: true });
  } else {
    fail('mobile-switch-column', 'filter button missing');
  }

  await mobile.close();
  await browser.close();
  const failed = report.checks.filter((c) => !c.ok);
  report.ok = failed.length === 0;
  fs.writeFileSync(path.join(OUT, 'report.json'), JSON.stringify(report, null, 2));
  console.log(JSON.stringify(report, null, 2));
  if (!report.ok) process.exit(1);
})().catch((err) => {
  console.error(err && err.stack ? err.stack : String(err));
  process.exit(1);
});
