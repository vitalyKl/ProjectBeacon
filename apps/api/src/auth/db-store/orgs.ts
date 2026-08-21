import { orgInvites, orgMembers, orgs, projectInvites, projectMembers, projects } from "@beacon/db";
import { and, asc, eq, isNull, isNotNull } from "drizzle-orm";
import { OrgSlugTakenError, ProjectSlugTakenError, higherOrgRole, higherProjectRole } from "../../orgs/types.js";
import { slugCandidate, slugFromLogin } from "../../slug.js";
import type { Db } from "@beacon/db";
import type { OrgRole, OrgInviteRole, ProjectRole, OrgRecord, OrgMemberRecord, OrgInviteRecord, ProjectRecord, ProjectMemberRecord, ProjectInviteRecord } from "../../orgs/types.js";
import type { UserRecord } from "../identity.js";
import { uniqueConstraint, toOrg, toOrgMember, toOrgInvite, toProject, toProjectMember, toProjectInvite, seedDefaultSecurityConstraints, seedDefaultProjectLabels } from "./mappers.js";
import type { Ctor } from "./ctor.js";
import { DbStoreCore } from "./core.js";
import type { OrgStore } from "../../orgs/store.js";

export function withDbOrgs<TBase extends Ctor<DbStoreCore>>(
  Base: TBase,
): TBase & Ctor<OrgStore> {
  return class DbOrgs extends Base {
  async findOwnedPersonalOrg(
    db: Pick<Db, "select">,
    userId: string,
  ): Promise<OrgRecord | undefined> {
    const [row] = await db
      .select()
      .from(orgs)
      .where(and(eq(orgs.id, userId), eq(orgs.kind, "personal")))
      .limit(1);
    return row ? toOrg(row) : undefined;
  }

  async ensurePersonalOrg(user: UserRecord, now: Date): Promise<OrgRecord> {
    const existing = await this.findOwnedPersonalOrg(this.db, user.id);
    if (existing) {
      await this.upsertOrgMember({ orgId: existing.id, userId: user.id, role: "owner" });
      return existing;
    }

    const base = slugFromLogin(user.login);
    for (let attempt = 1; attempt < 1000; attempt += 1) {
      const slug = slugCandidate(base, attempt);
      try {
        return await this.db.transaction(async (tx) => {
          const raced = await this.findOwnedPersonalOrg(tx, user.id);
          if (raced) {
            await tx
              .insert(orgMembers)
              .values({ orgId: raced.id, userId: user.id, role: "owner" })
              .onConflictDoUpdate({
                target: [orgMembers.orgId, orgMembers.userId],
                set: { role: "owner" },
              });
            return raced;
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
        if (!uniqueConstraint(error)) {
          throw error;
        }
        const recovered = await this.findOwnedPersonalOrg(this.db, user.id);
        if (recovered) {
          await this.upsertOrgMember({ orgId: recovered.id, userId: user.id, role: "owner" });
          return recovered;
        }
        if (uniqueConstraint(error) === "org_slug") {
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
      const [existing] = await tx
        .select()
        .from(orgMembers)
        .where(and(eq(orgMembers.orgId, invite.orgId), eq(orgMembers.userId, userId)))
        .limit(1);
      const role = existing
        ? higherOrgRole(existing.role as OrgRole, invite.role as OrgInviteRole)
        : invite.role;
      await tx
        .insert(orgMembers)
        .values({
          orgId: invite.orgId,
          userId,
          role,
        })
        .onConflictDoUpdate({
          target: [orgMembers.orgId, orgMembers.userId],
          set: { role },
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
        await seedDefaultSecurityConstraints(tx, created.id, project.createdAt);
        await seedDefaultProjectLabels(tx, created.id, project.createdAt);
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
    patch: {
      name?: string;
      description?: string;
      slug?: string;
      settings?: Record<string, unknown>;
    },
    updatedAt: Date,
  ): Promise<ProjectRecord | undefined> {
    try {
      const [row] = await this.db
        .update(projects)
        .set({
          ...(patch.name !== undefined ? { name: patch.name } : {}),
          ...(patch.description !== undefined ? { description: patch.description } : {}),
          ...(patch.slug !== undefined ? { slug: patch.slug } : {}),
          ...(patch.settings !== undefined ? { settings: patch.settings } : {}),
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
      const [existing] = await tx
        .select()
        .from(projectMembers)
        .where(
          and(eq(projectMembers.projectId, invite.projectId), eq(projectMembers.userId, userId)),
        )
        .limit(1);
      const role = existing
        ? higherProjectRole(existing.role as ProjectRole, invite.role as ProjectRole)
        : invite.role;
      await tx
        .insert(projectMembers)
        .values({
          projectId: invite.projectId,
          userId,
          role,
          createdAt: existing?.createdAt ?? acceptedAt,
        })
        .onConflictDoUpdate({
          target: [projectMembers.projectId, projectMembers.userId],
          set: { role },
        });
      return toProjectInvite(updated);
    });
  }

  async listDeletedProjects(): Promise<ProjectRecord[]> {
    const rows = await this.db
      .select()
      .from(projects)
      .where(isNotNull(projects.deletedAt))
      .orderBy(asc(projects.id));
    return rows.map(toProject);
  }

  async setDefaultRepoIfEmpty(
    projectId: string,
    repoId: string,
    updatedAt: Date,
  ): Promise<ProjectRecord> {
    const [updated] = await this.db
      .update(projects)
      .set({ defaultRepoId: repoId, updatedAt })
      .where(and(eq(projects.id, projectId), isNull(projects.defaultRepoId)))
      .returning();
    if (updated) {
      return toProject(updated);
    }
    const existing = await this.findProjectById(projectId);
    if (!existing) {
      throw new Error("project not found");
    }
    return existing;
  }
  } as TBase & Ctor<OrgStore>;
}
