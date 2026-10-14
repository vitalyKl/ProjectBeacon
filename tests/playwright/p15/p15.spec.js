'use strict';

const { test, expect } = require('@playwright/test');
const { seedCase, cleanupCase, cleanupAll, readAdminCredentials } = require('./seed');
const { startProxy } = require('./ws-proxy');

const PROXY_URL = 'http://127.0.0.1:5084';

// The circuit revalidation tick is 60s (CircuitRevalidatingAuthenticationStateProvider).
// Deadline = one tick + 35s technical margin. The app interval is not modified.
const FLIP_WAIT_MS = 95_000;
const POLL_STEP_MS = 5_000;

test.afterAll(() => {
  cleanupAll();
});

// ---------------------------------------------------------------------------
// Instrumentation
// ---------------------------------------------------------------------------

/**
 * Tracks document loads (full reloads) and Blazor circuit WebSockets on a page.
 * A live InteractiveServer circuit is a WebSocket on /_blazor?id=<circuitId>;
 * SignalR keep-alive and render frames keep flowing both ways while it is alive.
 */
function trackPage(page) {
  const t = { documentLoads: 0, circuits: [], firstCircuit: null, firstCircuitAt: 0 };
  page.on('request', (req) => {
    if (req.resourceType() === 'document') t.documentLoads += 1;
  });
  page.on('websocket', (ws) => {
    let url;
    try {
      url = new URL(ws.url());
    } catch {
      return;
    }
    const id = url.searchParams.get('id');
    if (url.pathname !== '/_blazor' || !id) return;
    const entry = { ws, id, sent: 0, received: 0, closed: false };
    ws.on('framesent', () => { entry.sent += 1; });
    ws.on('framereceived', () => { entry.received += 1; });
    ws.on('close', () => { entry.closed = true; });
    t.circuits.push(entry);
    if (!t.firstCircuit) {
      t.firstCircuit = entry;
      t.firstCircuitAt = Date.now();
    }
  });
  return t;
}

async function login(page, loginName, password, baseUrl = '') {
  await page.goto(baseUrl + '/login');
  await page.fill('input[name="login"]', loginName);
  await page.fill('input[name="password"]', password);
  await Promise.all([
    page.waitForURL(/\/dashboard(?:[?/]|$)/),
    page.click('form[action="/v1/auth/cookie-login"] button[type="submit"]'),
  ]);
}

async function expectBoardLoaded(page) {
  await page.locator('.kanban-board').waitFor({ state: 'visible', timeout: 30000 });
}

async function openBoard(page, baseUrl = '') {
  await page.goto(baseUrl + '/board');
  await expectBoardLoaded(page);
}

function markerCard(page, marker) {
  return page.locator('.beacon-card-title', { hasText: marker });
}

/**
 * In-circuit navigation (Blazor renders the route, no document reload):
 * dashboard -> board re-runs the board's OnInitialized against the circuit's
 * current scoped tenant.
 */
async function rerenderBoardInCircuit(page) {
  await page.click('.beacon-drawer a[href="/dashboard"]');
  // The dashboard body is settled when either the metric strip (a current
  // project resolved) or the Create-project CTA renders. Before the 60s tick,
  // the circuit is still scoped to the revoked project A: RLS on
  // ProjectMembers is project-keyed, so the remaining membership in B is not
  // visible to the scoped query and the pulse legitimately resolves to null.
  // That pre-rebind state is not a hang.
  await page.locator('.beacon-metric-strip, a[href="/projects/new"]')
    .waitFor({ state: 'visible', timeout: 20000 });
  await page.click('.beacon-drawer a[href="/board"]');
  // The board's settled state is either the kanban (a current project resolved)
  // or the create-project CTA: before the 60s rebind tick the circuit's scope
  // still points at the revoked project A, and RLS (GUC-keyed) hides the
  // remaining membership in B, so GetCurrentProject legitimately resolves to
  // null. Waiting for .kanban-board alone would hang in that window.
  await page.locator('.kanban-board, a[href="/projects/new"]')
    .waitFor({ state: 'visible', timeout: 20000 });
}

/** Proves a live circuit: a /_blazor WS exists and carries frames both ways. */
async function expectLiveCircuit(t, timeoutMs = 20000) {
  await expect.poll(() => t.firstCircuit, { timeout: timeoutMs }).not.toBeNull();
  const circuit = t.firstCircuit;
  await expect.poll(() => circuit.received > 0 && circuit.sent > 0, { timeout: timeoutMs }).toBe(true);
  return circuit;
}

