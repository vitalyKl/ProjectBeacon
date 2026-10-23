'use strict';

const { test, expect } = require('@playwright/test');
const { seedChat, cleanupChat, seedExtraPart } = require('./seed');

// P2 streaming acceptance (Web side):
//  1. The chat page renders a seeded session and its parts over a live circuit.
//  2. A part written directly to the database (no in-process ChatEventSignal can
//     fire for a raw SQL row) appears in the UI without a reload within the 5 s
//     safety-net window.
//  3. The SSE endpoint GET /v1/chat/sessions/{id}/events delivers its `ready`
//     event to a real browser (cookie auth, same origin).
//
// The in-process signal path and the SSE `changed` event are covered by
// API.Tests (ChatEventsHttpTests); exercising them here would require a live
// workstation device, which this suite does not provision.

let fx;

test.beforeAll(() => {
  fx = seedChat();
});

test.afterAll(() => {
  cleanupChat(fx);
});

async function login(page, loginName, password) {
  await page.goto('/login');
  await page.fill('input[name="login"]', loginName);
  await page.fill('input[name="password"]', password);
  await Promise.all([
    page.waitForURL(/\/dashboard(?:[?/]|$)/),
    page.click('form[action="/v1/auth/cookie-login"] button[type="submit"]'),
  ]);
}

test('chat page renders the seeded session; a DB-inserted part appears without a reload', async ({ page }) => {
  let docLoads = 0;
  page.on('request', (req) => {
    if (req.resourceType() === 'document') docLoads += 1;
  });

  await login(page, fx.login, fx.password);
  await page.goto('/chat');

  // The session list belongs to the user's current project (the single seeded
  // membership, so the project is unambiguous).
  const sessionItem = page.locator('div.beacon-session-item', { hasText: fx.sessionTitle }).first();
  await expect(sessionItem).toBeVisible({ timeout: 20000 });
  await sessionItem.locator('div.beacon-session-main').click();

  await expect(page.getByText(fx.partOneText)).toBeVisible({ timeout: 20000 });

  // Insert a part straight into the database. A raw SQL row cannot fire the
  // in-process ChatEventSignal, so the UI must converge via the 5 s safety-net
  // poll, without any reload.
  const loadsBeforeInsert = docLoads;
  const t0 = Date.now();
  seedExtraPart(fx, 2);
  await expect(page.getByText(fx.partTwoText)).toBeVisible({ timeout: 15000 });
  const elapsed = Date.now() - t0;
  console.log(`[chat-e2e] part two rendered ${elapsed} ms after DB insert (safety net = 5000 ms)`);
  expect(elapsed, 'safety net must converge well inside 15 s').toBeLessThan(15000);
  expect(docLoads, 'no full document reload is allowed while the part appears').toBe(loadsBeforeInsert);
});

test('SSE events endpoint delivers the ready event from a real browser', async ({ page }) => {
  await login(page, fx.login, fx.password);
  await page.goto('/chat');

  const evt = await page.evaluate(
    (sessionId) =>
      new Promise((resolve, reject) => {
        const es = new EventSource(`/v1/chat/sessions/${sessionId}/events`);
        const timer = setTimeout(() => {
          es.close();
          reject(new Error('no ready event within 10 s'));
        }, 10000);
        es.addEventListener('ready', (e) => {
          clearTimeout(timer);
          es.close();
          resolve(JSON.parse(e.data));
        });
      }),
    fx.sessionId,
  );

  expect(evt.sessionId, 'ready must carry the session id').toBe(fx.sessionId);
  expect(evt.status, 'ready must carry the session status').toBe('Idle');
  console.log(`[chat-e2e] SSE ready event: sessionId=${evt.sessionId} status=${evt.status}`);
});

// P3 mobile acceptance (ui-ux review roadmap): on small viewports the dock must become a
// bottom sheet / near-full-screen panel, not a fixed 360px desktop-style box.
test('dock becomes a bottom sheet on mobile viewports', async ({ page }) => {
  await page.setViewportSize({ width: 390, height: 844 });
  await login(page, fx.login, fx.password);
  await page.goto('/dashboard');

  const fab = page.locator('.beacon-chat-fab');
  await expect(fab).toBeVisible({ timeout: 20000 });
  await fab.click();

  const dock = page.locator('.beacon-chat-dock');
  await expect(dock).toBeVisible({ timeout: 20000 });
  const box = await dock.boundingBox();
  const viewport = page.viewportSize();

  // Bottom sheet: full width, anchored to the viewport's bottom edge.
  expect(box.x, 'sheet starts at the left edge').toBe(0);
  expect(box.width, 'sheet spans the full viewport width').toBeCloseTo(viewport.width, 0);
  expect(box.y + box.height, 'sheet is anchored to the bottom edge').toBeCloseTo(viewport.height, 0);
  // At this width the sheet is bounded, not a full-screen panel.
  expect(box.height, 'sheet is bounded, not full-screen').toBeLessThan(viewport.height * 0.9);

  // The composer inside the sheet is usable.
  await expect(dock.locator('.beacon-chat-compose input')).toBeVisible();
});