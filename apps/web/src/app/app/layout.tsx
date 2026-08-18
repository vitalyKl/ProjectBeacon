import type { ReactNode } from "react";

import { publicFlags } from "@/lib/flags";

import { AppShell } from "./app-shell";
import { SidecarTunnelBanner } from "./sidecar-tunnel-banner";

export default async function AuthenticatedLayout({ children }: Readonly<{ children: ReactNode }>) {
  const flags = await publicFlags();
  return (
    <AppShell banner={<SidecarTunnelBanner enabled={flags.sidecarTunnel} />}>{children}</AppShell>
  );
}
