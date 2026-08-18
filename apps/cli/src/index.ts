#!/usr/bin/env tsx

import { pathToFileURL } from "node:url";

import { runCli } from "./cli.js";

export const packageName = "@beacon/cli";

export { runCli } from "./cli.js";
export { connect } from "./connect.js";
export { serveStdio } from "./stdio.js";
export { startSidecar } from "./sidecar.js";

function isDirectRun(): boolean {
  const entry = process.argv[1];
  if (!entry) {
    return false;
  }
  try {
    return import.meta.url === pathToFileURL(entry).href;
  } catch {
    return false;
  }
}

if (isDirectRun()) {
  runCli()
    .then((code) => {
      process.exitCode = code;
    })
    .catch((error: unknown) => {
      const message = error instanceof Error ? error.message : "unexpected error";
      process.stderr.write(`${message}\n`);
      process.exitCode = 1;
    });
}
