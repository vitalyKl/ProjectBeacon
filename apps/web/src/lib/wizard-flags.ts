import { connection } from "next/server";

export type PublicWizardFlags = {
  workspaceEnabled: boolean;
  githubAppEnabled: boolean;
};

export async function publicWizardFlags(): Promise<PublicWizardFlags> {
  await connection();
  return {
    workspaceEnabled: Boolean(process.env.BEACON_WORKSPACE?.trim()),
    githubAppEnabled: Boolean(
      process.env.GITHUB_APP_ID?.trim() && process.env.GITHUB_APP_PRIVATE_KEY?.trim(),
    ),
  };
}
