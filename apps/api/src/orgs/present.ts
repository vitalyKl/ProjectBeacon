import type { UserRecord } from "../auth/identity.js";
import type {
  OrgInviteRecord,
  OrgMemberRecord,
  OrgRecord,
  ProjectInviteRecord,
  ProjectMemberRecord,
  ProjectRecord,
} from "./types.js";

export function presentOrg(org: OrgRecord) {
  return {
    id: org.id,
    slug: org.slug,
    name: org.name,
    kind: org.kind,
    created_at: org.createdAt.toISOString(),
  };
}

export function presentOrgMember(member: OrgMemberRecord, user?: UserRecord) {
  return {
    org_id: member.orgId,
    user_id: member.userId,
    role: member.role,
    login: user?.login ?? null,
    email: user?.email ?? null,
    name: user?.name ?? null,
  };
}

export function presentOrgInvite(invite: OrgInviteRecord) {
  return {
    id: invite.id,
    org_id: invite.orgId,
    email: invite.email,
    github_login: invite.githubLogin,
    role: invite.role,
    expires_at: invite.expiresAt.toISOString(),
    accepted_at: invite.acceptedAt ? invite.acceptedAt.toISOString() : null,
  };
}

export function presentProject(project: ProjectRecord) {
  return {
    id: project.id,
    org_id: project.orgId,
    slug: project.slug,
    name: project.name,
    description: project.description,
    visibility: project.visibility,
    default_repo_id: project.defaultRepoId,
    settings: project.settings,
    deleted_at: project.deletedAt ? project.deletedAt.toISOString() : null,
    created_at: project.createdAt.toISOString(),
    updated_at: project.updatedAt.toISOString(),
  };
}

export function presentProjectMember(member: ProjectMemberRecord, user?: UserRecord) {
  return {
    project_id: member.projectId,
    user_id: member.userId,
    role: member.role,
    login: user?.login ?? null,
    email: user?.email ?? null,
    name: user?.name ?? null,
    created_at: member.createdAt.toISOString(),
  };
}

export function presentProjectInvite(invite: ProjectInviteRecord) {
  return {
    id: invite.id,
    project_id: invite.projectId,
    email: invite.email,
    github_login: invite.githubLogin,
    role: invite.role,
    invited_by: invite.invitedBy,
    expires_at: invite.expiresAt.toISOString(),
    accepted_at: invite.acceptedAt ? invite.acceptedAt.toISOString() : null,
  };
}
