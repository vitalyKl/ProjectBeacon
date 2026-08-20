import { publicFlags } from "@/lib/flags";

import { HomeView } from "./home-view";

export const dynamic = "force-dynamic";

export default async function AppHomePage() {
  const flags = await publicFlags();
  return <HomeView hostedClone={flags.hostedClone} />;
}
