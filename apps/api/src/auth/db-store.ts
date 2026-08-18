import {
  orgInvites,
  orgMembers,
  orgs,
  projectInvites,
  projectMembers,
  projects,
  userSessions,
  users,
  type Db,
} from "@beacon/db";
import { and, asc, eq, isNull, sql } from "drizzle-orm";

import { slugCandidate, slugFromLogin } from "../slug.js";
import type {
  OrgInviteRecord,
  OrgInviteRole,
  OrgKind,
  OrgMemberRecord,
  OrgRecord,
  OrgRole,
  ProjectInviteRecord,
  ProjectMemberRecord,
  ProjectRecord,
  ProjectRole,
} from "../orgs/types.js";
import {
  BootstrapConsumedError,
  GithubIdTakenError,
  LoginTakenError,
  OrgSlugTakenError,
  ProjectSlugTakenError,
  type AuthStore,
  type SessionRecord,
  type UserRecord,
} from "./store.js";

const BOOTSTRAP_LOCK_KEY = 8_811_201;

type UniqueConstraint = "login" | "github_id" | "org_slug" | "project_slug" | "unknown";

function uniqueConstraint(error: unknown): UniqueConstraint | undefined {
  let current: unknown = error;
  for (let i = 0; i < 4 && current; i += 1) {
    if (typeof current === "object" && current !== null && "code" in current) {
      const code = (current as { code: unknown }).code;
      if (code === "23505") {
        const constraint =
          "constraint_name" in current && typeof current.constraint_name === "string"
            ? current.constraint_name
            : "constraint" in current && typeof current.constraint === "string"
              ? current.constraint
              : "";
        if (constraint.includes("login")) {
          return "login";
        }
        if (constraint.includes("github_id")) {
          return "github_id";
        }
        if (constraint.includes("orgs_slug")) {
          return "org_slug";
        }
        if (constraint.includes("projects_org_id_slug")) {
          return "project_slug";
        }
        return "unknown";
      }
    }
    current =
      typeof current === "object" && current !== null && "cause" in current
        ? (current as { cause: unknown }).cause
        : undefined;
  }
  return undefined;
}

function mapUserInsertError(error: unknown): never {
  const constraint = uniqueConstraint(error);
  if (constraint === "login") {
    throw new LoginTakenError();
  }
  if (constraint === "github_id") {
    throw new GithubIdTakenError();
  }
  throw error;
}

function asBuffer(value: Buffer | Uint8Array): Buffer {
  return Buffer.isBuffer(value) ? value : Buffer.from(value);
}

function toUser(row: typeof users.$inferSelect): UserRecord {
  return {
    id: row.id,
    githubId: row.githubId,
    login: row.login,
    email: row.email,
    name: row.name,
    avatarUrl: row.avatarUrl,
    passwordHash: row.passwordHash,
    createdAt: row.createdAt,
    updatedAt: row.updatedAt,
  };
}

function toOrg(row: typeof orgs.$inferSelect): OrgRecord {
  return {
    id: row.id,
    slug: row.slug,
    name: row.name,
    kind: row.kind as OrgKind,
    createdAt: row.createdAt,
  };
}

function toOrgMember(row: typeof orgMembers.$inferSelect): OrgMemberRecord {
  return {
    orgId: row.orgId,
    userId: row.userId,
    role: row.role as OrgRole,
  };
}

function toOrgInvite(row: typeof orgInvites.$inferSelect): OrgInviteRecord {
  return {
    id: row.id,
    orgId: row.orgId,
    email: row.email,
    githubLogin: row.githubLogin,
    role: row.role as OrgInviteRole,
    expiresAt: row.expiresAt,
    acceptedAt: row.acceptedAt,
  };
}

function asSettings(value: unknown): Record<string, unknown> {
  if (value !== null && typeof value === "object" && !Array.isArray(value)) {
    return value as Record<string, unknown>;
  }
  return {};
}

function toProject(row: typeof projects.$inferSelect): ProjectRecord {
  return {
    id: row.id,
    orgId: row.orgId,
    slug: row.slug,
    name: row.name,
    description: row.description,
    visibility: "private",
    settings: asSettings(row.settings),
    deletedAt: row.deletedAt,
    createdAt: row.createdAt,
    updatedAt: row.updatedAt,
  };
}

