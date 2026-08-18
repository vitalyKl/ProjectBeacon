import { connection } from "next/server";

export type PublicFlags = {
  hostedClone: boolean;
  sidecarTunnel: boolean;
};

function envEnabled(...names: string[]): boolean {
  return names.some((name) => process.env[name] === "true");
}

export async function publicFlags(): Promise<PublicFlags> {
  await connection();
  return {
    // FF_* is the Compose-safe alias; ff.* matches the operator flag name.
    hostedClone: envEnabled("ff.hosted_clone", "FF_HOSTED_CLONE"),
    sidecarTunnel: envEnabled("ff.sidecar_tunnel", "FF_SIDECAR_TUNNEL"),
  };
}
