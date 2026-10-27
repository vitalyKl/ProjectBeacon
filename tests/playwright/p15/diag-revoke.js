'use strict';
// Seeds a case, revokes membership A via the admin API, then dumps the user's
// remaining ProjectMembers rows directly from the DB. No browser involved.
const fs = require('fs');
const { execFileSync } = require('child_process');
const { seedCase, cleanupAll, readAdminCredentials } = require('./seed');

const BASE = 'http://localhost:5083';

function psql(sql) {
  return execFileSync('docker', ['exec', '-i', 'beacon-postgres', 'psql', '-U', 'beacon', '-d', 'beacon', '-t', '-A'], {
    input: sql, encoding: 'utf8',
  });
}

(async () => {
  const state = seedCase([{ key: 'u1', projects: ['A', 'B'] }]);
  const admin = readAdminCredentials();
  const out = [];
  try {
    const jar = 'C:\\Users\\vtlyk\\AppData\\Local\\Temp\\opencode\\admjar.txt';
    fs.writeFileSync(jar, '');
    const loginArgs = [
      '-s', '-o', 'NUL', '-w', '%{http_code}', '-c', jar,
      '-H', 'Content-Type: application/x-www-form-urlencoded',
      '--data', `login=${encodeURIComponent(admin.login)}&password=${encodeURIComponent(admin.password)}&returnUrl=%2Fdashboard`,
      `${BASE}/v1/auth/cookie-login`,
    ];
    const status = execFileSync('curl.exe', loginArgs, { encoding: 'utf8' });
    out.push(`admin login -> ${status.trim()}`);

    out.push(`before revoke: ${psql(`SELECT p."Name", m."Role", to_char(m."JoinedAt" AT TIME ZONE 'UTC', 'YYYY-MM-DD') FROM "ProjectMembers" m JOIN "Projects" p ON p."Id" = m."ProjectId" WHERE m."UserId" = '${state.users.u1.id}' ORDER BY m."JoinedAt";`).trim()}`);

    const delArgs = ['-s', '-o', 'NUL', '-w', '%{http_code}', '-b', jar, `-X`, 'DELETE', `${BASE}/v1/projects/${state.projectA}/members/${state.users.u1.id}`];
    const del = execFileSync('curl.exe', delArgs, { encoding: 'utf8' });
    out.push(`revoke A -> ${del.trim()}`);

    out.push(`after revoke: ${psql(`SELECT p."Name", m."Role", to_char(m."JoinedAt" AT TIME ZONE 'UTC', 'YYYY-MM-DD') FROM "ProjectMembers" m JOIN "Projects" p ON p."Id" = m."ProjectId" WHERE m."UserId" = '${state.users.u1.id}' ORDER BY m."JoinedAt";`).trim()}`);
    fs.unlinkSync(jar);
  } finally {
    cleanupAll();
  }
  console.log(out.join('\n'));
})().catch((e) => { console.error(e); process.exit(1); });
