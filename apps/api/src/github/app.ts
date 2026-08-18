import { createSign } from "node:crypto";

export type GithubIssueView = {
  id: string;
  number: number;
  title: string;
  body: string;
  state: "open" | "closed";
  html_url: string;
};

export type GithubPullView = {
  id: string;
  number: number;
  title: string;
  body: string;
  state: "open" | "closed";
  html_url: string;
  draft: boolean;
};

function base64urlJson(value: unknown): string {
  return Buffer.from(JSON.stringify(value)).toString("base64url");
}

export function normalizeGithubPrivateKey(value: string): string {
  return value.replace(/\\n/g, "\n");
}

export function signGithubAppJwt(appId: string, privateKey: string, now = Date.now()): string {
  const iat = Math.floor(now / 1000) - 60;
  const exp = iat + 9 * 60;
  const header = base64urlJson({ alg: "RS256", typ: "JWT" });
  const payload = base64urlJson({ iat, exp, iss: appId });
  const signer = createSign("RSA-SHA256");
  signer.update(`${header}.${payload}`);
  signer.end();
  const signature = signer.sign(normalizeGithubPrivateKey(privateKey)).toString("base64url");
  return `${header}.${payload}.${signature}`;
}

export function parseGithubRemote(
  remoteUrl: string | null,
): { owner: string; repo: string } | undefined {
  if (!remoteUrl) {
    return undefined;
  }
  const match = /github\.com[:/]([^/]+)\/([^/]+?)(?:\.git)?$/i.exec(remoteUrl.trim());
  if (!match?.[1] || !match[2]) {
    return undefined;
  }
  return { owner: match[1], repo: match[2] };
}

function asRecord(value: unknown): Record<string, unknown> | undefined {
  if (value === null || typeof value !== "object" || Array.isArray(value)) {
    return undefined;
  }
  return value as Record<string, unknown>;
}

function asPositiveBigInt(value: unknown): bigint | undefined {
  if (typeof value === "bigint" && value > 0n) {
    return value;
  }
  if (typeof value === "number" && Number.isInteger(value) && value > 0) {
    return BigInt(value);
  }
  if (typeof value === "string" && /^\d+$/.test(value)) {
    try {
      const parsed = BigInt(value);
      return parsed > 0n ? parsed : undefined;
    } catch {
      return undefined;
    }
  }
  return undefined;
}

export function presentGithubIssue(value: unknown): GithubIssueView | undefined {
  const record = asRecord(value);
  if (!record || asRecord(record["pull_request"])) {
    return undefined;
  }
  const id = asPositiveBigInt(record["id"]);
  const number = typeof record["number"] === "number" ? record["number"] : Number(record["number"]);
  const title = typeof record["title"] === "string" ? record["title"] : undefined;
  const state =
    record["state"] === "closed" ? "closed" : record["state"] === "open" ? "open" : undefined;
  const htmlUrl = typeof record["html_url"] === "string" ? record["html_url"] : undefined;
  if (!id || !Number.isInteger(number) || number <= 0 || !title || !state || !htmlUrl) {
    return undefined;
  }
  return {
    id: id.toString(),
    number,
    title,
    body: typeof record["body"] === "string" ? record["body"] : "",
    state,
    html_url: htmlUrl,
  };
}

export function presentGithubPull(value: unknown): GithubPullView | undefined {
  const record = asRecord(value);
  if (!record) {
    return undefined;
  }
  const id = asPositiveBigInt(record["id"]);
  const number = typeof record["number"] === "number" ? record["number"] : Number(record["number"]);
  const title = typeof record["title"] === "string" ? record["title"] : undefined;
  const state =
    record["state"] === "closed" ? "closed" : record["state"] === "open" ? "open" : undefined;
  const htmlUrl = typeof record["html_url"] === "string" ? record["html_url"] : undefined;
  if (!id || !Number.isInteger(number) || number <= 0 || !title || !state || !htmlUrl) {
    return undefined;
  }
  return {
    id: id.toString(),
    number,
    title,
    body: typeof record["body"] === "string" ? record["body"] : "",
    state,
    html_url: htmlUrl,
    draft: record["draft"] === true,
  };
}

export async function createInstallationToken(
  installationId: bigint,
  appId: string,
  privateKey: string,
  githubFetch: typeof fetch,
  now = Date.now(),
): Promise<string | undefined> {
  const jwt = signGithubAppJwt(appId, privateKey, now);
  const res = await githubFetch(
    `https://api.github.com/app/installations/${installationId}/access_tokens`,
    {
      method: "POST",
      headers: {
        Accept: "application/vnd.github+json",
        Authorization: `Bearer ${jwt}`,
        "User-Agent": "projectbeacon",
        "X-GitHub-Api-Version": "2022-11-28",
      },
    },
  );
  if (!res.ok) {
    return undefined;
  }
  const body: unknown = await res.json();
  const record = asRecord(body);
  return typeof record?.["token"] === "string" ? record["token"] : undefined;
}

export async function githubApiRequest(
  token: string,
  path: string,
  githubFetch: typeof fetch,
): Promise<{ ok: boolean; status: number; body: unknown }> {
  const res = await githubFetch(`https://api.github.com${path}`, {
    headers: {
      Accept: "application/vnd.github+json",
      Authorization: `Bearer ${token}`,
      "User-Agent": "projectbeacon",
      "X-GitHub-Api-Version": "2022-11-28",
    },
  });
  let parsed: unknown = undefined;
  const text = await res.text();
  if (text.length > 0) {
    try {
      parsed = JSON.parse(text) as unknown;
    } catch {
      parsed = text;
    }
  }
  return { ok: res.ok, status: res.status, body: parsed };
}
