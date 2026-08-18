export function isSidecarTunnelEnabled(env: NodeJS.ProcessEnv = process.env): boolean {
  return env["ff.sidecar_tunnel"] === "true" || env["FF_SIDECAR_TUNNEL"] === "true";
}
