'use strict';

const { defineConfig } = require('@playwright/test');

// P2 streaming acceptance: chat UI convergence (5 s safety net) and the SSE
// events endpoint, against the locally self-hosted Web app (http://localhost:5083)
// and the local beacon-postgres container. One worker, no retries: the scenarios
// are deterministic and timed against the app's 5 s poll safety net.
module.exports = defineConfig({
  testDir: '.',
  testMatch: 'chat.spec.js',
  fullyParallel: false,
  workers: 1,
  retries: 0,
  timeout: 5 * 60 * 1000,
  reporter: [['list']],
  use: {
    baseURL: 'http://localhost:5083',
    headless: true,
    viewport: { width: 1366, height: 900 },
    actionTimeout: 20000,
    navigationTimeout: 30000,
  },
});