function toProjectMember(row: typeof projectMembers.$inferSelect): ProjectMemberRecord {
  return {
    projectId: row.projectId,
    userId: row.userId,
    role: row.role as ProjectRole,
    createdAt: row.createdAt,
  };
}

function toProjectInvite(row: typeof projectInvites.$inferSelect): ProjectInviteRecord {
  return {
    id: row.id,
    projectId: row.projectId,
    email: row.email,
    githubLogin: row.githubLogin,
    role: row.role as ProjectRole,
    invitedBy: row.invitedBy,
    expiresAt: row.expiresAt,
    acceptedAt: row.acceptedAt,
  };
}

function toSession(row: typeof userSessions.$inferSelect): SessionRecord {
  return {
    id: row.id,
    userId: row.userId,
    tokenHash: asBuffer(row.tokenHash),
    createdAt: row.createdAt,
    lastSeenAt: row.lastSeenAt,
    expiresAt: row.expiresAt,
    revokedAt: row.revokedAt,
    userAgent: row.userAgent,
    ip: row.ip,
  };
}

export class DbAuthStore implements AuthStore {
  constructor(private readonly db: Db) {}

  async hasAnyUser(): Promise<boolean> {
    const row = await this.db.select({ id: users.id }).from(users).limit(1);
    return row.length > 0;
  }

  async findUserById(id: string): Promise<UserRecord | undefined> {
    const [row] = await this.db.select().from(users).where(eq(users.id, id)).limit(1);
    return row ? toUser(row) : undefined;
  }

  async findUserByLogin(login: string): Promise<UserRecord | undefined> {
    const [row] = await this.db.select().from(users).where(eq(users.login, login)).limit(1);
    return row ? toUser(row) : undefined;
  }

  async findUserByEmail(email: string): Promise<UserRecord | undefined> {
    const [row] = await this.db
      .select()
      .from(users)
      .where(sql`lower(${users.email}) = lower(${email})`)
      .limit(1);
    return row ? toUser(row) : undefined;
  }

  async findUserByGithubId(githubId: bigint): Promise<UserRecord | undefined> {
    const [row] = await this.db.select().from(users).where(eq(users.githubId, githubId)).limit(1);
    return row ? toUser(row) : undefined;
  }

  async createUser(user: UserRecord): Promise<UserRecord> {
    try {
      return toUser(await this.insertUser(this.db, user));
    } catch (error) {
      mapUserInsertError(error);
    }
  }

  async createFirstUser(user: UserRecord): Promise<UserRecord> {
    try {
      return await this.db.transaction(async (tx) => {
        await tx.execute(sql`select pg_advisory_xact_lock(${BOOTSTRAP_LOCK_KEY})`);
        const existing = await tx.select({ id: users.id }).from(users).limit(1);
        if (existing.length > 0) {
          throw new BootstrapConsumedError();
        }
        return toUser(await this.insertUser(tx, user));
      });
    } catch (error) {
      if (error instanceof BootstrapConsumedError) {
        throw error;
      }
      if (uniqueConstraint(error)) {
        throw new BootstrapConsumedError();
      }
      throw error;
    }
  }

  private async insertUser(
    db: Pick<Db, "insert">,
    user: UserRecord,
  ): Promise<typeof users.$inferSelect> {
    const [row] = await db
      .insert(users)
      .values({
        id: user.id,
        githubId: user.githubId,
        login: user.login,
        email: user.email,
        name: user.name,
        avatarUrl: user.avatarUrl,
        passwordHash: user.passwordHash,
        createdAt: user.createdAt,
        updatedAt: user.updatedAt,
      })
      .returning();
    if (!row) {
      throw new Error("insert user returned no row");
    }
    return row;
  }

  async createSession(session: SessionRecord): Promise<SessionRecord> {
    const [row] = await this.db
      .insert(userSessions)
      .values({
        id: session.id,
        userId: session.userId,
        tokenHash: session.tokenHash,
        createdAt: session.createdAt,
        lastSeenAt: session.lastSeenAt,
        expiresAt: session.expiresAt,
        revokedAt: session.revokedAt,
        userAgent: session.userAgent,
        ip: session.ip,
      })
      .returning();
    if (!row) {
      throw new Error("insert session returned no row");
    }
    return toSession(row);
  }

