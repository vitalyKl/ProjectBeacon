import { isUuid, uuidv7 } from "@beacon/shared";
import type { Context, Hono } from "hono";

import { requireProjectActor } from "../auth/access.js";
import type { AuthDeps } from "../auth/routes.js";
import { loadSession } from "../auth/routes.js";
import type { UserRecord } from "../auth/store.js";
import { OrgSlugTakenError, ProjectSlugTakenError } from "../auth/store.js";
import { errorJson } from "../errors.js";
import { parseEmail, parseOptionalString, readObject } from "../http.js";
import { parseSlug } from "../slug.js";
import {
  presentOrg,
  presentOrgInvite,
  presentOrgMember,
  presentProject,
  presentProjectInvite,
  presentProjectMember,
} from "./present.js";
import {
  isOrgInviteRole,
  isProjectRole,
  orgRoleAtLeast,
  projectRoleAtLeast,
  type OrgInviteRole,
  type OrgRecord,
  type ProjectRecord,
  type ProjectRole,
} from "./types.js";

const INVITE_TTL_MS = 7 * 24 * 60 * 60 * 1000;

type Authed = {
  user: UserRecord;
};

export async function requireSession(c: Context, deps: AuthDeps): Promise<Authed | Response> {
  const resolved = await loadSession(c, deps);
  if (!resolved) {
    return errorJson(c, 401, "unauthorized", "authentication required");
  }
  await deps.store.ensurePersonalOrg(resolved.user, deps.clock.now());
  return { user: resolved.user };
}

export function isResponse(value: Authed | Response): value is Response {
  return value instanceof Response;
}

async function resolveOrg(deps: AuthDeps, ref: string): Promise<OrgRecord | undefined> {
  if (isUuid(ref)) {
    const byId = await deps.store.findOrgById(ref);
    if (byId) {
      return byId;
    }
  }
  return deps.store.findOrgBySlug(ref.toLowerCase());
}

function parseInviteTarget(
  body: Record<string, unknown> | undefined,
): { email: string | null; githubLogin: string | null } | undefined {
  if (!body) {
    return undefined;
  }
  const emailRaw = body["email"];
  const githubRaw = body["github_login"];
  const email = emailRaw === undefined || emailRaw === null ? null : parseEmail(emailRaw);
  const githubLogin =
    githubRaw === undefined || githubRaw === null ? null : parseOptionalString(githubRaw, 64);
  if (emailRaw !== undefined && emailRaw !== null && !email) {
    return undefined;
  }
  if (githubRaw !== undefined && githubRaw !== null && !githubLogin) {
    return undefined;
  }
  if (!email && !githubLogin) {
    return undefined;
  }
  return { email: email ?? null, githubLogin: githubLogin ?? null };
}

function inviteMatchesUser(
  invite: { email: string | null; githubLogin: string | null },
  user: UserRecord,
): boolean {
  if (invite.email && user.email && invite.email.toLowerCase() === user.email.toLowerCase()) {
    return true;
  }
  if (
    invite.githubLogin &&
    user.login &&
    invite.githubLogin.toLowerCase() === user.login.toLowerCase()
  ) {
    return true;
  }
  return false;
}

async function requireOrgMember(
  c: Context,
  deps: AuthDeps,
  user: UserRecord,
  orgRef: string,
  needed?: "admin" | "owner",
): Promise<{ org: OrgRecord } | Response> {
  const org = await resolveOrg(deps, orgRef);
  if (!org) {
    return errorJson(c, 404, "not_found", "org not found");
  }
  const member = await deps.store.findOrgMember(org.id, user.id);
  if (!member) {
    return errorJson(c, 404, "not_found", "org not found");
  }
  if (needed && !orgRoleAtLeast(member.role, needed)) {
    return errorJson(c, 403, "forbidden", "org admin required");
  }
  return { org };
}

export async function requireProjectAccess(
  c: Context,
  deps: AuthDeps,
  user: UserRecord,
  projectId: string,
  needed: ProjectRole,
): Promise<{ project: ProjectRecord; role: ProjectRole } | Response> {
  if (!isUuid(projectId)) {
    return errorJson(c, 404, "not_found", "project not found");
  }
  const project = await deps.store.findProjectById(projectId);
  if (!project || project.deletedAt) {
    return errorJson(c, 404, "not_found", "project not found");
  }
  const member = await deps.store.findProjectMember(project.id, user.id);
  if (!member) {
    return errorJson(c, 404, "not_found", "project not found");
  }
  if (!projectRoleAtLeast(member.role, needed)) {
    return errorJson(c, 403, "forbidden", "insufficient project role");
  }
  return { project, role: member.role };
}