/** Revokes the user's membership in the project from the admin's browser session. */
async function revokeMembership(context, projectId, userId) {
  const resp = await context.request.delete(`/v1/projects/${projectId}/members/${userId}`);
  expect(resp.status(), `revocation should return 204, got ${resp.status()}: ${await resp.text()}`).toBe(204);
}

/**
 * Polls the board via in-circuit navigation until it shows marker B and no longer
 * shows marker A. Returns the elapsed ms, or -1 when the deadline passes.
 */
async function pollForBoardFlip(page, markerA, markerB, deadlineMs) {
  const start = Date.now();
  while (Date.now() - start < deadlineMs) {
    await rerenderBoardInCircuit(page);
    const a = await markerCard(page, markerA).count();
    const b = await markerCard(page, markerB).count();
    if (a === 0 && b > 0) return Date.now() - start;
    await page.waitForTimeout(POLL_STEP_MS);
  }
  return -1;
}

// ---------------------------------------------------------------------------
// S1 + S2 + S3a: remaining membership -> circuit flips to B, no reload
// ---------------------------------------------------------------------------

test('S1+S2+S3a: revoking project A flips the live circuit to remaining project B without a reload', async ({ browser }) => {
  const state = seedCase([{ key: 'u1', projects: ['A', 'B'] }]);
  const contexts = [];
  try {
    const admin = readAdminCredentials();

    const ctxU1 = await browser.newContext();
    contexts.push(ctxU1);
    const pageU1 = await ctxU1.newPage();
    const t1 = trackPage(pageU1);

    await login(pageU1, state.users.u1.login, state.password);
    await openBoard(pageU1);
    await expect(markerCard(pageU1, state.markerA)).toBeVisible();
    await expectLiveCircuit(t1);
    const boardCircuit = t1.circuits[t1.circuits.length - 1];
    const loadsAtStart = t1.documentLoads;
    const circuitsAtStart = t1.circuits.length;
    const framesAtStart = boardCircuit.sent + boardCircuit.received;

    const ctxAdmin = await browser.newContext();
    contexts.push(ctxAdmin);
    const pageAdmin = await ctxAdmin.newPage();
    await login(pageAdmin, admin.login, admin.password);

    const revokedAt = Date.now();
    await revokeMembership(ctxAdmin, state.projectA, state.users.u1.id);

    const flipAt = await pollForBoardFlip(pageU1, state.markerA, state.markerB, FLIP_WAIT_MS);
    expect(flipAt, 'the board should flip to B within 95s of the revocation (60s tick + margin)').not.toBe(-1);

    // Circuit persistence proof: no document reload, no new circuit WS, and the
    // board circuit kept carrying frames across the flip.
    expect(t1.documentLoads, 'no full document reload should occur during the flip').toBe(loadsAtStart);
    expect(t1.circuits.length, 'no new circuit WS should open during the flip').toBe(circuitsAtStart);
    expect(
      boardCircuit.received + boardCircuit.sent,
      'frame activity must continue on the original board circuit',
    ).toBeGreaterThan(framesAtStart);

    // The HTTP path agrees with the circuit: with the same (stale-claim) session,
    // project A is no longer reachable (middleware live-resolves the tenant per request).
    const respA = await ctxU1.request.get(`/v1/projects/${state.projectA}`);
    expect([403, 404], `GET /v1/projects/A after revocation should be 403/404, got ${respA.status()}`).toContain(respA.status());

    console.log(
      `[p15 S3a] flip to B ${Math.round((flipAt) / 1000)}s after revocation (tick=60s); ` +
      `board circuit id=${boardCircuit.id}; ws frames rx=${boardCircuit.received} tx=${boardCircuit.sent}; ` +
      `document reloads=0; revokedAt=${new Date(revokedAt).toISOString()}`,
    );
  } finally {
    await Promise.all(contexts.map((c) => c.close().catch(() => {})));
    cleanupCase();
  }
});

// ---------------------------------------------------------------------------
// S2 + S3b: no remaining membership -> circuit goes anonymous, protected ops unavailable
// ---------------------------------------------------------------------------