  async findSessionByTokenHash(tokenHash: Buffer): Promise<SessionRecord | undefined> {
    const [row] = await this.db
      .select()
      .from(userSessions)
      .where(eq(userSessions.tokenHash, tokenHash))
      .limit(1);
    return row ? toSession(row) : undefined;
  }

  async updateSessionRolling(id: string, lastSeenAt: Date, expiresAt: Date): Promise<void> {
    await this.db
      .update(userSessions)
      .set({ lastSeenAt, expiresAt })
      .where(eq(userSessions.id, id));
  }

  async revokeSession(id: string, revokedAt: Date): Promise<void> {
    await this.db
      .update(userSessions)
      .set({ revokedAt })
      .where(and(eq(userSessions.id, id), isNull(userSessions.revokedAt)));
  }

  async revokeUserSessions(userId: string, revokedAt: Date): Promise<void> {
    await this.db
      .update(userSessions)
      .set({ revokedAt })
      .where(and(eq(userSessions.userId, userId), isNull(userSessions.revokedAt)));
  }

  async ensurePersonalOrg(user: UserRecord, now: Date): Promise<OrgRecord> {
    const existing = await this.db
      .select({ org: orgs })
      .from(orgMembers)
      .innerJoin(orgs, eq(orgs.id, orgMembers.orgId))
      .where(and(eq(orgMembers.userId, user.id), eq(orgs.kind, "personal")))
      .limit(1);
    if (existing[0]) {
      return toOrg(existing[0].org);
    }

    const base = slugFromLogin(user.login);
    for (let attempt = 1; attempt < 1000; attempt += 1) {
      const slug = slugCandidate(base, attempt);
      try {
        return await this.db.transaction(async (tx) => {
          const raced = await tx
            .select({ org: orgs })
            .from(orgMembers)
            .innerJoin(orgs, eq(orgs.id, orgMembers.orgId))
            .where(and(eq(orgMembers.userId, user.id), eq(orgs.kind, "personal")))
            .limit(1);
          if (raced[0]) {
            return toOrg(raced[0].org);
          }
          const [org] = await tx
            .insert(orgs)
            .values({
              id: user.id,
              slug,
              name: user.name ?? user.login,
              kind: "personal",
              createdAt: now,
            })
            .returning();
          if (!org) {
            throw new Error("insert personal org returned no row");
          }
          await tx.insert(orgMembers).values({
            orgId: org.id,
            userId: user.id,
            role: "owner",
          });
          return toOrg(org);
        });
      } catch (error) {
        const constraint = uniqueConstraint(error);
        if (constraint === "org_slug") {
          continue;
        }
        throw error;
      }
    }
    throw new OrgSlugTakenError();
  }

  async listOrgsForUser(userId: string): Promise<OrgRecord[]> {
    const rows = await this.db
      .select({ org: orgs })
      .from(orgMembers)
      .innerJoin(orgs, eq(orgs.id, orgMembers.orgId))
      .where(eq(orgMembers.userId, userId))
      .orderBy(asc(orgs.createdAt), asc(orgs.id));
    return rows.map((row) => toOrg(row.org));
  }

  async findOrgById(id: string): Promise<OrgRecord | undefined> {
    const [row] = await this.db.select().from(orgs).where(eq(orgs.id, id)).limit(1);
    return row ? toOrg(row) : undefined;
  }

  async findOrgBySlug(slug: string): Promise<OrgRecord | undefined> {
    const [row] = await this.db.select().from(orgs).where(eq(orgs.slug, slug)).limit(1);
    return row ? toOrg(row) : undefined;
  }

  async createTeamOrg(org: OrgRecord, ownerUserId: string): Promise<OrgRecord> {
    try {
      return await this.db.transaction(async (tx) => {
        const [created] = await tx
          .insert(orgs)
          .values({
            id: org.id,
            slug: org.slug,
            name: org.name,
            kind: "team",
            createdAt: org.createdAt,
          })
          .returning();
        if (!created) {
          throw new Error("insert org returned no row");
        }
        await tx.insert(orgMembers).values({
          orgId: created.id,
          userId: ownerUserId,
          role: "owner",
        });
        return toOrg(created);
      });
    } catch (error) {
      if (uniqueConstraint(error) === "org_slug") {
        throw new OrgSlugTakenError();
      }
      throw error;
    }
  }

