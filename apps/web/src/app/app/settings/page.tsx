import { publicFlags } from "@/lib/flags";

import { SettingsClient } from "./settings-client";

export const dynamic = "force-dynamic";

export default async function SettingsPage() {
  const flags = await publicFlags();
  return <SettingsClient hostedClone={flags.hostedClone} />;
}