test('S2+S3b: revoking the only membership signs the circuit out in-circuit; protected routes stay closed', async ({ browser }) => {
  const state = seedCase([{ key: 'u2', projects: ['A'] }]);
  const contexts = [];
  try {
    const admin = readAdminCredentials();

    const ctxU2 = await browser.newContext();
    contexts.push(ctxU2);
    const pageU2 = await ctxU2.newPage();
    const t2 = trackPage(pageU2);

    await login(pageU2, state.users.u2.login, state.password);
    await openBoard(pageU2);
    await expect(markerCard(pageU2, state.markerA)).toBeVisible();
    await expectLiveCircuit(t2);
    const boardCircuit = t2.circuits[t2.circuits.length - 1];
    const loadsAtStart = t2.documentLoads;

    const ctxAdmin = await browser.newContext();
    contexts.push(ctxAdmin);
    const pageAdmin = await ctxAdmin.newPage();
    await login(pageAdmin, admin.login, admin.password);

    const revokedAt = Date.now();
    await revokeMembership(ctxAdmin, state.projectA, state.users.u2.id);

    // The 60s tick returns false -> circuit auth state flips anonymous ->
    // AuthorizeRouteView NotAuthorized -> RedirectToLogin -> Navigation.NavigateTo(
    // "/login", forceLoad: true). That is a FULL document load by design (verified
    // in diag-signout.js: flip at ~61.7s, old circuit torn down, new circuit opens).
    const circuitsAtFlip = t2.circuits.length;
    await pageU2.waitForURL(/\/login/, { timeout: FLIP_WAIT_MS });
    const signoutAt = Date.now();
    await expect.poll(() => t2.documentLoads, { timeout: 10000 })
      .toBeGreaterThan(loadsAtStart);
    // The old circuit is torn down with the document; /login (InteractiveServer)
    // opens a brand-new circuit. Playwright does not reliably emit 'close' for
    // sockets destroyed by unload, so the new circuit is the observable proof.
    await expect.poll(() => t2.circuits.length, { timeout: 10000 })
      .toBeGreaterThan(circuitsAtFlip);

    // A fresh full load of a protected route must also land on /login: the circuit
    // rebind handler revalidates on open and immediately flips the circuit anonymous.
    await pageU2.goto('/board');
    await pageU2.waitForURL(/\/login/, { timeout: 30000 });
    await expect(pageU2.locator('form[action="/v1/auth/cookie-login"]')).toBeVisible();

    console.log(
      `[p15 S3b] anonymous flip ${Math.round((signoutAt - revokedAt) / 1000)}s after revocation (tick=60s); ` +
      `board circuit id=${boardCircuit.id}; full document load to /login; new circuit opened; ` +
      `fresh /board load lands on /login (0s rebind on open)`,
    );
  } finally {
    await Promise.all(contexts.map((c) => c.close().catch(() => {})));
    cleanupCase();
  }
});

// ---------------------------------------------------------------------------
// S4: reconnect after membership change -> tenant updates immediately (0s)
// ---------------------------------------------------------------------------

test('S4: circuit reconnect after membership change rebinds the tenant immediately (before the first 60s tick)', async ({ browser }) => {
  const state = seedCase([{ key: 'u3', projects: ['A', 'B'] }]);
  const contexts = [];
  const proxy = startProxy();
  try {
    const admin = readAdminCredentials();

    // The user page goes through the local TCP proxy so the test can abruptly
    // destroy the /_blazor WebSocket and force the client's reconnect path.
    // (context.setOffline does NOT tear down open WebSockets in Chromium.)
    const ctx = await browser.newContext();
    contexts.push(ctx);
    const page = await ctx.newPage();
    const t = trackPage(page);

    await login(page, state.users.u3.login, state.password, PROXY_URL);
    await openBoard(page, PROXY_URL);
    await expect(markerCard(page, state.markerA)).toBeVisible();
    const circuit = await expectLiveCircuit(t);

    const ctxAdmin = await browser.newContext();
    contexts.push(ctxAdmin);
    const pageAdmin = await ctxAdmin.newPage();
    await login(pageAdmin, admin.login, admin.password);
    await revokeMembership(ctxAdmin, state.projectA, state.users.u3.id);

    // Destroy the tracked circuit sockets: the Blazor client auto-reconnects the
    // SAME circuit over a new WebSocket. The rebind handler runs on
    // connection-up, so the scoped tenant must already be B at the next
    // in-circuit render.
    const socketsBefore = t.circuits.length;
    const dropped = await proxy.drop();
    console.log(`[p15 S4] proxy dropped ${dropped} circuit socket(s)`);

    await expect.poll(() => t.circuits.length, {
      timeout: 45_000,
      message: 'the circuit should reconnect over a new WebSocket',
    }).toBeGreaterThan(socketsBefore);
    const newEntry = t.circuits[t.circuits.length - 1];
    await expect.poll(() => newEntry.received > 0, { timeout: 30_000 }).toBe(true);
    const reopenedAt = Date.now();

    await rerenderBoardInCircuit(page);
    await expect(markerCard(page, state.markerB)).toBeVisible();
    await expect(markerCard(page, state.markerA)).toHaveCount(0);
    const checkedAt = Date.now();

    // Attribution proof: the flip was observed before the first 60s tick could fire
    // (the tick starts at circuit open), so it came from the reconnect rebind.
    expect(
      checkedAt - t.firstCircuitAt,
      'the flip must precede the first 60s tick to prove the reconnect rebind',
    ).toBeLessThan(55_000);

    console.log(
      `[p15 S4] tenant rebind visible ${Math.round((checkedAt - reopenedAt) / 1000)}s after WS reconnect ` +
      `(${Math.round((checkedAt - t.firstCircuitAt) / 1000)}s after circuit open < 60s tick); ` +
      `board circuit id=${t.circuits[t.circuits.length - 2]?.id} reconnect ws id=${newEntry.id}`,
    );
  } finally {
    await Promise.all(contexts.map((c) => c.close().catch(() => {})));
    proxy.close();
    cleanupCase();
  }
});

