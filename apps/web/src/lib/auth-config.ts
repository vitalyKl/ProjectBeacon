export type PublicAuthConfig = {
  githubEnabled: boolean;
  githubClientId: string | null;
};

export function publicAuthConfig(): PublicAuthConfig {
  const clientId = process.env.GITHUB_OAUTH_CLIENT_ID?.trim() || null;
  const githubEnabled = process.env.AUTH_GITHUB === "true" && Boolean(clientId);
  return { githubEnabled, githubClientId: githubEnabled ? clientId : null };
}

export function publicOrigin(): string {
  return (process.env.BEACON_PUBLIC_URL ?? "http://localhost:3000").replace(/\/$/, "");
}

export function githubCallbackUrl(origin = publicOrigin()): string {
  return `${origin}/login/github`;
}
