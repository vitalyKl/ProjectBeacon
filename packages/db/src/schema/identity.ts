import { sql } from "drizzle-orm";
import {
  bigint,
  check,
  index,
  inet,
  pgTable,
  primaryKey,
  text,
  uuid,
} from "drizzle-orm/pg-core";
import { bytea, timestamptz } from "./common.js";

export const users = pgTable("users", {
  id: uuid("id").primaryKey(),
  githubId: bigint("github_id", { mode: "bigint" }).unique(),
  login: text("login").notNull().unique(),
  email: text("email"),
  name: text("name"),
  avatarUrl: text("avatar_url"),
  passwordHash: text("password_hash"),
  createdAt: timestamptz("created_at").notNull().defaultNow(),
  updatedAt: timestamptz("updated_at").notNull().defaultNow(),
});

export const orgs = pgTable(
  "orgs",
  {
    id: uuid("id").primaryKey(),
    slug: text("slug").notNull().unique(),
    name: text("name").notNull(),
    kind: text("kind").notNull().default("team"),
    createdAt: timestamptz("created_at").notNull().defaultNow(),
  },
  (t) => [check("orgs_kind_check", sql`${t.kind} IN ('personal','team')`)],
);

export const orgMembers = pgTable(
  "org_members",
  {
    orgId: uuid("org_id")
      .notNull()
      .references(() => orgs.id),
    userId: uuid("user_id")
      .notNull()
      .references(() => users.id),
    role: text("role").notNull(),
  },
  (t) => [
    primaryKey({ columns: [t.orgId, t.userId] }),
    check("org_members_role_check", sql`${t.role} IN ('owner','admin','member')`),
  ],
);

export const orgInvites = pgTable(
  "org_invites",
  {
    id: uuid("id").primaryKey(),
    orgId: uuid("org_id")
      .notNull()
      .references(() => orgs.id),
    email: text("email"),
    githubLogin: text("github_login"),
    role: text("role").notNull(),
    expiresAt: timestamptz("expires_at").notNull(),
    acceptedAt: timestamptz("accepted_at"),
  },
  (t) => [check("org_invites_role_check", sql`${t.role} IN ('admin','member')`)],
);

export const userSessions = pgTable(
  "user_sessions",
  {
    id: uuid("id").primaryKey(),
    userId: uuid("user_id")
      .notNull()
      .references(() => users.id),
    tokenHash: bytea("token_hash").notNull().unique(),
    createdAt: timestamptz("created_at").notNull().defaultNow(),
    lastSeenAt: timestamptz("last_seen_at").notNull().defaultNow(),
    expiresAt: timestamptz("expires_at").notNull(),
    revokedAt: timestamptz("revoked_at"),
    userAgent: text("user_agent"),
    ip: inet("ip"),
  },
  (t) => [
    index("user_sessions_user")
      .on(t.userId)
      .where(sql`${t.revokedAt} IS NULL`),
  ],
);