export function mountOrgs(app: Hono, deps: AuthDeps): void {
  app.post("/v1/orgs", async (c) => {
    const session = await requireSession(c, deps);
    if (isResponse(session)) {
      return session;
    }

    const body = await readObject(c);
    const slug = parseSlug(body?.["slug"]);
    const name = parseOptionalString(body?.["name"], 120);
    if (!slug || !name) {
      return errorJson(c, 400, "unauthorized", "slug and name are required", {
        reason: "invalid_body",
      });
    }

    const now = deps.clock.now();
    try {
      const org = await deps.store.createTeamOrg(
        {
          id: uuidv7(now.getTime()),
          slug,
          name,
          kind: "team",
          createdAt: now,
        },
        session.user.id,
      );
      return c.json(presentOrg(org), 201);
    } catch (error) {
      if (error instanceof OrgSlugTakenError) {
        return errorJson(c, 409, "login_taken", "org slug is already taken", { field: "slug" });
      }
      throw error;
    }
  });

  app.get("/v1/orgs/:org/members", async (c) => {
    const session = await requireSession(c, deps);
    if (isResponse(session)) {
      return session;
    }
    const access = await requireOrgMember(c, deps, session.user, c.req.param("org"));
    if (access instanceof Response) {
      return access;
    }
    const members = await deps.store.listOrgMembers(access.org.id);
    const items = await Promise.all(
      members.map(async (member) =>
        presentOrgMember(member, await deps.store.findUserById(member.userId)),
      ),
    );
    return c.json({ items, next_cursor: null });
  });

  app.post("/v1/orgs/:org/invites", async (c) => {
    const session = await requireSession(c, deps);
    if (isResponse(session)) {
      return session;
    }
    const access = await requireOrgMember(c, deps, session.user, c.req.param("org"), "admin");
    if (access instanceof Response) {
      return access;
    }
    if (access.org.kind === "personal") {
      return errorJson(c, 403, "forbidden", "personal orgs cannot be invited to");
    }

    const body = await readObject(c);
    const target = parseInviteTarget(body);
    const roleRaw = typeof body?.["role"] === "string" ? body["role"] : "member";
    if (!target || !isOrgInviteRole(roleRaw)) {
      return errorJson(
        c,
        400,
        "unauthorized",
        "email or github_login and a valid role are required",
        {
          reason: "invalid_body",
        },
      );
    }
    const role: OrgInviteRole = roleRaw;
    const now = deps.clock.now();
    const invite = await deps.store.createOrgInvite({
      id: uuidv7(now.getTime()),
      orgId: access.org.id,
      email: target.email,
      githubLogin: target.githubLogin,
      role,
      expiresAt: new Date(now.getTime() + INVITE_TTL_MS),
      acceptedAt: null,
    });
    return c.json(presentOrgInvite(invite), 201);
  });

  app.post("/v1/org-invites/:id/accept", async (c) => {
    const session = await requireSession(c, deps);
    if (isResponse(session)) {
      return session;
    }
    const id = c.req.param("id");
    if (!isUuid(id)) {
      return errorJson(c, 404, "not_found", "invite not found");
    }
    const invite = await deps.store.findOrgInviteById(id);
    const now = deps.clock.now();
    if (!invite || invite.acceptedAt || invite.expiresAt.getTime() <= now.getTime()) {
      return errorJson(c, 404, "not_found", "invite not found");
    }
    const org = await deps.store.findOrgById(invite.orgId);
    if (!org || org.kind === "personal") {
      return errorJson(c, 404, "not_found", "invite not found");
    }
    if (!inviteMatchesUser(invite, session.user)) {
      return errorJson(c, 403, "forbidden", "invite does not match this account");
    }
    const accepted = await deps.store.acceptOrgInvite(id, session.user.id, now);
    if (!accepted) {
      return errorJson(c, 404, "not_found", "invite not found");
    }
    return c.json(presentOrgInvite(accepted));
  });

  app.get("/v1/orgs/:org/projects", async (c) => {
    const session = await requireSession(c, deps);
    if (isResponse(session)) {
      return session;
    }
    const access = await requireOrgMember(c, deps, session.user, c.req.param("org"));
    if (access instanceof Response) {
      return access;
    }
    const items = (await deps.store.listProjectsForOrg(access.org.id, session.user.id)).map(
      presentProject,
    );
    return c.json({ items, next_cursor: null });
  });

  app.post("/v1/orgs/:org/projects", async (c) => {
    const session = await requireSession(c, deps);
    if (isResponse(session)) {
      return session;
    }
    const access = await requireOrgMember(c, deps, session.user, c.req.param("org"));
    if (access instanceof Response) {
      return access;
    }

    const body = await readObject(c);
    const slug = parseSlug(body?.["slug"]);
    const name = parseOptionalString(body?.["name"], 120);
    const description =
      body?.["description"] === undefined ? "" : parseOptionalString(body["description"], 2000);
    if (!slug || !name || description === undefined) {
      return errorJson(c, 400, "unauthorized", "name and slug are required", {
        reason: "invalid_body",
      });
    }
    if (body?.["visibility"] !== undefined && body["visibility"] !== "private") {
      return errorJson(c, 400, "unauthorized", "visibility must be private", {
        reason: "invalid_visibility",
      });
    }

    const now = deps.clock.now();
    try {
      const project = await deps.store.createProject(
        {
          id: uuidv7(now.getTime()),
          orgId: access.org.id,
          slug,
          name,
          description,
          visibility: "private",
          defaultRepoId: null,
          settings: {},
          deletedAt: null,
          createdAt: now,
          updatedAt: now,
        },
        session.user.id,
      );
      return c.json(presentProject(project), 201);
    } catch (error) {
      if (error instanceof ProjectSlugTakenError) {
        return errorJson(c, 409, "login_taken", "project slug is already taken", { field: "slug" });
      }
      throw error;
    }
  });

  app.get("/v1/projects/:id", async (c) => {
    const access = await requireProjectActor(c, deps, c.req.param("id"), "project:read");
    if (access instanceof Response) {
      return access;
    }
    return c.json(presentProject(access.project));
  });

  app.patch("/v1/projects/:id", async (c) => {
    const session = await requireSession(c, deps);
    if (isResponse(session)) {
      return session;
    }
    const access = await requireProjectAccess(c, deps, session.user, c.req.param("id"), "admin");
    if (access instanceof Response) {
      return access;
    }

    const body = await readObject(c);
    if (!body) {
      return errorJson(c, 400, "unauthorized", "invalid body", { reason: "invalid_body" });
    }
    const patch: { name?: string; description?: string; slug?: string } = {};
    if (body["name"] !== undefined) {
      const name = parseOptionalString(body["name"], 120);
      if (!name) {
        return errorJson(c, 400, "unauthorized", "invalid name", { reason: "invalid_body" });
      }
      patch.name = name;
    }
    if (body["description"] !== undefined) {
      if (typeof body["description"] !== "string" || body["description"].length > 2000) {
        return errorJson(c, 400, "unauthorized", "invalid description", { reason: "invalid_body" });
      }
      patch.description = body["description"];
    }
    if (body["slug"] !== undefined) {
      const slug = parseSlug(body["slug"]);
      if (!slug) {
        return errorJson(c, 400, "unauthorized", "invalid slug", { reason: "invalid_body" });
      }
      patch.slug = slug;
    }
    if (body["visibility"] !== undefined && body["visibility"] !== "private") {
      return errorJson(c, 400, "unauthorized", "visibility must be private", {
        reason: "invalid_visibility",
      });
    }

    try {
      const updated = await deps.store.updateProject(access.project.id, patch, deps.clock.now());
      if (!updated) {
        return errorJson(c, 404, "not_found", "project not found");
      }
      return c.json(presentProject(updated));
    } catch (error) {
      if (error instanceof ProjectSlugTakenError) {
        return errorJson(c, 409, "login_taken", "project slug is already taken", { field: "slug" });
      }
      throw error;
    }
  });

  app.delete("/v1/projects/:id", async (c) => {
    const session = await requireSession(c, deps);
    if (isResponse(session)) {
      return session;
    }
    const access = await requireProjectAccess(c, deps, session.user, c.req.param("id"), "admin");
    if (access instanceof Response) {
      return access;
    }
    const deleted = await deps.store.softDeleteProject(access.project.id, deps.clock.now());
    if (!deleted) {
      return errorJson(c, 404, "not_found", "project not found");
    }
    return c.json(presentProject(deleted));
  });

  app.get("/v1/projects/:id/members", async (c) => {
    const session = await requireSession(c, deps);
    if (isResponse(session)) {
      return session;
    }
    const access = await requireProjectAccess(c, deps, session.user, c.req.param("id"), "read");
    if (access instanceof Response) {
      return access;
    }
    const members = await deps.store.listProjectMembers(access.project.id);
    const items = await Promise.all(
      members.map(async (member) =>
        presentProjectMember(member, await deps.store.findUserById(member.userId)),
      ),
    );
    return c.json({ items, next_cursor: null });
  });

  app.post("/v1/projects/:id/members", async (c) => {
    const session = await requireSession(c, deps);
    if (isResponse(session)) {
      return session;
    }
    const access = await requireProjectAccess(c, deps, session.user, c.req.param("id"), "admin");
    if (access instanceof Response) {
      return access;
    }

    const body = await readObject(c);
    const roleRaw = typeof body?.["role"] === "string" ? body["role"] : "read";
    if (!isProjectRole(roleRaw)) {
      return errorJson(c, 400, "unauthorized", "invalid role", { reason: "invalid_body" });
    }
    const role: ProjectRole = roleRaw;
    const now = deps.clock.now();

    const userId = typeof body?.["user_id"] === "string" ? body["user_id"] : undefined;
    if (userId) {
      if (!isUuid(userId)) {
        return errorJson(c, 400, "unauthorized", "invalid user_id", { reason: "invalid_body" });
      }
      const target = await deps.store.findUserById(userId);
      if (!target) {
        return errorJson(c, 404, "not_found", "user not found");
      }
      const member = await deps.store.upsertProjectMember({
        projectId: access.project.id,
        userId: target.id,
        role,
        createdAt: now,
      });
      return c.json(presentProjectMember(member, target), 201);
    }

    const target = parseInviteTarget(body);
    if (!target) {
      return errorJson(c, 400, "unauthorized", "user_id, email, or github_login is required", {
        reason: "invalid_body",
      });
    }
    const invite = await deps.store.createProjectInvite({
      id: uuidv7(now.getTime()),
      projectId: access.project.id,
      email: target.email,
      githubLogin: target.githubLogin,
      role,
      invitedBy: session.user.id,
      expiresAt: new Date(now.getTime() + INVITE_TTL_MS),
      acceptedAt: null,
    });
    return c.json(presentProjectInvite(invite), 201);
  });

  app.post("/v1/projects/:id/invites", async (c) => {
    const session = await requireSession(c, deps);
    if (isResponse(session)) {
      return session;
    }
    const access = await requireProjectAccess(c, deps, session.user, c.req.param("id"), "admin");
    if (access instanceof Response) {
      return access;
    }

    const body = await readObject(c);
    const target = parseInviteTarget(body);
    const roleRaw = typeof body?.["role"] === "string" ? body["role"] : "read";
    if (!target || !isProjectRole(roleRaw)) {
      return errorJson(
        c,
        400,
        "unauthorized",
        "email or github_login and a valid role are required",
        {
          reason: "invalid_body",
        },
      );
    }
    const now = deps.clock.now();
    const invite = await deps.store.createProjectInvite({
      id: uuidv7(now.getTime()),
      projectId: access.project.id,
      email: target.email,
      githubLogin: target.githubLogin,
      role: roleRaw,
      invitedBy: session.user.id,
      expiresAt: new Date(now.getTime() + INVITE_TTL_MS),
      acceptedAt: null,
    });
    return c.json(presentProjectInvite(invite), 201);
  });

  app.post("/v1/project-invites/:id/accept", async (c) => {
    const session = await requireSession(c, deps);
    if (isResponse(session)) {
      return session;
    }
    const id = c.req.param("id");
    if (!isUuid(id)) {
      return errorJson(c, 404, "not_found", "invite not found");
    }
    const invite = await deps.store.findProjectInviteById(id);
    const now = deps.clock.now();
    if (!invite || invite.acceptedAt || invite.expiresAt.getTime() <= now.getTime()) {
      return errorJson(c, 404, "not_found", "invite not found");
    }
    const project = await deps.store.findProjectById(invite.projectId);
    if (!project || project.deletedAt) {
      return errorJson(c, 404, "not_found", "invite not found");
    }
    if (!inviteMatchesUser(invite, session.user)) {
      return errorJson(c, 403, "forbidden", "invite does not match this account");
    }
    const accepted = await deps.store.acceptProjectInvite(id, session.user.id, now);
    if (!accepted) {
      return errorJson(c, 404, "not_found", "invite not found");
    }
    return c.json(presentProjectInvite(accepted));
  });
}
