'use strict';

const { execFileSync } = require('child_process');
const crypto = require('crypto');
const fs = require('fs');
const path = require('path');

const REPO_ROOT = path.resolve(__dirname, '..', '..');

// Shared test password for all seeded users. The hash below is a BCrypt.Net-Next 4.0.3
// hash (same package the app uses) of PASSWORD, cost 11.
const PASSWORD = 'P15-e2e-pass-2026';
const PASSWORD_HASH = '$2a$11$IQlvMRekajliwnFi3n0q9uiM1VtwuEhUDiBD4C50zU09IQkhc9Rhq';

function readAdminCredentials() {
  const raw = fs.readFileSync(path.join(__dirname, '..', '.dev-admin.json'), 'utf8');
  const parsed = JSON.parse(raw);
  if (!parsed.login || !parsed.password) {
    throw new Error('tests/playwright/.dev-admin.json must contain login and password');
  }
  return parsed;
}

function psql(sql) {
  execFileSync(
    'docker',
    ['exec', '-i', 'beacon-postgres', 'psql', '-U', 'beacon', '-d', 'beacon',
      '-v', 'ON_ERROR_STOP=1', '--single-transaction', '-q'],
    { input: sql, encoding: 'utf8', maxBuffer: 16 * 1024 * 1024 },
  );
}

/**
 * Seeds one isolated test case: a single org, two projects (A older than B), the given
 * users, their memberships, and one marker task per project. All rows are prefixed
 * p15-/P15- so cleanup is exact and re-runnable.
 *
 * users: [{ key, projects: ['A'] | ['B'] | ['A','B'] }]
 *
 * Determinism notes:
 *  - Project A has an older CreatedAt than B.
 *  - Membership A has an older JoinedAt than B, so "oldest remaining membership"
 *    (the rebind target and the login-time current project) is unambiguous.
 *  - Roles are Member (2); the global system admin performs the revocations.
 */
function seedCase(users) {
  const runId = `p15-${Date.now()}-${crypto.randomBytes(3).toString('hex')}`;
  const state = {
    runId,
    orgId: crypto.randomUUID(),
    projectA: crypto.randomUUID(),
    projectB: crypto.randomUUID(),
    taskA: crypto.randomUUID(),
    taskB: crypto.randomUUID(),
    markerA: `P15-A-${runId}`,
    markerB: `P15-B-${runId}`,
    password: PASSWORD,
    users: {},
  };

  const statements = [
    `INSERT INTO "Orgs" ("Id","Name","CreatedAt","UpdatedAt")
     VALUES ('${state.orgId}', 'P15-ORG-${runId}', now(), now());`,
    `INSERT INTO "Projects" ("Id","Name","OrgId","CreatedAt","UpdatedAt")
     VALUES ('${state.projectA}', 'P15-A-${runId}', '${state.orgId}', now() - interval '3 days', now());`,
    `INSERT INTO "Projects" ("Id","Name","OrgId","CreatedAt","UpdatedAt")
     VALUES ('${state.projectB}', 'P15-B-${runId}', '${state.orgId}', now() - interval '1 day', now());`,
  ];

  for (const u of users) {
    state.users[u.key] = { login: `${runId}-${u.key}`, id: crypto.randomUUID() };
    statements.push(
      `INSERT INTO "Users" ("Id","Login","Email","PasswordHash","IsAdmin","FailedLoginAttempts")
       VALUES ('${state.users[u.key].id}', '${state.users[u.key].login}', '${state.users[u.key].login}@p15-e2e.local', '${PASSWORD_HASH}', false, 0);`,
    );
  }

  for (const u of users) {
    for (const p of u.projects) {
      const projectId = p === 'A' ? state.projectA : state.projectB;
      const joined = p === 'A' ? "now() - interval '3 days'" : "now() - interval '1 day'";
      statements.push(
        `INSERT INTO "ProjectMembers" ("Id","ProjectId","UserId","Role","JoinedAt")
         VALUES ('${crypto.randomUUID()}', '${projectId}', '${state.users[u.key].id}', 2, ${joined});`,
      );
    }
  }

  statements.push(
    `INSERT INTO "Tasks" ("Id","Title","Description","Status","Priority","Type","SubStage","ProjectId","CreatedAt")
     VALUES ('${state.taskA}', '${state.markerA}', 'P15 E2E marker A', 'Todo', 'Medium', 'Task', 'RequirementGathering', '${state.projectA}', now());`,
    `INSERT INTO "Tasks" ("Id","Title","Description","Status","Priority","Type","SubStage","ProjectId","CreatedAt")
     VALUES ('${state.taskB}', '${state.markerB}', 'P15 E2E marker B', 'Todo', 'Medium', 'Task', 'RequirementGathering', '${state.projectB}', now());`,
  );

  psql(statements.join('\n'));
  return state;
}

function cleanupSql() {
  return [
    `DELETE FROM "Tasks" WHERE "Title" LIKE 'P15-%';`,
    `DELETE FROM "Projects" WHERE "Name" LIKE 'P15-%';`,
    `DELETE FROM "Users" WHERE "Login" LIKE 'p15-%';`,
    `DELETE FROM "Orgs" WHERE "Name" LIKE 'P15-ORG-%';`,
  ].join('\n');
}

function cleanupCase() {
  psql(cleanupSql());
}

function cleanupAll() {
  psql(cleanupSql());
}

module.exports = { PASSWORD, seedCase, cleanupCase, cleanupAll, readAdminCredentials };
