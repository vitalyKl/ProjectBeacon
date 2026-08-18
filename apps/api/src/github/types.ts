export type GithubIssuesMode = "off" | "import";

export type GithubInstallationRecord = {
  id: string;
  orgId: string;
  installationId: bigint;
  accountLogin: string;
  createdAt: Date;
};

export type GithubSyncStateRecord = {
  repoId: string;
  lastCursor: string | null;
  lastSyncedAt: Date | null;
};

export type ImportedGithubIssue = {
  githubIssueId: bigint;
  number: number;
  title: string;
  body: string;
  state: "open" | "closed";
  htmlUrl: string;
};
