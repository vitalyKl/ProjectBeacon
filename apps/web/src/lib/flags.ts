import { connection } from "next/server";

export type PublicFlags = {
  hostedClone: boolean;
  sidecarTunnel: boolean;
};

function envEnabled(...names: string[]): boolean {
  return names.some((name) => process.env[name] === "true");
}

export async function publicFlags(): Promise<PublicFlags> {
  // Request-time: compose / host set operator flags at runtime.
  await connection();
  return {
    hostedClone: envEnabled("ff.hosted_clone", "FF_HOSTED_CLONE"),
    sidecarTunnel: envEnabled("ff.sidecar_tunnel", "FF_SIDECAR_TUNNEL"),
  };
}