  async listOrgMembers(orgId: string): Promise<OrgMemberRecord[]> {
    const rows = await this.db
      .select()
      .from(orgMembers)
      .where(eq(orgMembers.orgId, orgId))
      .orderBy(asc(orgMembers.userId));
    return rows.map(toOrgMember);
  }

  async findOrgMember(orgId: string, userId: string): Promise<OrgMemberRecord | undefined> {
    const [row] = await this.db
      .select()
      .from(orgMembers)
      .where(and(eq(orgMembers.orgId, orgId), eq(orgMembers.userId, userId)))
      .limit(1);
    return row ? toOrgMember(row) : undefined;
  }

  async upsertOrgMember(member: OrgMemberRecord): Promise<OrgMemberRecord> {
    const [row] = await this.db
      .insert(orgMembers)
      .values({
        orgId: member.orgId,
        userId: member.userId,
        role: member.role,
      })
      .onConflictDoUpdate({
        target: [orgMembers.orgId, orgMembers.userId],
        set: { role: member.role },
      })
      .returning();
    if (!row) {
      throw new Error("upsert org member returned no row");
    }
    return toOrgMember(row);
  }

  async createOrgInvite(invite: OrgInviteRecord): Promise<OrgInviteRecord> {
    const [row] = await this.db
      .insert(orgInvites)
      .values({
        id: invite.id,
        orgId: invite.orgId,
        email: invite.email,
        githubLogin: invite.githubLogin,
        role: invite.role,
        expiresAt: invite.expiresAt,
        acceptedAt: invite.acceptedAt,
      })
      .returning();
    if (!row) {
      throw new Error("insert org invite returned no row");
    }
    return toOrgInvite(row);
  }

  async findOrgInviteById(id: string): Promise<OrgInviteRecord | undefined> {
    const [row] = await this.db.select().from(orgInvites).where(eq(orgInvites.id, id)).limit(1);
    return row ? toOrgInvite(row) : undefined;
  }

  async acceptOrgInvite(
    id: string,
    userId: string,
    acceptedAt: Date,
  ): Promise<OrgInviteRecord | undefined> {
    return this.db.transaction(async (tx) => {
      const [invite] = await tx.select().from(orgInvites).where(eq(orgInvites.id, id)).limit(1);
      if (!invite || invite.acceptedAt) {
        return undefined;
      }
      const [updated] = await tx
        .update(orgInvites)
        .set({ acceptedAt })
        .where(and(eq(orgInvites.id, id), isNull(orgInvites.acceptedAt)))
        .returning();
      if (!updated) {
        return undefined;
      }
      await tx
        .insert(orgMembers)
        .values({
          orgId: invite.orgId,
          userId,
          role: invite.role,
        })
        .onConflictDoUpdate({
          target: [orgMembers.orgId, orgMembers.userId],
          set: { role: invite.role },
        });
      return toOrgInvite(updated);
    });
  }

  async createProject(project: ProjectRecord, creatorUserId: string): Promise<ProjectRecord> {
    try {
      return await this.db.transaction(async (tx) => {
        const [created] = await tx
          .insert(projects)
          .values({
            id: project.id,
            orgId: project.orgId,
            slug: project.slug,
            name: project.name,
            description: project.description,
            visibility: "private",
            settings: project.settings,
            deletedAt: null,
            createdAt: project.createdAt,
            updatedAt: project.updatedAt,
          })
          .returning();
        if (!created) {
          throw new Error("insert project returned no row");
        }
        await tx.insert(projectMembers).values({
          projectId: created.id,
          userId: creatorUserId,
          role: "admin",
          createdAt: project.createdAt,
        });
        return toProject(created);
      });
    } catch (error) {
      if (uniqueConstraint(error) === "project_slug") {
        throw new ProjectSlugTakenError();
      }
      throw error;
    }
  }

  async listProjectsForOrg(orgId: string, userId: string): Promise<ProjectRecord[]> {
    const rows = await this.db
      .select({ project: projects })
      .from(projects)
      .innerJoin(
        projectMembers,
        and(eq(projectMembers.projectId, projects.id), eq(projectMembers.userId, userId)),
      )
      .where(and(eq(projects.orgId, orgId), isNull(projects.deletedAt)))
      .orderBy(asc(projects.createdAt), asc(projects.id));
    return rows.map((row) => toProject(row.project));
  }

  async findProjectById(id: string): Promise<ProjectRecord | undefined> {
    const [row] = await this.db.select().from(projects).where(eq(projects.id, id)).limit(1);
    return row ? toProject(row) : undefined;
  }

