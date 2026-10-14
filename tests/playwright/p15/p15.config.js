'use strict';

const { defineConfig } = require('@playwright/test');

// P1.5 acceptance: Blazor InteractiveServer circuit isolation after membership changes.
// Runs against the locally self-hosted Web app (http://localhost:5083) and the local
// beacon-postgres container. One worker, no retries: the scenarios are deterministic
// and timed against the app's 60s revalidation tick.
module.exports = defineConfig({
  testDir: '.',
  testMatch: 'p15.spec.js',
  fullyParallel: false,
  workers: 1,
  retries: 0,
  timeout: 8 * 60 * 1000,
  reporter: [['list']],
  use: {
    baseURL: 'http://localhost:5083',
    headless: true,
    viewport: { width: 1366, height: 900 },
    actionTimeout: 20000,
    navigationTimeout: 30000,
  },
});
