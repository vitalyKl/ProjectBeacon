'use strict';

const { execFileSync } = require('child_process');
const crypto = require('crypto');

// Same shared password/hash as the p15 suite: a BCrypt.Net-Next 4.0.3 hash
// (the app's hashing package) of PASSWORD, cost 11.
const PASSWORD = 'P15-e2e-pass-2026';
const PASSWORD_HASH = '$2a$11$IQlvMRekajliwnFi3n0q9uiM1VtwuEhUDiBD4C50zU09IQkhc9Rhq';

function psql(sql) {
  execFileSync(
    'docker',
    ['exec', '-i', 'beacon-postgres', 'psql', '-U', 'beacon', '-d', 'beacon',
      '-v', 'ON_ERROR_STOP=1', '--single-transaction', '-q'],
    { input: sql, encoding: 'utf8', maxBuffer: 16 * 1024 * 1024 },
  );
}

/**
 * Seeds an isolated chat case: org, project, member user (a single membership,
 * so the login-time current project is unambiguous), an offline device, one
 * chat session on it, and an initial assistant part. All rows carry the run id
 * so cleanup is exact and re-runnable. The device stays offline on purpose:
 * this suite verifies the Web-side streaming loop, not a live workstation.
 */
function seedChat() {
  const runId = `e2e-chat-${Date.now()}-${crypto.randomBytes(3).toString('hex')}`;
  const fx = {
    runId,
    orgId: crypto.randomUUID(),
    projectId: crypto.randomUUID(),
    userId: crypto.randomUUID(),
    deviceId: crypto.randomUUID(),
    sessionId: crypto.randomUUID(),
    partOneId: crypto.randomUUID(),
    login: `${runId}-user`,
    password: PASSWORD,
    sessionTitle: `E2E Chat ${runId}`,
    partOneText: `P2-E2E-1 ${runId} initial part`,
    partTwoText: `P2-E2E-2 ${runId} safety-net part`,
  };

  // TokenHash is the SHA-256 of a throwaway device token; the raw token is
  // never persisted (and is discarded here, since the device is never used).
  const tokenHash = crypto
    .createHash('sha256')
    .update('bcd_' + crypto.randomBytes(24).toString('hex'))
    .digest('hex');

  const statements = [
    `INSERT INTO "Orgs" ("Id","Name","CreatedAt","UpdatedAt")
     VALUES ('${fx.orgId}', 'E2E-CHAT-ORG-${runId}', now(), now());`,
    `INSERT INTO "Projects" ("Id","Name","OrgId","CreatedAt","UpdatedAt")
     VALUES ('${fx.projectId}', 'E2E-CHAT-${runId}', '${fx.orgId}', now(), now());`,
    `INSERT INTO "Users" ("Id","Login","Email","PasswordHash","IsAdmin","FailedLoginAttempts")
     VALUES ('${fx.userId}', '${fx.login}', '${fx.login}@p2e2e.local', '${PASSWORD_HASH}', false, 0);`,
    `INSERT INTO "ProjectMembers" ("Id","ProjectId","UserId","Role","JoinedAt")
     VALUES ('${crypto.randomUUID()}', '${fx.projectId}', '${fx.userId}', 2, now());`,
    `INSERT INTO "DaemonDevices" ("Id","Name","UserId","Fingerprint","TokenHash","TokenPrefix","ProbeJson","WorkstationJson","DesiredWorkstationJson","DesiredRevision","AppliedRevision","CreatedAt")
     VALUES ('${fx.deviceId}', 'e2e-chat-device-${runId}', '${fx.userId}', 'e2e-chat', '${tokenHash}', 'bcd_', '{}', '{}', '{}', 0, 0, now());`,
    `INSERT INTO "ChatSessions" ("Id","ProjectId","DeviceId","ExternalSessionId","Title","LocalRoot","Status","CreatedAt")
     VALUES ('${fx.sessionId}', '${fx.projectId}', '${fx.deviceId}', '', '${fx.sessionTitle}', '/tmp/e2e', 'Idle', now());`,
    `INSERT INTO "ChatParts" ("Id","SessionId","ProjectId","Role","Kind","Body","SortOrder","CreatedAt")
     VALUES ('${fx.partOneId}', '${fx.sessionId}', '${fx.projectId}', 'assistant', 'text', '${fx.partOneText}', 1, now());`,
  ];

  psql(statements.join('\n'));
  return fx;
}

/** Appends another plain assistant part to the seeded session (direct SQL). */
function seedExtraPart(fx, sortOrder, body) {
  const text = body || fx.partTwoText;
  psql(
    `INSERT INTO "ChatParts" ("Id","SessionId","ProjectId","Role","Kind","Body","SortOrder","CreatedAt")
     VALUES ('${crypto.randomUUID()}', '${fx.sessionId}', '${fx.projectId}', 'assistant', 'text', '${text}', ${sortOrder}, now());`,
  );
}

function cleanupChat(fx) {
  if (!fx) return;
  psql(
    [
      `DELETE FROM "ChatParts" WHERE "SessionId" = '${fx.sessionId}';`,
      `DELETE FROM "ChatSessions" WHERE "Id" = '${fx.sessionId}';`,
      `DELETE FROM "DaemonDevices" WHERE "Id" = '${fx.deviceId}';`,
      `DELETE FROM "ProjectMembers" WHERE "ProjectId" = '${fx.projectId}';`,
      `DELETE FROM "Projects" WHERE "Id" = '${fx.projectId}';`,
      `DELETE FROM "Users" WHERE "Id" = '${fx.userId}';`,
      `DELETE FROM "Orgs" WHERE "Id" = '${fx.orgId}';`,
    ].join('\n'),
  );
}

module.exports = { PASSWORD, seedChat, seedExtraPart, cleanupChat };