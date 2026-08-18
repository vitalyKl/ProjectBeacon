import path from "node:path";
import { fileURLToPath } from "node:url";

import type { NextConfig } from "next";

const appDir = path.dirname(fileURLToPath(import.meta.url));

function beaconApiUrl(): string {
  return (process.env.BEACON_API_URL ?? "http://127.0.0.1:8080").replace(/\/$/, "");
}

const nextConfig: NextConfig = {
  outputFileTracingRoot: path.join(appDir, "../.."),
  async rewrites() {
    const api = beaconApiUrl();
    return [
      {
        source: "/v1/:path*",
        destination: `${api}/v1/:path*`,
      },
    ];
  },
};

export default nextConfig;
