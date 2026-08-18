import { createPrivateKey, createSign } from "node:crypto";

const APP_JWT_TTL_MS = 9 * 60 * 1000;

export type GithubAppConfig = {
  appId: string;
  privateKey: string;
};

export function loadGithubAppConfig(
  env: NodeJS.ProcessEnv = process.env,
): GithubAppConfig | undefined {
  const appId = env["GITHUB_APP_ID"]?.trim();
  const privateKey = env["GITHUB_APP_PRIVATE_KEY"]?.replace(/\\n/g, "\n").trim();
  if (!appId || !privateKey) {
    return undefined;
  }
  return { appId, privateKey };
}

function base64Url(value: string | Buffer): string {
  const buf = typeof value === "string" ? Buffer.from(value) : value;
  return buf.toString("base64url");
}

export function signGithubAppJwt(config: GithubAppConfig, now = Date.now()): string {
  const header = base64Url(JSON.stringify({ alg: "RS256", typ: "JWT" }));
  const payload = base64Url(
    JSON.stringify({
      iat: Math.floor(now / 1000) - 30,
      exp: Math.floor((now + APP_JWT_TTL_MS) / 1000),
      iss: config.appId,
    }),
  );
  const signer = createSign("RSA-SHA256");
  signer.update(`${header}.${payload}`);
  const key = createPrivateKey(config.privateKey);
  return `${header}.${payload}.${signer.sign(key, "base64url")}`;
}

export async function createInstallationToken(
  config: GithubAppConfig,
  installationId: string,
  fetchImpl: typeof fetch = fetch,
): Promise<string> {
  const jwt = signGithubAppJwt(config);
  const response = await fetchImpl(
    `https://api.github.com/app/installations/${installationId}/access_tokens`,
    {
      method: "POST",
      headers: {
        accept: "application/vnd.github+json",
        authorization: `Bearer ${jwt}`,
        "user-agent": "projectbeacon",
        "x-github-api-version": "2022-11-28",
      },
    },
  );
  if (!response.ok) {
    throw new Error(`github installation token failed: ${response.status}`);
  }
  const body = (await response.json()) as { token?: unknown };
  if (typeof body.token !== "string" || body.token.length === 0) {
    throw new Error("github installation token missing");
  }
  return body.token;
}

export function cloneUrlWithToken(remoteUrl: string, token: string): string {
  const parsed = new URL(remoteUrl);
  parsed.username = "x-access-token";
  parsed.password = token;
  if (!parsed.pathname.endsWith(".git")) {
    parsed.pathname = `${parsed.pathname.replace(/\/+$/, "")}.git`;
  }
  return parsed.toString();
}
