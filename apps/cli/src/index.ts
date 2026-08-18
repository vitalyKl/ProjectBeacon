#!/usr/bin/env tsx

import { pathToFileURL } from "node:url";

import { loadOtelConfig, writeLog } from "@beacon/shared";

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
  const otel = loadOtelConfig(process.env, "beacon-cli");
  if (otel.enabled) {
    writeLog({
      level: "info",
      msg: "otel enabled",
      endpoint: otel.endpoint,
      sample_ratio: otel.sampleRatio,
    });
  }
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
