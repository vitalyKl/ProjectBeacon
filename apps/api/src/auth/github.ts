export type GithubProfile = {
  id: bigint;
  login: string;
  email: string | null;
  name: string | null;
  avatarUrl: string | null;
};

export type GithubExchangeResult = { ok: true; profile: GithubProfile } | { ok: false };

export async function exchangeGithubCode(
  code: string,
  clientId: string,
  clientSecret: string,
  githubFetch: typeof fetch,
): Promise<GithubExchangeResult> {
  const tokenRes = await githubFetch("https://github.com/login/oauth/access_token", {
    method: "POST",
    headers: {
      Accept: "application/json",
      "Content-Type": "application/json",
    },
    body: JSON.stringify({
      client_id: clientId,
      client_secret: clientSecret,
      code,
    }),
  });

  if (!tokenRes.ok) {
    return { ok: false };
  }

  const tokenBody: unknown = await tokenRes.json();
  const accessToken =
    tokenBody !== null &&
    typeof tokenBody === "object" &&
    "access_token" in tokenBody &&
    typeof tokenBody.access_token === "string"
      ? tokenBody.access_token
      : undefined;
  if (!accessToken) {
    return { ok: false };
  }

  const userRes = await githubFetch("https://api.github.com/user", {
    headers: {
      Accept: "application/vnd.github+json",
      Authorization: `Bearer ${accessToken}`,
      "User-Agent": "projectbeacon",
    },
  });
  if (!userRes.ok) {
    return { ok: false };
  }

  const userBody: unknown = await userRes.json();
  if (userBody === null || typeof userBody !== "object") {
    return { ok: false };
  }

  const record = userBody as Record<string, unknown>;
  if (typeof record["login"] !== "string") {
    return { ok: false };
  }
  const id = parseGithubUserId(record["id"]);
  if (id === undefined) {
    return { ok: false };
  }

  return {
    ok: true,
    profile: {
      id,
      login: record["login"],
      email: typeof record["email"] === "string" ? record["email"] : null,
      name: typeof record["name"] === "string" ? record["name"] : null,
      avatarUrl: typeof record["avatar_url"] === "string" ? record["avatar_url"] : null,
    },
  };
}

export function parseGithubUserId(value: unknown): bigint | undefined {
  if (typeof value === "bigint") {
    return value > 0n ? value : undefined;
  }
  if (typeof value === "number") {
    if (!Number.isInteger(value) || value <= 0) {
      return undefined;
    }
    return BigInt(value);
  }
  if (typeof value === "string" && /^\d+$/.test(value)) {
    try {
      const id = BigInt(value);
      return id > 0n ? id : undefined;
    } catch {
      return undefined;
    }
  }
  return undefined;
}
