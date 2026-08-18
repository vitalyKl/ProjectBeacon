import { publicWizardFlags } from "@/lib/wizard-flags";

import { ProjectWizard } from "./wizard";

export const dynamic = "force-dynamic";

export default async function NewProjectPage() {
  const flags = await publicWizardFlags();
  return (
    <ProjectWizard
      workspaceEnabled={flags.workspaceEnabled}
      githubAppEnabled={flags.githubAppEnabled}
    />
  );
}