  async updateProject(
    id: string,
    patch: { name?: string; description?: string; slug?: string },
    updatedAt: Date,
  ): Promise<ProjectRecord | undefined> {
    try {
      const [row] = await this.db
        .update(projects)
        .set({
          ...(patch.name !== undefined ? { name: patch.name } : {}),
          ...(patch.description !== undefined ? { description: patch.description } : {}),
          ...(patch.slug !== undefined ? { slug: patch.slug } : {}),
          updatedAt,
        })
        .where(and(eq(projects.id, id), isNull(projects.deletedAt)))
        .returning();
      return row ? toProject(row) : undefined;
    } catch (error) {
      if (uniqueConstraint(error) === "project_slug") {
        throw new ProjectSlugTakenError();
      }
      throw error;
    }
  }

  async softDeleteProject(id: string, deletedAt: Date): Promise<ProjectRecord | undefined> {
    const [row] = await this.db
      .update(projects)
      .set({ deletedAt, updatedAt: deletedAt })
      .where(and(eq(projects.id, id), isNull(projects.deletedAt)))
      .returning();
    return row ? toProject(row) : undefined;
  }

  async listProjectMembers(projectId: string): Promise<ProjectMemberRecord[]> {
    const rows = await this.db
      .select()
      .from(projectMembers)
      .where(eq(projectMembers.projectId, projectId))
      .orderBy(asc(projectMembers.userId));
    return rows.map(toProjectMember);
  }

  async findProjectMember(
    projectId: string,
    userId: string,
  ): Promise<ProjectMemberRecord | undefined> {
    const [row] = await this.db
      .select()
      .from(projectMembers)
      .where(and(eq(projectMembers.projectId, projectId), eq(projectMembers.userId, userId)))
      .limit(1);
    return row ? toProjectMember(row) : undefined;
  }

  async upsertProjectMember(member: ProjectMemberRecord): Promise<ProjectMemberRecord> {
    const [row] = await this.db
      .insert(projectMembers)
      .values({
        projectId: member.projectId,
        userId: member.userId,
        role: member.role,
        createdAt: member.createdAt,
      })
      .onConflictDoUpdate({
        target: [projectMembers.projectId, projectMembers.userId],
        set: { role: member.role },
      })
      .returning();
    if (!row) {
      throw new Error("upsert project member returned no row");
    }
    return toProjectMember(row);
  }

  async createProjectInvite(invite: ProjectInviteRecord): Promise<ProjectInviteRecord> {
    const [row] = await this.db
      .insert(projectInvites)
      .values({
        id: invite.id,
        projectId: invite.projectId,
        email: invite.email,
        githubLogin: invite.githubLogin,
        role: invite.role,
        invitedBy: invite.invitedBy,
        expiresAt: invite.expiresAt,
        acceptedAt: invite.acceptedAt,
      })
      .returning();
    if (!row) {
      throw new Error("insert project invite returned no row");
    }
    return toProjectInvite(row);
  }

  async findProjectInviteById(id: string): Promise<ProjectInviteRecord | undefined> {
    const [row] = await this.db
      .select()
      .from(projectInvites)
      .where(eq(projectInvites.id, id))
      .limit(1);
    return row ? toProjectInvite(row) : undefined;
  }

  async acceptProjectInvite(
    id: string,
    userId: string,
    acceptedAt: Date,
  ): Promise<ProjectInviteRecord | undefined> {
    return this.db.transaction(async (tx) => {
      const [invite] = await tx
        .select()
        .from(projectInvites)
        .where(eq(projectInvites.id, id))
        .limit(1);
      if (!invite || invite.acceptedAt) {
        return undefined;
      }
      const [updated] = await tx
        .update(projectInvites)
        .set({ acceptedAt })
        .where(and(eq(projectInvites.id, id), isNull(projectInvites.acceptedAt)))
        .returning();
      if (!updated) {
        return undefined;
      }
      await tx
        .insert(projectMembers)
        .values({
          projectId: invite.projectId,
          userId,
          role: invite.role,
          createdAt: acceptedAt,
        })
        .onConflictDoUpdate({
          target: [projectMembers.projectId, projectMembers.userId],
          set: { role: invite.role },
        });
      return toProjectInvite(updated);
    });
  }
}