// ---------------------------------------------------------------------------
// S5: two independent user sessions stay isolated
// ---------------------------------------------------------------------------

test("S5: revoking one user's membership does not affect another user's independent circuit", async ({ browser }) => {
  const state = seedCase([
    { key: 'u1', projects: ['A'] },
    { key: 'u2', projects: ['A'] },
  ]);
  const contexts = [];
  try {
    const admin = readAdminCredentials();

    const ctxA = await browser.newContext();
    contexts.push(ctxA);
    const pageA = await ctxA.newPage();
    const tA = trackPage(pageA);
    await login(pageA, state.users.u1.login, state.password);
    await openBoard(pageA);
    await expect(markerCard(pageA, state.markerA)).toBeVisible();
    await expectLiveCircuit(tA);
    const boardA = tA.circuits[tA.circuits.length - 1];
    const loadsA = tA.documentLoads;
    const circuitsAAtFlip = tA.circuits.length;

    const ctxB = await browser.newContext();
    contexts.push(ctxB);
    const pageB = await ctxB.newPage();
    const tB = trackPage(pageB);
    await login(pageB, state.users.u2.login, state.password);
    await openBoard(pageB);
    await expect(markerCard(pageB, state.markerA)).toBeVisible();
    await expectLiveCircuit(tB);
    const boardB = tB.circuits[tB.circuits.length - 1];
    const loadsB = tB.documentLoads;
    const framesBAtRevoke = boardB.sent + boardB.received;

    const ctxAdmin = await browser.newContext();
    contexts.push(ctxAdmin);
    const pageAdmin = await ctxAdmin.newPage();
    await login(pageAdmin, admin.login, admin.password);
    await revokeMembership(ctxAdmin, state.projectA, state.users.u1.id);

    // u1 (revoked, no remaining membership) is force-signed-out: the anonymous
    // circuit flips -> AuthorizeRouteView NotAuthorized -> full document load to
    // /login, which opens a brand-new circuit (InteractiveServer).
    await pageA.waitForURL(/\/login/, { timeout: FLIP_WAIT_MS });
    await expect.poll(() => tA.documentLoads, { timeout: 10000 }).toBeGreaterThan(loadsA);
    await expect.poll(() => tA.circuits.length, { timeout: 10000 }).toBeGreaterThan(circuitsAAtFlip);

    // u2's independent circuit is untouched: still authenticated, still sees A,
    // still carrying frames.
    await rerenderBoardInCircuit(pageB);
    await expect(markerCard(pageB, state.markerA)).toBeVisible();
    expect(pageB.url(), 'u2 must not be redirected to login').not.toMatch(/\/login/);
    expect(tB.documentLoads, 'u2 must not reload').toBe(loadsB);
    expect(boardB.sent + boardB.received, 'u2 circuit must keep carrying frames').toBeGreaterThan(framesBAtRevoke);

    console.log(
      `[p15 S5] u1 force-signed-out (full load to /login, new circuit); u2 circuit unaffected ` +
      `(marker A visible, frames flowing); board circuit ids: u1=${boardA.id} u2=${boardB.id}`,
    );
  } finally {
    await Promise.all(contexts.map((c) => c.close().catch(() => {})));
    cleanupCase();
  }
});
