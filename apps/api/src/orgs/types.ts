export type OrgKind = "personal" | "team";
export type OrgRole = "owner" | "admin" | "member";
export type OrgInviteRole = "admin" | "member";
export type ProjectRole = "admin" | "write" | "read";
export type ProjectVisibility = "private";

export type OrgRecord = {
  id: string;
  slug: string;
  name: string;
  kind: OrgKind;
  createdAt: Date;
};

export type OrgMemberRecord = {
  orgId: string;
  userId: string;
  role: OrgRole;
};

export type OrgInviteRecord = {
  id: string;
  orgId: string;
  email: string | null;
  githubLogin: string | null;
  role: OrgInviteRole;
  expiresAt: Date;
  acceptedAt: Date | null;
};

export type ProjectRecord = {
  id: string;
  orgId: string;
  slug: string;
  name: string;
  description: string;
  visibility: ProjectVisibility;
  settings: Record<string, unknown>;
  deletedAt: Date | null;
  createdAt: Date;
  updatedAt: Date;
};

export type ProjectMemberRecord = {
  projectId: string;
  userId: string;
  role: ProjectRole;
  createdAt: Date;
};

export type ProjectInviteRecord = {
  id: string;
  projectId: string;
  email: string | null;
  githubLogin: string | null;
  role: ProjectRole;
  invitedBy: string;
  expiresAt: Date;
  acceptedAt: Date | null;
};

export class OrgSlugTakenError extends Error {
  override readonly name = "OrgSlugTakenError";

  constructor() {
    super("org slug is already taken");
  }
}

export class ProjectSlugTakenError extends Error {
  override readonly name = "ProjectSlugTakenError";

  constructor() {
    super("project slug is already taken");
  }
}

export class InviteTargetRequiredError extends Error {
  override readonly name = "InviteTargetRequiredError";

  constructor() {
    super("email or github_login is required");
  }
}

export const ORG_ROLES: readonly OrgRole[] = ["owner", "admin", "member"];
export const ORG_INVITE_ROLES: readonly OrgInviteRole[] = ["admin", "member"];
export const PROJECT_ROLES: readonly ProjectRole[] = ["admin", "write", "read"];

export function isOrgRole(value: string): value is OrgRole {
  return (ORG_ROLES as readonly string[]).includes(value);
}

export function isOrgInviteRole(value: string): value is OrgInviteRole {
  return (ORG_INVITE_ROLES as readonly string[]).includes(value);
}

export function isProjectRole(value: string): value is ProjectRole {
  return (PROJECT_ROLES as readonly string[]).includes(value);
}

export function orgRoleAtLeast(role: OrgRole, needed: "owner" | "admin"): boolean {
  if (needed === "owner") {
    return role === "owner";
  }
  return role === "owner" || role === "admin";
}

export function projectRoleAtLeast(role: ProjectRole, needed: ProjectRole): boolean {
  const rank = { read: 1, write: 2, admin: 3 } as const;
  return rank[role] >= rank[needed];
}
